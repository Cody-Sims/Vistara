import {
  closeSync,
  openSync,
  readFileSync,
  rmSync,
  mkdirSync,
  writeFileSync,
} from 'node:fs';
import { randomBytes } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import type { FullConfig } from '@playwright/test';
import {
  artifactsDirectory,
  databasePath,
  fixturePath,
  hostAssembly,
  hostProject,
  mediaRoot,
  repositoryRoot,
  statePath,
  webDirectory,
  webDist,
} from './paths.js';

const baseUrl = process.env.VISTARA_E2E_BASE_URL ?? 'http://127.0.0.1:5188';
const pepper = randomBytes(32).toString('base64');

export default async function globalSetup(_config: FullConfig) {
  rmSync(artifactsDirectory, { force: true, recursive: true });
  mkdirSync(artifactsDirectory, { recursive: true });
  mkdirSync(mediaRoot, { recursive: true });
  const encodedFixture = readFileSync(
    new URL('../fixtures/tiny.png.base64', import.meta.url),
    'utf8',
  ).trim();
  writeFileSync(fixturePath, Buffer.from(encodedFixture, 'base64'));

  run('npm', ['--prefix', webDirectory, 'run', 'build']);
  run('dotnet', ['build', hostProject, '-c', 'Release', '--nologo']);
  run('dotnet', [
    hostAssembly,
    'seed',
    '--database',
    databasePath,
    '--media-root',
    mediaRoot,
    '--fixture',
    fixturePath,
    '--state',
    statePath,
    '--pepper',
    pepper,
  ]);

  const stdout = openSync(`${artifactsDirectory}/api.stdout.log`, 'a');
  const stderr = openSync(`${artifactsDirectory}/api.stderr.log`, 'a');
  const runtimeEnvironment = {
    ...process.env,
    Persistence__Provider: 'Sqlite',
    Persistence__ConnectionString: `Data Source=${databasePath}`,
    Media__Storage__Provider: 'Local',
    Media__Storage__Local__RootPath: mediaRoot,
    Media__Imaging__Provider: 'NetVips',
  };
  const api = spawn(
    'dotnet',
    [
      hostAssembly,
      'serve',
      '--urls',
      baseUrl,
      '--contentRoot',
      repositoryRoot,
      '--webroot',
      webDist,
    ],
    {
      cwd: repositoryRoot,
      detached: true,
      env: {
        ...runtimeEnvironment,
        ASPNETCORE_ENVIRONMENT: 'Development',
        Logging__LogLevel__Microsoft: 'Warning',
        Platform__Authentication__ApiKeys__CurrentPepperVersion: 'v1',
        Platform__Authentication__ApiKeys__Peppers__v1: pepper,
        Platform__Authentication__Jwt__Issuers__0__ProfileId: 'e2e',
        Platform__Authentication__Jwt__Issuers__0__Issuer:
          'https://issuer.e2e.invalid',
        Platform__Authentication__Jwt__Issuers__0__Audience: 'vistara-api',
        Platform__Authentication__Jwt__Issuers__0__MetadataAddress:
          'https://issuer.e2e.invalid/.well-known/openid-configuration',
        Platform__Authentication__Jwt__Issuers__0__AllowedAlgorithms__0:
          'RS256',
      },
      stdio: ['ignore', stdout, stderr],
    },
  );
  api.unref();
  closeSync(stdout);
  closeSync(stderr);

  const workerStdout = openSync(`${artifactsDirectory}/worker.stdout.log`, 'a');
  const workerStderr = openSync(`${artifactsDirectory}/worker.stderr.log`, 'a');
  const worker = spawn('dotnet', [hostAssembly, 'worker'], {
    cwd: repositoryRoot,
    detached: true,
    env: {
      ...runtimeEnvironment,
      Worker__InstanceId: 'vistara-e2e-worker',
      Worker__Jobs__MaximumConcurrency: '1',
      Worker__ImagingLimits__ScratchDirectory: `${artifactsDirectory}/scratch`,
      Logging__LogLevel__Microsoft: 'Warning',
    },
    stdio: ['ignore', workerStdout, workerStderr],
  });
  worker.unref();
  closeSync(workerStdout);
  closeSync(workerStderr);

  try {
    await waitForApi(baseUrl);
    await waitForProcess(worker.pid);
    const seeded = JSON.parse(readFileSync(statePath, 'utf8')) as {
      browsers: Record<string, unknown>;
    };
    writeFileSync(
      statePath,
      JSON.stringify(
        {
          baseUrl,
          apiPid: api.pid,
          workerPid: worker.pid,
          browsers: seeded.browsers,
        },
        null,
        2,
      ),
    );
  } catch (error) {
    if (api.pid) {
      try {
        process.kill(api.pid, 'SIGTERM');
      } catch (killError) {
        if ((killError as NodeJS.ErrnoException).code !== 'ESRCH') {
          throw killError;
        }
      }
    }
    if (worker.pid) {
      try {
        process.kill(worker.pid, 'SIGTERM');
      } catch (killError) {
        if ((killError as NodeJS.ErrnoException).code !== 'ESRCH') {
          throw killError;
        }
      }
    }
    throw error;
  }
}

function run(command: string, args: readonly string[]) {
  const result = spawnSync(command, args, {
    cwd: repositoryRoot,
    encoding: 'utf8',
    stdio: 'pipe',
  });
  if (result.status !== 0) {
    throw new Error(
      `${command} ${args.join(' ')} failed.\n${result.stdout}\n${result.stderr}`,
    );
  }
}

async function waitForApi(url: string) {
  let lastError: unknown;
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const health = await fetch(`${url}/health/live`);
      if (!health.ok) {
        throw new Error(`Health returned ${health.status}.`);
      }

      const openApi = await fetch(`${url}/openapi/gallery-v1.json`);
      const document = (await openApi.json()) as {
        paths?: Record<string, unknown>;
        'x-vistara-runtime-gallery-routes'?: boolean;
      };
      if (
        !openApi.ok ||
        document['x-vistara-runtime-gallery-routes'] !== true ||
        operationCount(document.paths ?? {}) !== 35
      ) {
        throw new Error('Runtime OpenAPI did not match the gallery route catalog.');
      }

      return;
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
  }

  throw new Error(`Vistara API did not become ready: ${String(lastError)}`);
}

async function waitForProcess(pid: number | undefined) {
  if (!pid) {
    throw new Error('Worker process did not start.');
  }

  await new Promise((resolve) => setTimeout(resolve, 500));
  try {
    process.kill(pid, 0);
  } catch {
    throw new Error('Worker process exited during startup.');
  }
}

function operationCount(paths: Record<string, unknown>) {
  const methods = new Set(['get', 'post', 'put', 'patch', 'delete']);
  return Object.values(paths).reduce<number>((count, pathItem) => {
    if (pathItem === null || typeof pathItem !== 'object') {
      return count;
    }

    return (
      count +
      Object.keys(pathItem).filter((method) => methods.has(method)).length
    );
  }, 0);
}
