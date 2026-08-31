import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import { parse } from 'yaml';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const DEPLOY_DIRECTORY = resolve(REPOSITORY_ROOT, 'deploy');

// The topologies the deployment-gates workflow validates, in the same order.
const TOPOLOGIES = ['development', 'starter', 'postgres'];
const RESOURCE_SECTIONS = ['services', 'volumes', 'networks', 'secrets', 'configs', 'models'];

const ENVIRONMENT = [
  'VISTARA_IMAGE_TAG=topology-test',
  'VISTARA_BIND_ADDRESS=127.0.0.1',
  'VISTARA_HTTP_PORT=8080',
  'VISTARA_PUBLIC_HOST=vistara.example.invalid',
  'VISTARA_API_PEPPER=topology-test-pepper',
  'VISTARA_OIDC_PROFILE=local',
  'VISTARA_OIDC_ISSUER=https://issuer.example.invalid',
  'VISTARA_OIDC_AUDIENCE=vistara-api',
  'VISTARA_OIDC_METADATA_ADDRESS=https://issuer.example.invalid/.well-known/openid-configuration',
  'VISTARA_POSTGRES_BOOTSTRAP_PASSWORD=topology-test-bootstrap',
  'VISTARA_MIGRATOR_DB_PASSWORD=topology-test-migrator',
  'VISTARA_API_DB_PASSWORD=topology-test-api',
  'VISTARA_WORKER_DB_PASSWORD=topology-test-worker',
  'VISTARA_MINIO_ROOT_USER=vistara-admin',
  'VISTARA_MINIO_ROOT_PASSWORD=topology-test-minio-root',
  'VISTARA_S3_ACCESS_KEY=vistara-app-topology-test',
  'VISTARA_S3_SECRET_KEY=topology-test-minio-secret',
  '',
].join('\n');

function topologyPath(name) {
  return resolve(DEPLOY_DIRECTORY, `compose.${name}.yml`);
}

function readModel(path) {
  // Compose merge tags such as `!reset` are unknown to a plain YAML parser; the
  // tagged value itself is still returned, which is all this check needs.
  return parse(readFileSync(path, 'utf8'), { logLevel: 'silent' }) ?? {};
}

function includedPaths(model, containingFile) {
  const entries = Array.isArray(model.include) ? model.include : [];
  return entries.map((entry) => {
    const raw = typeof entry === 'string' ? entry : entry?.path;
    const paths = Array.isArray(raw) ? raw : [raw];
    return paths
      .filter((candidate) => typeof candidate === 'string')
      .map((candidate) => resolve(dirname(containingFile), candidate));
  });
}

/**
 * Names each `include:` entry contributes to the importing file. An entry with a
 * list of paths is merged into a single imported model before it reaches the
 * importing file, so those files never conflict with one another.
 */
function importedResources(path, seen = new Set()) {
  const imported = new Map(RESOURCE_SECTIONS.map((section) => [section, new Map()]));
  if (seen.has(path)) {
    return imported;
  }
  seen.add(path);

  for (const entry of includedPaths(readModel(path), path)) {
    for (const included of entry) {
      const model = readModel(included);
      for (const section of RESOURCE_SECTIONS) {
        const defined = model[section];
        if (defined && typeof defined === 'object') {
          for (const [name, definition] of Object.entries(defined)) {
            imported.get(section).set(name, definition);
          }
        }
      }
      for (const [section, nested] of importedResources(included, seen)) {
        for (const [name, definition] of nested) {
          imported.get(section).set(name, definition);
        }
      }
    }
  }

  return imported;
}

function composeFiles() {
  return readdirSync(DEPLOY_DIRECTORY)
    .filter((entry) => entry.startsWith('compose.') && entry.endsWith('.yml'))
    .map((entry) => resolve(DEPLOY_DIRECTORY, entry));
}

function dockerCompose(args, options = {}) {
  return spawnSync('docker', ['compose', ...args], {
    cwd: REPOSITORY_ROOT,
    encoding: 'utf8',
    ...options,
  });
}

const dockerComposeAvailable = dockerCompose(['version']).status === 0;
const skipWithoutDocker = dockerComposeAvailable
  ? false
  : 'docker compose is not available on this machine';

