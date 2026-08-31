import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import {
  chmodSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const ENTRYPOINT = resolve(REPOSITORY_ROOT, 'deploy/containers/migration-entrypoint.sh');

const CLIENT_ID = '8d5c2f1e-0f2b-4a3d-9a1b-2c3d4e5f6071';
const IDENTITY_HEADER = 'e3b0c44298fc1c149afbf4c8996fb924';
const SCOPE = 'https://ossrdbms-aad.database.windows.net/.default';
const ACCESS_TOKEN = `eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.${'a'.repeat(96)}.${'b'.repeat(64)}`;
const AZURE_CONNECTION =
  'Host=vistara.postgres.database.azure.com;Port=5432;Database=vistara;' +
  'Username=vistara_migrate_runtime;SSL Mode=VerifyFull;' +
  'GSS Encryption Mode=Disable;Include Error Detail=false';

// The bundle records how it was invoked and what the pgpass file held while it
// ran, which is the only way to prove the token reached PostgreSQL without ever
// reaching the command line.
const FAKE_BUNDLE = `#!/usr/bin/env bash
set -uo pipefail

{
  printf 'argv:%s\\n' "$*"
  printf 'cmdline:%s\\n' "$(tr '\\0' ' ' < "/proc/$$/cmdline" 2>/dev/null || printf '%s' "$0 $*")"
  connection="\${2:-}"
  passfile="\${connection##*Passfile=}"
  if [ "$passfile" != "$connection" ] && [ -f "$passfile" ]; then
    printf 'passfile-mode:%s\\n' "$(stat -f '%Lp' "$passfile" 2>/dev/null || stat -c '%a' "$passfile")"
    printf 'passfile:%s' "$(cat "$passfile")"
    printf '\\n'
  fi
} >> "$FAKE_BUNDLE_LOG"

exit "\${FAKE_BUNDLE_EXIT:-0}"
`;

function identityResponse(overrides = {}) {
  return {
    access_token: ACCESS_TOKEN,
    expires_on: String(Math.floor(Date.now() / 1000) + 3600),
    resource: 'https://ossrdbms-aad.database.windows.net',
    token_type: 'Bearer',
    client_id: CLIENT_ID,
    ...overrides,
  };
}

async function withIdentityEndpoint(options, body) {
  const requests = [];
  const server = createServer((request, response) => {
    requests.push({ url: request.url, headers: request.headers });
    if (options.status && options.status !== 200) {
      response.writeHead(options.status, { 'content-type': 'application/json' });
      response.end('{"error":"invalid_request"}');
      return;
    }

    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify(options.payload ?? identityResponse()));
  });

  await new Promise((resolveListen) => server.listen(0, '127.0.0.1', resolveListen));
  const { port } = server.address();
  try {
    return await body({ port, requests });
  } finally {
    await new Promise((resolveClose) => server.close(resolveClose));
  }
}

// The fake identity endpoint lives in this process, so the entrypoint has to be
// spawned asynchronously; a blocking spawn would stall the server it calls.
function runEntrypoint(environment) {
  return new Promise((resolveRun, rejectRun) => {
    const child = spawn('bash', [ENTRYPOINT], {
      cwd: REPOSITORY_ROOT,
      env: environment,
    });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk) => {
      stdout += chunk;
    });
    child.stderr.on('data', (chunk) => {
      stderr += chunk;
    });
    child.on('error', rejectRun);
    child.on('close', (status) => resolveRun({ status, stdout, stderr }));
  });
}

