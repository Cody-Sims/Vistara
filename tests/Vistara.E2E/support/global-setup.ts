import {
  closeSync,
  existsSync,
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
  apiCertificatePath,
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
import {
  identityProviderArguments,
  oidcApiCertificatePath,
  oidcApiEnvironment,
  oidcCertificatePath,
  oidcEnvironment,
  oidcMediaRoot,
  oidcRoutes,
  oidcSeedArguments,
} from './oidc.js';
import { suiteRateLimits } from './deployment.js';
import {
  isSuccess,
  pinnedGet,
  pinnedGetJson,
  readPinnedCertificate,
} from './tls.js';

/**
 * Everything this run serves is served over TLS on the loopback interface.
 * Vistara's session cookie and hosted sign-in login handle are `__Host-`
 * cookies, and WebKit declines to keep a `Secure` cookie delivered over
 * `http://127.0.0.1`, so a plain-HTTP harness could only ever sign in on
 * Chromium and Gecko. Each host generates its own certificate for the run and
 * publishes the public half into the ignored artifacts folder; every probe
 * below pins exactly that certificate.
 */
const baseUrl = process.env.VISTARA_E2E_BASE_URL ?? 'https://127.0.0.1:5188';
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

  mkdirSync(oidcMediaRoot, { recursive: true });
  run('dotnet', [hostAssembly, ...oidcSeedArguments()]);

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
        VISTARA_E2E_SERVER_CERTIFICATE: apiCertificatePath,
        ...suiteRateLimits,
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

  const started: (number | undefined)[] = [api.pid, worker.pid];
  try {
    await waitForApi(baseUrl);
    await waitForProcess(worker.pid);

    // The stub provider writes the certificate the hosted sign-in API is told
    // to trust, so it has to be listening before that API starts.
    const identityProvider = spawnHost('identity-provider', [
      hostAssembly,
      ...identityProviderArguments(),
    ]);
    started.push(identityProvider.pid);
    await waitForIdentityProvider();

    const oidcApi = spawnHost(
      'oidc-api',
      [
        hostAssembly,
        'serve',
        '--urls',
        oidcEnvironment.baseUrl,
        '--contentRoot',
        repositoryRoot,
        '--webroot',
        webDist,
      ],
      oidcApiEnvironment(),
    );
    started.push(oidcApi.pid);
    await waitForHostedSignIn();

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
          oidc: {
            ...oidcEnvironment,
            identityProviderUrl: oidcEnvironment.identityProviderUrl,
            apiPid: oidcApi.pid,
            identityProviderPid: identityProvider.pid,
          },
        },
        null,
        2,
      ),
    );
  } catch (error) {
    for (const pid of started) {
      if (!pid) {
        continue;
      }

      try {
        process.kill(pid, 'SIGTERM');
      } catch (killError) {
        if ((killError as NodeJS.ErrnoException).code !== 'ESRCH') {
          throw killError;
        }
      }
    }

    throw error;
  }
}

/** Starts one detached host process with its output kept in the artifacts. */
function spawnHost(
  name: string,
  args: readonly string[],
  environment: Record<string, string> = {},
) {
  const stdout = openSync(`${artifactsDirectory}/${name}.stdout.log`, 'a');
  const stderr = openSync(`${artifactsDirectory}/${name}.stderr.log`, 'a');
  const child = spawn('dotnet', [...args], {
    cwd: repositoryRoot,
    detached: true,
    env: { ...process.env, ...environment },
    stdio: ['ignore', stdout, stderr],
  });
  child.unref();
  closeSync(stdout);
  closeSync(stderr);
  return child;
}

/**
 * Waits for the stub provider to be answering over TLS and to have published
 * the certificate the API pins. The probe pins that same certificate, so a
 * provider presenting anything else fails the wait rather than passing it.
 */
async function waitForIdentityProvider() {
  let lastError: unknown;
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const { status } = await pinnedGet(
        `${oidcEnvironment.identityProviderUrl}/health`,
        pinnedCertificate(oidcCertificatePath),
      );
      if (!isSuccess(status)) {
        throw new Error(`The stub identity provider answered ${status}.`);
      }

      return;
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
  }

  throw new Error(
    `The stub identity provider did not become ready: ${String(lastError)}`,
  );
}

/**
 * Waits for the hosted sign-in API, and only accepts it once the anonymous
 * setup surface publishes the provider a browser is meant to start at. A
 * deployment that composed the routes without publishing the provider would
 * otherwise look healthy while the suite had nothing to click.
 */
async function waitForHostedSignIn() {
  let lastError: unknown;
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const { status, body } = await pinnedGetJson<{
        signInProviders?: { id?: string; startUrl?: string }[];
      }>(
        `${oidcEnvironment.baseUrl}/api/v1/setup`,
        pinnedCertificate(oidcApiCertificatePath),
      );
      const provider = body.signInProviders?.find(
        (candidate) => candidate.id === oidcEnvironment.providerId,
      );
      if (!isSuccess(status) || provider?.startUrl !== oidcRoutes.startPath) {
        throw new Error('Hosted sign-in published no start URL.');
      }

      return;
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
  }

  throw new Error(
    `The hosted sign-in API did not become ready: ${String(lastError)}`,
  );
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
      const certificate = pinnedCertificate(apiCertificatePath);
      const health = await pinnedGet(`${url}/health/live`, certificate);
      if (!isSuccess(health.status)) {
        throw new Error(`Health returned ${health.status}.`);
      }

      const openApi = await pinnedGetJson<{
        paths?: Record<string, unknown>;
        'x-vistara-runtime-gallery-routes'?: boolean;
      }>(`${url}/openapi/gallery-v1.json`, certificate);
      if (
        !isSuccess(openApi.status) ||
        openApi.body['x-vistara-runtime-gallery-routes'] !== true ||
        operationCount(openApi.body.paths ?? {}) !== 35
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

/**
 * The certificate a host published for this run. It appears the moment that
 * host starts, so a missing file is one more reason to keep waiting rather
 * than a reason to trust something else.
 */
function pinnedCertificate(certificatePath: string) {
  if (!existsSync(certificatePath)) {
    throw new Error(`${certificatePath} has not been published yet.`);
  }

  return readPinnedCertificate(certificatePath);
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
