import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import {
  chmodSync,
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
const HOST = 'vistara.postgres.database.azure.com';
const DATABASE = 'vistara';
const USERNAME = 'vistara_migrate_runtime';
const PASSWORD_CONNECTION =
  'Host=postgres;Port=5432;Database=vistara;Username=vistara_migrator;Password=local';

// The bundle records how it was invoked, what the pgpass file held while it ran,
// and which connection-relevant variables survived into its environment. That is
// the only way to prove the token reached PostgreSQL without reaching an
// argument list, and that no operator input reached the effective config.
const FAKE_BUNDLE = `#!/usr/bin/env bash
set -uo pipefail

{
  printf 'argv:%s\\n' "$*"
  printf 'cmdline:%s\\n' "$(tr '\\0' ' ' < "/proc/$$/cmdline" 2>/dev/null || printf '%s' "$0 $*")"
  printf 'inherited:%s\\n' "$(env | grep -E '^(PG|ConnectionStrings__|Persistence__|IDENTITY_)' | cut -d= -f1 | sort | tr '\\n' ',')"
  printf 'home:%s\\n' "\${HOME:-}"
  connection="\${2:-}"
  passfile="\${connection##*Passfile=}"
  passfile="\${passfile%%;*}"
  if [ "$passfile" != "$connection" ] && [ -f "$passfile" ]; then
    printf 'passfile-mode:%s\\n' "$(stat -f '%Lp' "$passfile" 2>/dev/null || stat -c '%a' "$passfile")"
    printf 'passfile:%s\\n' "$(cat "$passfile")"
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
      field: (name) => {
        const line = readFileSync(log, 'utf8')
          .split('\n')
          .find((candidate) => candidate.startsWith(`${name}:`));
        return line === undefined ? undefined : line.slice(name.length + 1);
      },
      leftoverTokenFiles: () => readdirSync(tokens),
      writeFile: (name, contents) => {
        const path = resolve(root, name);
        writeFileSync(path, contents);
        return path;
      },
      run(environment = {}) {
        return runEntrypoint({
          PATH: process.env.PATH,
          HOME: root,
          MIGRATION_BUNDLE_DIRECTORY: bundles,
          MIGRATION_TOKEN_DIRECTORY: tokens,
          MIGRATION_IDENTITY_TIMEOUT_SECONDS: '5',
          FAKE_BUNDLE_LOG: log,
          MIGRATION_PROVIDER: 'PostgreSql',
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
    MIGRATION_POSTGRES_HOST: HOST,
    MIGRATION_POSTGRES_DATABASE: DATABASE,
    MIGRATION_POSTGRES_USERNAME: USERNAME,
    IDENTITY_ENDPOINT: `http://127.0.0.1:${port}/msi/token`,
    IDENTITY_HEADER,
    ...overrides,
  };
}