async function withWorkspace(body) {
  const root = resolve(HERE, `.scratch-migration-entrypoint-${process.pid}-${randomUUID()}`);
  const bundles = resolve(root, 'bundles');
  const tokens = resolve(root, 'tokens');
  mkdirSync(bundles, { recursive: true });
  mkdirSync(tokens, { recursive: true });
  const log = resolve(root, 'bundle.log');
  writeFileSync(log, '');
  for (const name of ['vistara-migrate-postgres', 'vistara-migrate-sqlite']) {
    const path = resolve(bundles, name);
    writeFileSync(path, FAKE_BUNDLE);
    chmodSync(path, 0o755);
  }

  try {
    return await body({
      root,
      tokens,
      log,
      invocations: () => readFileSync(log, 'utf8'),
      leftoverTokenFiles: () => readdirSync(tokens),
      run(environment = {}) {
        return runEntrypoint({
          PATH: process.env.PATH,
          HOME: root,
          MIGRATION_BUNDLE_DIRECTORY: bundles,
          MIGRATION_TOKEN_DIRECTORY: tokens,
          MIGRATION_IDENTITY_TIMEOUT_SECONDS: '5',
          FAKE_BUNDLE_LOG: log,
          MIGRATION_PROVIDER: 'PostgreSql',
          ConnectionStrings__Vistara: AZURE_CONNECTION,
          ...environment,
        });
      },
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function entraEnvironment(port, overrides = {}) {
  return {
    MIGRATION_MANAGED_IDENTITY_CLIENT_ID: CLIENT_ID,
    MIGRATION_ENTRA_TOKEN_SCOPE: SCOPE,
    IDENTITY_ENDPOINT: `http://127.0.0.1:${port}/msi/token`,
    IDENTITY_HEADER,
    ...overrides,
  };
}

test('password deployments reach the bundle with the connection string unchanged', async () => {
  await withWorkspace(async ({ run, invocations }) => {
    const result = await run({
      ConnectionStrings__Vistara: 'Host=postgres;Database=vistara;Username=v;Password=local',
    });

    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(
      invocations(),
      /argv:--connection Host=postgres;Database=vistara;Username=v;Password=local/,
    );
    assert.doesNotMatch(invocations(), /Passfile=/);
  });
});

test('a missing connection string fails before any bundle runs', async () => {
  await withWorkspace(async ({ run, invocations }) => {
    const result = await run({ ConnectionStrings__Vistara: '' });

    assert.equal(result.status, 64);
    assert.match(result.stderr, /ConnectionStrings__Vistara is required/);
    assert.equal(invocations(), '');
  });
});

test('an unknown provider fails before any bundle runs', async () => {
  await withWorkspace(async ({ run, invocations }) => {
    const result = await run({ MIGRATION_PROVIDER: 'MySql' });

    assert.equal(result.status, 64);
    assert.equal(invocations(), '');
  });
});

test('a managed-identity migration splices the token through a private pgpass file', async () => {
  await withIdentityEndpoint({}, async ({ port, requests }) =>
    withWorkspace(async ({ run, invocations, leftoverTokenFiles }) => {
      const result = await run(entraEnvironment(port));

      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
      const log = invocations();
      assert.match(log, /argv:--connection .*Passfile=/);
      assert.match(log, new RegExp(`passfile:\\*:\\*:\\*:\\*:${ACCESS_TOKEN}`));
      assert.match(log, /passfile-mode:600/);

      const [argv] = log.split('\n');
      assert.ok(!argv.includes(ACCESS_TOKEN), 'the token must never reach the command line');
      assert.ok(
        !log.split('\n')[1].includes(ACCESS_TOKEN),
        'the token must never reach /proc cmdline',
      );
      assert.equal(
        leftoverTokenFiles().length,
        0,
        'the pgpass directory must be removed when the migration finishes',
      );

      const [request] = requests;
      assert.equal(request.headers['x-identity-header'], IDENTITY_HEADER);
      assert.match(request.url, /api-version=2019-08-01/);
      assert.match(
        request.url,
        /resource=https%3A%2F%2Fossrdbms-aad\.database\.windows\.net(&|$)/,
      );
      assert.match(request.url, new RegExp(`client_id=${CLIENT_ID}`));
      assert.ok(!request.url.includes('/.default'), 'the resource drops the /.default suffix');
    }),
  );
});

test('a managed-identity migration prints no secret material', async () => {
  await withIdentityEndpoint({}, async ({ port }) =>
    withWorkspace(async ({ run }) => {
      const result = await run(entraEnvironment(port));

      const output = `${result.stdout}${result.stderr}`;
      assert.ok(!output.includes(ACCESS_TOKEN), 'the token must never be printed');
      assert.ok(!output.includes(IDENTITY_HEADER), 'the identity header must never be printed');
      assert.ok(!output.includes('Passfile='), 'the pgpass path must never be printed');
    }),
  );
});

test('a failing bundle propagates its exit code and still removes the token', async () => {
  await withIdentityEndpoint({}, async ({ port }) =>
    withWorkspace(async ({ run, leftoverTokenFiles }) => {
      const result = await run(entraEnvironment(port, { FAKE_BUNDLE_EXIT: '9' }));

      assert.equal(result.status, 9);
      assert.equal(leftoverTokenFiles().length, 0);
    }),
  );
});

test('a rejected token request fails closed', async () => {
  await withIdentityEndpoint({ status: 400 }, async ({ port }) =>
    withWorkspace(async ({ run, invocations, leftoverTokenFiles }) => {
      const result = await run(entraEnvironment(port));

      assert.equal(result.status, 78);
      assert.match(result.stderr, /rejected the token request/);
      assert.equal(invocations(), '');
      assert.equal(leftoverTokenFiles().length, 0);
    }),
  );
});

test('an unreachable identity endpoint fails closed', async () => {
  await withWorkspace(async ({ run, invocations }) => {
    // Port 1 is reserved and refuses connections on every supported runner.
    const result = await run(entraEnvironment(1));

    assert.equal(result.status, 78);
    assert.equal(invocations(), '');
  });
});

const REJECTED_ENVIRONMENTS = [
  ['a non-GUID managed identity client id', { MIGRATION_MANAGED_IDENTITY_CLIENT_ID: 'api' }, /client id GUID/],
  ['a SQLite provider', { MIGRATION_PROVIDER: 'Sqlite' }, /MIGRATION_PROVIDER=PostgreSql/],
  ['a missing identity endpoint', { IDENTITY_ENDPOINT: '' }, /IDENTITY_ENDPOINT is required/],
  ['a missing identity header', { IDENTITY_HEADER: '' }, /IDENTITY_HEADER is required/],
  [
    'a non-loopback plaintext identity endpoint',
    { IDENTITY_ENDPOINT: 'http://identity.example.com/msi/token' },
    /loopback or link-local/,
  ],
  [
    'an unsupported identity endpoint scheme',
    { IDENTITY_ENDPOINT: 'file:///msi/token' },
    /http or https URL/,
  ],
  [
    'an identity endpoint with a non-numeric port',
    { IDENTITY_ENDPOINT: 'http://127.0.0.1:msi/token' },
    /non-numeric port/,
  ],
  [
    'a plaintext token scope',
    { MIGRATION_ENTRA_TOKEN_SCOPE: 'http://ossrdbms-aad.database.windows.net/.default' },
    /HTTPS scope/,
  ],
  [
    'a token scope without the default suffix',
    { MIGRATION_ENTRA_TOKEN_SCOPE: 'https://ossrdbms-aad.database.windows.net/' },
    /HTTPS scope/,
  ],
  [
    'a connection string that still carries a password',
    { ConnectionStrings__Vistara: `${AZURE_CONNECTION};Password=leaked` },
    /must not contain Password/,
  ],
  [
    'a connection string that already points at a pgpass file',
    { ConnectionStrings__Vistara: `${AZURE_CONNECTION};Passfile=/etc/pgpass` },
    /must not contain Passfile/,
  ],
  [
    'a connection string without full certificate verification',
    {
      ConnectionStrings__Vistara:
        'Host=vistara.postgres.database.azure.com;Database=vistara;Username=v;' +
        'SSL Mode=Require;GSS Encryption Mode=Disable',
    },
    /SSL Mode=VerifyFull/,
  ],
  [
    'a connection string that leaves GSS encryption enabled',
    {
      ConnectionStrings__Vistara:
        'Host=vistara.postgres.database.azure.com;Database=vistara;Username=v;' +
        'SSL Mode=VerifyFull',
    },
    /GSS Encryption Mode=Disable/,
  ],
];

for (const [description, overrides, expected] of REJECTED_ENVIRONMENTS) {
  test(`${description} fails closed`, async () => {
    await withIdentityEndpoint({}, async ({ port }) =>
      withWorkspace(async ({ run, invocations }) => {
        const result = await run(entraEnvironment(port, overrides));

        assert.equal(result.status, 78, `${result.stdout}${result.stderr}`);
        assert.match(result.stderr, expected);
        assert.equal(invocations(), '');
      }),
    );
  });
}

const REJECTED_RESPONSES = [
  ['no access token', identityResponse({ access_token: '' }), /no access token/],
  [
    'a token with pgpass separators',
    identityResponse({ access_token: `head:${'a'.repeat(64)}` }),
    /unexpected format/,
  ],
  [
    'an implausibly short token',
    identityResponse({ access_token: 'abc' }),
    /implausibly short/,
  ],
  ['no expiry', identityResponse({ expires_on: '' }), /numeric expires_on/],
  [
    'a non-numeric expiry',
    identityResponse({ expires_on: '2026-08-31T12:00:00Z' }),
    /numeric expires_on/,
  ],
  [
    'an expiry inside the migration window',
    identityResponse({ expires_on: String(Math.floor(Date.now() / 1000) + 60) }),
    /expires within/,
  ],
];

for (const [description, payload, expected] of REJECTED_RESPONSES) {
  test(`an identity response with ${description} fails closed`, async () => {
    await withIdentityEndpoint({ payload }, async ({ port }) =>
      withWorkspace(async ({ run, invocations, leftoverTokenFiles }) => {
        const result = await run(entraEnvironment(port));

        assert.equal(result.status, 78, `${result.stdout}${result.stderr}`);
        assert.match(result.stderr, expected);
        assert.equal(invocations(), '');
        assert.equal(leftoverTokenFiles().length, 0);
      }),
    );
  });
}

test('the entrypoint stays executable and free of the Azure CLI', () => {
  const script = readFileSync(ENTRYPOINT, 'utf8');

  assert.ok(!/\baz\s/.test(script), 'the entrypoint must not shell out to the Azure CLI');
  assert.ok(!/set\s+-x/.test(script), 'tracing would print the token');
  assert.ok(existsSync(ENTRYPOINT));
});