function withEnvironmentFile(body) {
  const root = resolve(HERE, `.scratch-topology-${process.pid}-${randomUUID()}`);
  mkdirSync(root, { recursive: true });
  const envFile = resolve(root, 'topology.env');
  writeFileSync(envFile, ENVIRONMENT, { mode: 0o600 });
  try {
    return body(envFile);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('no Compose file redefines a resource it imports with include', () => {
  for (const path of composeFiles()) {
    const model = readModel(path);
    const imported = importedResources(path);

    for (const section of RESOURCE_SECTIONS) {
      const defined = model[section];
      if (!defined || typeof defined !== 'object') {
        continue;
      }

      for (const [name, definition] of Object.entries(defined)) {
        const conflict = imported.get(section).get(name);
        // Compose only tolerates a redefinition that is byte-for-byte the
        // imported one; anything else fails as
        // "<section>.<name> conflicts with imported resource".
        assert.ok(
          conflict === undefined || isDeepStrictEqual(conflict, definition),
          `${path.slice(REPOSITORY_ROOT.length + 1)} redefines ${section}.${name}, which it also ` +
            'imports with include. Move the override into a file listed after the base in the ' +
            'include path list instead.',
        );
      }
    }
  }
});

test('every Compose file is a validated topology or imported by one', () => {
  const topologies = new Set(TOPOLOGIES.map((name) => topologyPath(name)));
  const reachable = new Set(topologies);
  for (const topology of topologies) {
    for (const entry of includedPaths(readModel(topology), topology)) {
      for (const included of entry) {
        reachable.add(included);
      }
    }
  }

  for (const path of composeFiles()) {
    assert.ok(
      reachable.has(path),
      `${path.slice(REPOSITORY_ROOT.length + 1)} is neither validated by the deployment gate nor ` +
        'imported by a topology that is',
    );
  }
});

test('the deployment gate validates every topology in the repository', () => {
  const workflow = readFileSync(
    resolve(REPOSITORY_ROOT, '.github/workflows/deployment-gates.yml'),
    'utf8',
  );
  assert.match(workflow, /for file in development starter postgres; do/);
  assert.match(workflow, /docker compose --env-file deploy\/\.env \\\n\s+--file "deploy\/compose\.\$file\.yml" config --quiet/);
});

test(
  'development, starter, and postgres topologies render with docker compose config',
  { skip: skipWithoutDocker },
  () => {
    withEnvironmentFile((envFile) => {
      for (const name of TOPOLOGIES) {
        const result = dockerCompose([
          '--env-file',
          envFile,
          '--file',
          `deploy/compose.${name}.yml`,
          'config',
          '--quiet',
        ]);
        assert.equal(
          result.status,
          0,
          `deploy/compose.${name}.yml did not render: ${result.stdout}${result.stderr}`,
        );
      }
    });
  },
);

test(
  'the development topology renders the PostgreSQL base with its development overrides',
  { skip: skipWithoutDocker },
  () => {
    withEnvironmentFile((envFile) => {
      const result = dockerCompose([
        '--env-file',
        envFile,
        '--file',
        'deploy/compose.development.yml',
        'config',
      ]);
      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);

      const rendered = parse(result.stdout, { logLevel: 'silent' });
      assert.equal(rendered.name, 'vistara-development');
      assert.deepEqual(Object.keys(rendered.services).sort(), [
        'api',
        'migrate',
        'minio',
        'minio-bootstrap',
        'otel-collector',
        'postgres',
        'proxy',
        'web',
        'worker',
      ]);

      // Imported from compose.postgres.yml.
      assert.equal(rendered.services.api.environment.Persistence__Provider, 'PostgreSql');
      assert.equal(
        rendered.services.api.depends_on.migrate.condition,
        'service_completed_successfully',
      );
      // Applied by compose.development.override.yml.
      assert.equal(rendered.services.api.environment.ASPNETCORE_ENVIRONMENT, 'Development');
      assert.equal(rendered.services.worker.environment.Worker__InstanceId, 'development-worker');
      assert.equal(rendered.services.proxy.depends_on.web.condition, 'service_healthy');
      assert.ok(
        rendered.services.proxy.volumes.some((volume) =>
          volume.source.endsWith('deploy/nginx/development.conf'),
        ),
        'the development proxy must mount the development Nginx configuration',
      );
      // Security posture the overrides must not drop.
      assert.equal(rendered.services.api.read_only, true);
      assert.equal(rendered.services.api.user, '1654:1654');
      assert.deepEqual(rendered.services.api.cap_drop, ['ALL']);
      assert.equal(rendered.services.proxy.networks.backend.ipv4_address, '172.30.0.10');
      assert.equal(rendered.networks.backend.internal, true);
    });
  },
);