function expectedConnection({
  tokens,
  host = HOST,
  port = '5432',
  database = DATABASE,
  username = USERNAME,
  rootCertificate = null,
}) {
  const escape = (value) => value.replaceAll(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const suffix = rootCertificate ? `;Root Certificate=${escape(rootCertificate)}` : '';
  return new RegExp(
    `^--connection Host=${escape(host)};Port=${port};Database=${escape(database)};` +
      `Username=${escape(username)};SSL Mode=VerifyFull;GSS Encryption Mode=Disable;` +
      `Include Error Detail=false;` +
      `Passfile=${escape(tokens)}/vistara-migrate\\.[A-Za-z0-9]{8}/pgpass${suffix}$`,
  );
}

test('password deployments reach the bundle with the connection string unchanged', async () => {
  await withWorkspace(async ({ run, field }) => {
    const result = await run({ ConnectionStrings__Vistara: PASSWORD_CONNECTION });

    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.equal(field('argv'), `--connection ${PASSWORD_CONNECTION}`);
    assert.ok(!field('argv').includes('Passfile='));
  });
});

test('password deployments still require a connection string', async () => {
  await withWorkspace(async ({ run, invocations }) => {
    const result = await run({});

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

test('a managed-identity migration builds the whole connection string itself', async () => {
  await withIdentityEndpoint({}, async ({ port, requests }) =>
    withWorkspace(async ({ run, tokens, field, leftoverTokenFiles }) => {
      const result = await run(entraEnvironment(port));

      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
      assert.match(field('argv'), expectedConnection({ tokens }));
      assert.equal(field('passfile'), `*:*:*:*:${ACCESS_TOKEN}`);
      assert.equal(field('passfile-mode'), '600');
      assert.ok(!field('argv').includes(ACCESS_TOKEN), 'the token must never reach the command line');
      assert.ok(!field('cmdline').includes(ACCESS_TOKEN), 'the token must never reach /proc cmdline');
      assert.equal(leftoverTokenFiles().length, 0);

      const [request] = requests;
      assert.equal(request.headers['x-identity-header'], IDENTITY_HEADER);
      assert.match(request.url, /api-version=2019-08-01/);
      assert.match(
        request.url,
        /resource=https%3A%2F%2Fossrdbms-aad\.database\.windows\.net(&|$)/,
      );
      assert.match(request.url, new RegExp(`client_id=${CLIENT_ID}`));
    }),
  );
});

test('a managed-identity migration needs no connection string at all', async () => {
  await withIdentityEndpoint({}, async ({ port }) =>
    withWorkspace(async ({ run, tokens, field }) => {
      const result = await run(entraEnvironment(port));

      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
      assert.match(field('argv'), expectedConnection({ tokens }));
    }),
  );
});

const IGNORED_CONNECTION_STRINGS = [
  ['a password keyword', `${PASSWORD_CONNECTION}`],
  ['the pwd alias', 'Host=evil.example.com;Database=other;User ID=root;pwd=stolen'],
  ['the psw alias', 'Host=evil.example.com;Database=other;Username=root;psw=stolen'],
  [
    'duplicate keywords that would win last',
    'Host=vistara.postgres.database.azure.com;SSL Mode=VerifyFull;SSL Mode=Disable;' +
      'GSS Encryption Mode=Disable;GSS Encryption Mode=Require;Database=vistara;Username=v',
  ],
  [
    'quoted and spaced keywords',
    "Host = evil.example.com ; Database = 'other' ; Username = \"root\" ; Password = 'stolen'",
  ],
  ['a redirected passfile', 'Host=evil.example.com;Database=v;Username=v;Passfile=/etc/pgpass'],
];

for (const [description, supplied] of IGNORED_CONNECTION_STRINGS) {
  test(`a managed-identity migration ignores ${description}`, async () => {
    await withIdentityEndpoint({}, async ({ port }) =>
      withWorkspace(async ({ run, tokens, field }) => {
        const result = await run(
          entraEnvironment(port, {
            ConnectionStrings__Vistara: supplied,
            Persistence__ConnectionString: supplied,
          }),
        );

        assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
        assert.match(field('argv'), expectedConnection({ tokens }));
        assert.ok(!field('argv').includes('stolen'));
        assert.ok(!field('argv').includes('evil.example.com'));
        assert.match(result.stderr, /Ignoring the supplied connection string/);
      }),
    );
  });
}

test('a managed-identity migration strips every inherited connection override', async () => {
  await withIdentityEndpoint({}, async ({ port }) =>
    withWorkspace(async ({ run, tokens, field }) => {
      const result = await run(
        entraEnvironment(port, {
          ConnectionStrings__Vistara: PASSWORD_CONNECTION,
          Persistence__ConnectionString: PASSWORD_CONNECTION,
          PGPASSWORD: 'stolen',
          PGPASSFILE: '/etc/pgpass',
          PGHOST: 'evil.example.com',
          PGPORT: '65000',
          PGDATABASE: 'other',
          PGUSER: 'root',
          PGSSLMODE: 'disable',
          PGGSSENCMODE: 'require',
          PGSSLROOTCERT: '/etc/evil-root.crt',
          PGSERVICE: 'evil',
          PGSERVICEFILE: '/etc/pg_service.conf',
          PGOPTIONS: '-c log_statement=all',
          PGREQUIREAUTH: 'none',
        }),
      );

      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
      assert.match(field('argv'), expectedConnection({ tokens }));
      assert.equal(field('inherited'), '');
      assert.match(field('home'), new RegExp(`^${tokens}/vistara-migrate\\.`));
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

test('an explicit host allowlist admits a non-Azure host', async () => {
  await withIdentityEndpoint({}, async ({ port }) =>
    withWorkspace(async ({ run, tokens, field }) => {
      const result = await run(
        entraEnvironment(port, {
          MIGRATION_POSTGRES_HOST: 'vistara-db.internal.example',
          MIGRATION_POSTGRES_HOST_ALLOWLIST: 'other.example vistara-db.internal.example',
        }),
      );

      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
      assert.match(field('argv'), expectedConnection({ tokens, host: 'vistara-db.internal.example' }));
    }),
  );
});

test('a sovereign-cloud suffix policy admits that cloud only', async () => {
  await withIdentityEndpoint({}, async ({ port }) =>
    withWorkspace(async ({ run, tokens, field }) => {
      const result = await run(
        entraEnvironment(port, {
          MIGRATION_POSTGRES_HOST: 'vistara.postgres.database.usgovcloudapi.net',
          MIGRATION_POSTGRES_HOST_SUFFIXES: '.postgres.database.usgovcloudapi.net',
          MIGRATION_POSTGRES_PORT: '5433',
        }),
      );

      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
      assert.match(
        field('argv'),
        expectedConnection({
          tokens,
          host: 'vistara.postgres.database.usgovcloudapi.net',
          port: '5433',
        }),
      );
    }),
  );
});

test('a readable root certificate is appended to the constructed string', async () => {
  await withIdentityEndpoint({}, async ({ port }) =>
    withWorkspace(async ({ run, tokens, field, writeFile }) => {
      const certificate = writeFile('root.crt', '-----BEGIN CERTIFICATE-----\n');
      const result = await run(
        entraEnvironment(port, { MIGRATION_POSTGRES_ROOT_CERTIFICATE: certificate }),
      );

      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
      assert.match(field('argv'), expectedConnection({ tokens, rootCertificate: certificate }));
    }),
  );
});

const REJECTED_ENVIRONMENTS = [
  ['a non-GUID managed identity client id', { MIGRATION_MANAGED_IDENTITY_CLIENT_ID: 'api' }, /client id GUID/],
  ['a SQLite provider', { MIGRATION_PROVIDER: 'Sqlite' }, /MIGRATION_PROVIDER=PostgreSql/],
  ['a missing host', { MIGRATION_POSTGRES_HOST: '' }, /MIGRATION_POSTGRES_HOST is required/],
  [
    'a host outside the Azure suffix policy',
    { MIGRATION_POSTGRES_HOST: 'evil.example.com' },
    /must be an Azure Database for PostgreSQL host/,
  ],
  [
    'a host carrying connection keywords',
    { MIGRATION_POSTGRES_HOST: 'vistara.postgres.database.azure.com;Password=stolen' },
    /MIGRATION_POSTGRES_HOST contains characters/,
  ],
  [
    'a host with a trailing dot',
    { MIGRATION_POSTGRES_HOST: 'vistara.postgres.database.azure.com.' },
    /MIGRATION_POSTGRES_HOST contains characters/,
  ],
  [
    'an allowlist entry that is not a hostname',
    {
      MIGRATION_POSTGRES_HOST: 'vistara-db.internal.example',
      MIGRATION_POSTGRES_HOST_ALLOWLIST: 'vistara-db.internal.example;Password=stolen',
    },
    /MIGRATION_POSTGRES_HOST_ALLOWLIST contains characters/,
  ],
  [
    'a suffix policy entry without a leading dot',
    { MIGRATION_POSTGRES_HOST_SUFFIXES: 'postgres.database.azure.com' },
    /must start with a dot/,
  ],
  ['a missing database', { MIGRATION_POSTGRES_DATABASE: '' }, /MIGRATION_POSTGRES_DATABASE is required/],
  [
    'a database carrying connection keywords',
    { MIGRATION_POSTGRES_DATABASE: 'vistara;SSL Mode=Disable' },
    /MIGRATION_POSTGRES_DATABASE contains characters/,
  ],
  [
    'a database with a control character',
    { MIGRATION_POSTGRES_DATABASE: 'vistara\nPassword=stolen' },
    /MIGRATION_POSTGRES_DATABASE must not contain control characters/,
  ],
  [
    'an over-long database name',
    { MIGRATION_POSTGRES_DATABASE: 'v'.repeat(64) },
    /MIGRATION_POSTGRES_DATABASE must be at most 63/,
  ],
  ['a missing username', { MIGRATION_POSTGRES_USERNAME: '' }, /MIGRATION_POSTGRES_USERNAME is required/],
  [
    'a username carrying connection keywords',
    { MIGRATION_POSTGRES_USERNAME: 'v;Password=stolen' },
    /MIGRATION_POSTGRES_USERNAME contains characters/,
  ],
  [
    'a username that starts with a delimiter',
    { MIGRATION_POSTGRES_USERNAME: '-v' },
    /MIGRATION_POSTGRES_USERNAME contains characters/,
  ],
  ['a zero port', { MIGRATION_POSTGRES_PORT: '0' }, /MIGRATION_POSTGRES_PORT contains characters/],
  [
    'a padded port',
    { MIGRATION_POSTGRES_PORT: '05432' },
    /MIGRATION_POSTGRES_PORT contains characters/,
  ],
  [
    'a non-numeric port',
    { MIGRATION_POSTGRES_PORT: '5432;Password=stolen' },
    /MIGRATION_POSTGRES_PORT must be at most 5 characters/,
  ],
  [
    'a port above the TCP range',
    { MIGRATION_POSTGRES_PORT: '65536' },
    /MIGRATION_POSTGRES_PORT must be between 1 and 65535/,
  ],
  [
    'a relative root certificate path',
    { MIGRATION_POSTGRES_ROOT_CERTIFICATE: 'root.crt' },
    /MIGRATION_POSTGRES_ROOT_CERTIFICATE contains characters/,
  ],
  [
    'a missing root certificate file',
    { MIGRATION_POSTGRES_ROOT_CERTIFICATE: '/nonexistent/root.crt' },
    /MIGRATION_POSTGRES_ROOT_CERTIFICATE is not a readable file/,
  ],
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
];

for (const [description, overrides, expected] of REJECTED_ENVIRONMENTS) {
  test(`${description} fails closed`, async () => {
    await withIdentityEndpoint({}, async ({ port }) =>
      withWorkspace(async ({ run, invocations, leftoverTokenFiles }) => {
        const result = await run(entraEnvironment(port, overrides));

        assert.equal(result.status, 78, `${result.stdout}${result.stderr}`);
        assert.match(result.stderr, expected);
        assert.equal(invocations(), '');
        assert.equal(leftoverTokenFiles().length, 0);
      }),
    );
  });
}

test('an unreachable identity endpoint fails closed', async () => {
  await withWorkspace(async ({ run, invocations }) => {
    // Port 1 is reserved and refuses connections on every supported runner.
    const result = await run(entraEnvironment(1));

    assert.equal(result.status, 78);
    assert.equal(invocations(), '');
  });
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

test('a failing bundle propagates its exit code and still removes the token', async () => {
  await withIdentityEndpoint({}, async ({ port }) =>
    withWorkspace(async ({ run, leftoverTokenFiles }) => {
      const result = await run(entraEnvironment(port, { FAKE_BUNDLE_EXIT: '9' }));

      assert.equal(result.status, 9);
      assert.equal(leftoverTokenFiles().length, 0);
    }),
  );
});

const REJECTED_RESPONSES = [
  ['no access token', identityResponse({ access_token: '' }), /no access token/],
  [
    'a token with pgpass separators',
    identityResponse({ access_token: `head:${'a'.repeat(64)}` }),
    /unexpected format/,
  ],
  ['an implausibly short token', identityResponse({ access_token: 'abc' }), /implausibly short/],
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

test('the frozen deployment contract stays documented for the Bicep handoff', () => {
  const script = readFileSync(ENTRYPOINT, 'utf8');

  for (const key of [
    'MIGRATION_PROVIDER',
    'MIGRATION_MANAGED_IDENTITY_CLIENT_ID',
    'MIGRATION_POSTGRES_HOST',
    'MIGRATION_POSTGRES_PORT',
    'MIGRATION_POSTGRES_DATABASE',
    'MIGRATION_POSTGRES_USERNAME',
    'MIGRATION_POSTGRES_HOST_SUFFIXES',
    'MIGRATION_POSTGRES_HOST_ALLOWLIST',
    'MIGRATION_POSTGRES_ROOT_CERTIFICATE',
    'MIGRATION_ENTRA_TOKEN_SCOPE',
    'IDENTITY_ENDPOINT',
    'IDENTITY_HEADER',
  ]) {
    assert.ok(
      script.includes(`#   ${key}`) || script.includes(`#   ${key} `),
      `${key} must stay in the documented deployment contract`,
    );
  }

  assert.ok(!/\baz\s/.test(script), 'the entrypoint must not shell out to the Azure CLI');
  assert.ok(!/set\s+-x/.test(script), 'tracing would print the token');
});
