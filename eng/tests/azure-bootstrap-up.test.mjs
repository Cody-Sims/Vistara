// `deploy/azure/up.sh` — behavior gate.
//
// Every external command is a recording stand-in (see
// `azure-bootstrap-harness.mjs`), so these run the real scripts end to end
// without an Azure subscription and without creating anything billable.

import assert from 'node:assert/strict';
import test from 'node:test';

import {
  ACCESS_TOKEN_MARKER,
  API_DIGEST,
  APP_CLIENT_ID,
  API_PRINCIPAL_ID,
  MIGRATION_DIGEST,
  PEPPER_MARKER,
  SERVICE_API_URI,
  SIGNED_IN_OBJECT_ID,
  TENANT_ID,
  UP_SCRIPT,
  WORKER_DIGEST,
  createSandbox,
  run,
  upArguments,
} from './azure-bootstrap-harness.mjs';

/** Index of the first call to `tool` whose arguments contain every needle. */
function indexOfCall(calls, tool, ...needles) {
  return calls.findIndex(
    (call) => call[0] === tool && needles.every((needle) => call.join(' ').includes(needle)),
  );
}

function assertOrdered(calls, first, second, message) {
  assert.notEqual(first, -1, `${message}: the earlier call never happened`);
  assert.notEqual(second, -1, `${message}: the later call never happened`);
  assert.ok(first < second, message);
}

function withSandbox(name, body, options) {
  const sandbox = createSandbox(name, options);
  try {
    return body(sandbox);
  } finally {
    sandbox.cleanup();
  }
}

test('a first run provisions twice, registers the app, migrates, and lands on the sign-in URL', () => {
  withSandbox('happy-path', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);

    const calls = sandbox.calls();
    const provisions = calls.filter((call) => call[0] === 'azd' && call[1] === 'provision');
    assert.equal(provisions.length, 2, 'the deployment takes exactly two provisioning passes');

    const environment = sandbox.environment('eval');
    assert.equal(environment.VISTARA_DEPLOY_APPLICATIONS, 'true');
    assert.equal(environment.VISTARA_APPLICATION_CLIENT_ID, APP_CLIENT_ID);
    assert.equal(
      environment.VISTARA_API_KEY_PEPPER_SECRET_URI,
      'https://kv-vistara-abcdefghijk.vault.azure.net/secrets/api-key-pepper',
    );
    assert.equal(environment.VISTARA_MIGRATION_COMPLETED_DIGEST, MIGRATION_DIGEST);

    // The dependency chain: registration, then verification, then the pepper,
    // then the database roles, then the migration, then the applications.
    const registration = indexOfCall(calls, 'az', 'rest', 'POST');
    const verification = indexOfCall(calls, 'az', 'federated-credential', 'list');
    const pepper = indexOfCall(calls, 'az', 'keyvault', 'secret', 'set');
    const roles = indexOfCall(calls, 'psql');
    const migration = indexOfCall(calls, 'az', 'containerapp', 'job', 'start');
    const health = indexOfCall(calls, 'curl', '/health/ready');

    assertOrdered(calls, registration, verification, 'the registration is verified after it exists');
    assertOrdered(calls, verification, pepper, 'the pepper is written after verification');
    assertOrdered(calls, pepper, roles, 'database roles come after the pepper');
    assertOrdered(calls, roles, migration, 'the migration runs after the roles exist');
    assertOrdered(calls, migration, health, 'health is only checked after the migration');

    assert.match(result.stdout, /https:\/\/.*\/login\n?$/, 'the sign-in URL is the last thing on stdout');
    assert.ok(result.output.includes(`${SERVICE_API_URI}/login`));
  });
});

test('the browser is opened at the sign-in URL unless --no-open is passed', () => {
  withSandbox('browser-open', (sandbox) => {
    const withoutNoOpen = upArguments().filter((argument) => argument !== '--no-open');
    const result = run(sandbox, UP_SCRIPT, withoutNoOpen);
    assert.equal(result.status, 0, result.output);

    const opens = sandbox.callsFor('open');
    assert.equal(opens.length, 1, 'exactly one browser launch');
    assert.equal(opens[0][1], `${SERVICE_API_URI}/login`);
  });

  withSandbox('browser-suppressed', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    assert.equal(sandbox.callsFor('open').length, 0, '--no-open launches nothing');
  });
});

test('the local first-owner form is offered when the API advertises no Entra provider', () => {
  withSandbox('setup-fallback', (sandbox) => {
    sandbox.state('setup_body', '{"available":true,"signInProviders":[]}');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    assert.ok(result.output.includes(`${SERVICE_API_URI}/setup`));
  });
});

test('only digests reach the template, and a tag is refused', () => {
  withSandbox('digest-required', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments(['--api-digest', 'v1.4.0']));
    assert.equal(result.status, 64);
    assert.match(result.output, /must be digests/);
    assert.equal(sandbox.callsFor('azd').filter((call) => call[1] === 'provision').length, 0);
  });

  withSandbox('digest-resolved', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    const environment = sandbox.environment('eval');
    assert.equal(environment.VISTARA_API_IMAGE, `ghcr.io/cody-sims/vistara-api@${API_DIGEST}`);
    assert.equal(environment.VISTARA_WORKER_IMAGE, `ghcr.io/cody-sims/vistara-worker@${WORKER_DIGEST}`);
    assert.equal(
      environment.VISTARA_MIGRATION_IMAGE,
      `ghcr.io/cody-sims/vistara-migrations@${MIGRATION_DIGEST}`,
    );
    for (const [key, value] of Object.entries(environment)) {
      if (key.endsWith('_IMAGE')) {
        assert.match(value, /@sha256:[0-9a-f]{64}$/, `${key} must pin a digest`);
      }
    }
  });
});

test('an unresolvable image digest fails before anything is created', () => {
  withSandbox('digest-unresolvable', (sandbox) => {
    sandbox.touch('registry_unavailable');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 64);
    assert.match(result.output, /could not resolve an immutable digest/);
    assert.equal(sandbox.callsFor('azd').filter((call) => call[1] === 'provision').length, 0);
  });
});

test('the latest release tag is resolved when none is given', () => {
  withSandbox('release-lookup', (sandbox) => {
    const withoutRelease = [];
    const original = upArguments();
    for (let index = 0; index < original.length; index += 1) {
      if (original[index] === '--release') {
        index += 1;
        continue;
      }
      withoutRelease.push(original[index]);
    }
    const result = run(sandbox, UP_SCRIPT, withoutRelease);
    assert.equal(result.status, 0, result.output);
    const lookup = sandbox
      .callsFor('curl')
      .find((call) => call.join(' ').includes('/releases/latest'));
    assert.ok(lookup, 'the release redirect is followed to find the tag');
    const manifest = sandbox
      .callsFor('curl')
      .find((call) => call.join(' ').includes('/manifests/'));
    assert.ok(manifest.join(' ').includes('/manifests/v1.4.0'), 'digests come from that tag');
  });
});

test('the budget start date is written once and never rebased', () => {
  withSandbox('budget-first-run', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    assert.match(sandbox.environment('eval').VISTARA_BUDGET_START_DATE, /^\d{4}-\d{2}-01$/);
  });

  withSandbox('budget-preserved', (sandbox) => {
    sandbox.state('current_env_name', 'eval');
    sandbox.state('env-eval.env', 'AZURE_ENV_NAME=eval\nVISTARA_BUDGET_START_DATE=2026-01-01\n');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    assert.equal(sandbox.environment('eval').VISTARA_BUDGET_START_DATE, '2026-01-01');
    assert.match(result.output, /keeping the existing budget start date 2026-01-01/);
  });
});

test('a rerun resumes instead of repeating completed work', () => {
  withSandbox('resume', (sandbox) => {
    const first = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(first.status, 0, first.output);

    const callsBefore = sandbox.calls().length;
    sandbox.state('calls.log', '');
    sandbox.state('provision_count', '0');

    const second = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(second.status, 0, second.output);
    assert.ok(callsBefore > 0);

    const calls = sandbox.calls();
    assert.equal(
      calls.filter((call) => call[0] === 'az' && call[1] === 'containerapp' && call[3] === 'start').length,
      0,
      'a completed migration is not run again',
    );
    assert.equal(
      calls.filter((call) => call[0] === 'psql').length,
      0,
      'database roles that are already in place are left alone',
    );
    assert.equal(
      calls.filter((call) => call.join(' ').includes('firewall-rule create')).length,
      0,
      'the firewall is not reopened for work that is already done',
    );
    assert.equal(
      calls.filter((call) => call.join(' ').includes('keyvault secret set')).length,
      0,
      'the pepper is never rotated by a rerun',
    );
    assert.equal(
      calls.filter((call) => call[0] === 'az' && call[1] === 'rest' && call.includes('POST')).length,
      0,
      'a second application registration is never created',
    );
  });
});

test('a provisioning failure exits 70, deletes nothing, and prints the resume command', () => {
  withSandbox('provision-failure', (sandbox) => {
    sandbox.state('fail_provision', '1\n');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 70);
    assert.match(result.output, /\.\/deploy\/azure\/up\.sh --env-name eval/);
    assert.match(result.output, /Nothing was deleted/);
    assert.equal(sandbox.callsFor('azd').filter((call) => call[1] === 'down').length, 0);
    assert.ok(!sandbox.has('deleted-resources.log'), 'no resource is deleted on failure');
  });
});

test('a failed migration exits 71, dumps redacted logs, and never deploys the applications', () => {
  withSandbox('migration-failure', (sandbox) => {
    sandbox.state('job_status_sequence', 'Running\nFailed\n');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 71, result.output);
    assert.match(result.output, /migration logs for/);
    assert.ok(!result.output.includes(ACCESS_TOKEN_MARKER), 'tokens in job logs are redacted');
    assert.ok(!result.output.includes('hunter2'), 'passwords in job logs are redacted');
    assert.equal(sandbox.environment('eval').VISTARA_DEPLOY_APPLICATIONS, 'false');
    assert.equal(sandbox.read('provision_count').trim(), '1', 'the activation pass never ran');
  });
});

test('a migration that never finishes exits 71 at the timeout', () => {
  withSandbox('migration-timeout', (sandbox) => {
    sandbox.state('job_status_sequence', 'Running\n');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 71, result.output);
    assert.match(result.output, /timed out/);
  });
});

test('the polled execution is the execution this run started', () => {
  withSandbox('migration-pinning', (sandbox) => {
    sandbox.state('job_execution_name', 'cj-migrate-eval-xyz789');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    const show = sandbox
      .calls()
      .find((call) => call.join(' ').includes('containerapp job execution show'));
    assert.ok(show, 'the execution is polled');
    const index = show.indexOf('--job-execution-name');
    assert.notEqual(index, -1, 'the execution is named explicitly, not inferred from "latest"');
    assert.equal(show[index + 1], 'cj-migrate-eval-xyz789');
  });
});

test('an API that never becomes healthy exits 75', () => {
  withSandbox('health-timeout', (sandbox) => {
    sandbox.state('health_ready', '503');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 75, result.output);
    assert.match(result.output, /never became healthy/);
  });

  withSandbox('health-order', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    const probes = sandbox
      .callsFor('curl')
      .map((call) => call.find((argument) => argument.startsWith('https://')))
      .filter((url) => url && url.includes('/health/'));
    assert.deepEqual(
      probes.map((url) => url.slice(url.indexOf('/health/'))),
      ['/health/live', '/health/startup', '/health/ready'],
      'liveness, then startup, then readiness',
    );
    const probeCall = sandbox
      .callsFor('curl')
      .find((call) => call.join(' ').includes('/health/live'));
    const hostIndex = probeCall.indexOf('-H');
    assert.ok(probeCall[hostIndex + 1].startsWith('Host: ca-api-eval.'), 'the probe sends the ingress host');
  });
});

test('a missing tool exits 69 and a signed-out CLI exits 77', () => {
  withSandbox('missing-tool', (sandbox) => {
    sandbox.removeFake('az');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 69, result.output);
    assert.match(result.output, /required tool 'az' was not found/);
  });

  withSandbox('signed-out', (sandbox) => {
    sandbox.touch('not_logged_in');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 77, result.output);
    assert.match(result.output, /az login/);
  });
});

test('a deployer without directory rights is told exactly what to run', () => {
  withSandbox('no-directory-rights', (sandbox) => {
    sandbox.touch('no_directory_rights');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 77, result.output);
    assert.match(result.output, /--skip-app-registration --client-id/);
    assert.match(result.output, /az ad app create --display-name/);
    assert.match(result.output, /az ad app federated-credential create/);
  });
});

test('--skip-app-registration requires a client ID and then creates nothing in the directory', () => {
  withSandbox('skip-without-client-id', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments(['--skip-app-registration']));
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /--skip-app-registration needs --client-id/);
  });

  withSandbox('skip-with-client-id', (sandbox) => {
    sandbox.touch('app_exists');
    sandbox.state('fic_issuer', `https://login.microsoftonline.com/${TENANT_ID}/v2.0`);
    sandbox.state('fic_subject', API_PRINCIPAL_ID);
    sandbox.state('fic_audience', 'api://AzureADTokenExchange');
    const result = run(
      sandbox,
      UP_SCRIPT,
      upArguments(['--skip-app-registration', '--client-id', APP_CLIENT_ID]),
    );
    assert.equal(result.status, 0, result.output);
    assert.equal(sandbox.environment('eval').VISTARA_DEPLOY_APP_REGISTRATION, 'false');
    assert.equal(
      sandbox.calls().filter((call) => call[0] === 'az' && call[1] === 'rest').length,
      0,
      'an operator-supplied registration is never rewritten',
    );
    assert.ok(
      sandbox.calls().some((call) => call.join(' ').includes('federated-credential list')),
      'but it is still verified',
    );
  });
});

test('a wrong federated credential or reply URL fails the deployment', () => {
  withSandbox('fic-mismatch', (sandbox) => {
    sandbox.touch('app_exists');
    sandbox.state('fic_issuer', 'https://login.microsoftonline.com/other-tenant/v2.0');
    sandbox.state('fic_subject', API_PRINCIPAL_ID);
    sandbox.state('fic_audience', 'api://AzureADTokenExchange');
    const result = run(
      sandbox,
      UP_SCRIPT,
      upArguments(['--skip-app-registration', '--client-id', APP_CLIENT_ID]),
    );
    assert.equal(result.status, 70, result.output);
    assert.match(result.output, /issuer mismatch/);
    assert.match(result.output, /az ad app federated-credential create/);
  });

  withSandbox('reply-url-mismatch', (sandbox) => {
    sandbox.touch('app_exists');
    sandbox.state('app_reply_urls', `${SERVICE_API_URI}/api/v1/auth/oidc/entra/callback`);
    sandbox.state('fic_issuer', `https://login.microsoftonline.com/${TENANT_ID}/v2.0`);
    sandbox.state('fic_subject', API_PRINCIPAL_ID);
    sandbox.state('fic_audience', 'api://AzureADTokenExchange');
    const result = run(
      sandbox,
      UP_SCRIPT,
      upArguments(['--skip-app-registration', '--client-id', APP_CLIENT_ID]),
    );
    assert.equal(result.status, 70, result.output);
    assert.match(result.output, /reply URLs do not match/);
  });

  withSandbox('logout-url-registered', (sandbox) => {
    sandbox.touch('app_exists');
    sandbox.state('app_logout_url', `${SERVICE_API_URI}/logout`);
    sandbox.state('fic_issuer', `https://login.microsoftonline.com/${TENANT_ID}/v2.0`);
    sandbox.state('fic_subject', API_PRINCIPAL_ID);
    sandbox.state('fic_audience', 'api://AzureADTokenExchange');
    const result = run(
      sandbox,
      UP_SCRIPT,
      upArguments(['--skip-app-registration', '--client-id', APP_CLIENT_ID]),
    );
    assert.equal(result.status, 70, result.output);
    assert.match(result.output, /web\.logoutUrl must not be registered/);
  });
});

test('the created registration declares exactly two reply URLs, no logout URL, and the exact credential', () => {
  withSandbox('registration-shape', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);

    const bodies = sandbox.read('bodies.log');
    const applicationBodies = bodies
      .split(/^=== /m)
      .filter((section) => section.includes('"signInAudience"'));
    assert.ok(applicationBodies.length >= 1, 'the registration body was recorded');
    for (const body of applicationBodies) {
      const replyUrls = body.match(/"https:\/\/[^"]*\/api\/v1\/auth\/oidc\/entra\/[^"]*"/g) ?? [];
      assert.deepEqual(
        replyUrls,
        [
          `"${SERVICE_API_URI}/api/v1/auth/oidc/entra/callback"`,
          `"${SERVICE_API_URI}/api/v1/auth/oidc/entra/signed-out"`,
        ],
        'exactly the two reply URLs the API serves, and nothing merged in',
      );
      assert.ok(body.includes('"logoutUrl": null'), 'no front-channel sign-out URL is registered');
    }
    assert.ok(bodies.includes('"signInAudience": "AzureADMyOrg"'));
    assert.ok(bodies.includes('"enableIdTokenIssuance": false'));
    assert.ok(bodies.includes(`"issuer": "https://login.microsoftonline.com/${TENANT_ID}/v2.0"`));
    assert.ok(bodies.includes(`"subject": "${API_PRINCIPAL_ID}"`));
    assert.ok(bodies.includes('"api://AzureADTokenExchange"'));

    assert.ok(
      sandbox.calls().some((call) => call.join(' ').includes('ad sp create')),
      'the tenant-local service principal is created too',
    );
  });
});

test('no secret ever reaches a command line, an environment value, or the output', () => {
  withSandbox('secret-hygiene', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);

    const calls = sandbox.read('calls.log');
    assert.ok(!calls.includes(PEPPER_MARKER), 'the pepper is never an argument');
    assert.ok(!result.output.includes(PEPPER_MARKER), 'the pepper is never printed');
    assert.ok(!calls.includes(ACCESS_TOKEN_MARKER), 'the database token is never an argument');
    assert.ok(!result.output.includes(ACCESS_TOKEN_MARKER), 'the database token is never printed');

    const environment = sandbox.environment('eval');
    for (const [key, value] of Object.entries(environment)) {
      assert.ok(!value.includes(PEPPER_MARKER), `${key} must not carry the pepper`);
      assert.ok(!value.includes(ACCESS_TOKEN_MARKER), `${key} must not carry a token`);
    }

    const secretSet = sandbox
      .calls()
      .find((call) => call.join(' ').includes('keyvault secret set'));
    assert.ok(secretSet.includes('--file'), 'the secret is handed over as a file');
    assert.ok(!secretSet.includes('--value'), 'never as a command-line value');
    assert.equal(sandbox.read('secret-value').trim(), PEPPER_MARKER, 'the file did carry the value');
    assert.equal(sandbox.read('secret-file-mode').trim(), '600', 'and it was owner-only');

    const psqlEnvironment = sandbox.read('psql-environment.log');
    assert.ok(psqlEnvironment.includes('PGPASSWORD=unset'), 'no token in the environment');
    assert.ok(psqlEnvironment.includes('MODE=600'), 'the password file is owner-only');
    assert.ok(psqlEnvironment.includes(ACCESS_TOKEN_MARKER), 'the token did reach psql, by file');
  });
});

test('the operator address is allowed through the firewall only while the roles are created', () => {
  withSandbox('firewall-window', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);

    const calls = sandbox.calls();
    const opened = indexOfCall(calls, 'az', 'firewall-rule', 'create');
    const sql = indexOfCall(calls, 'psql');
    const closed = indexOfCall(calls, 'az', 'firewall-rule', 'delete');
    assertOrdered(calls, opened, sql, 'the rule exists before the SQL runs');
    assertOrdered(calls, sql, closed, 'and is removed afterwards');
    assert.ok(sandbox.has('firewall_removed'));
    assert.ok(!sandbox.has('firewall_open'), 'no opening is left behind');

    const create = calls[opened];
    const startIndex = create.indexOf('--start-ip-address');
    assert.equal(create[startIndex + 1], '203.0.113.7');
    assert.equal(create[create.indexOf('--end-ip-address') + 1], '203.0.113.7', 'a single address, not a range');
  });
});

test('the firewall opening is removed even when the SQL fails', () => {
  withSandbox('firewall-trap', (sandbox) => {
    sandbox.touch('psql_fails');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 70, result.output);
    assert.ok(sandbox.has('firewall_removed'), 'the trap closed the opening');
    assert.ok(!sandbox.has('firewall_open'));
    assert.equal(sandbox.read('provision_count').trim(), '1', 'the applications were never turned on');
  });
});

test('the bootstrap SQL creates the three principals with the five-argument function', () => {
  withSandbox('pgaadauth-arity', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);

    const sql = sandbox.read('psql-sql.log');
    const creations = sql.match(/pgaadauth_create_principal_with_oid\([^)]*\)/g) ?? [];
    assert.equal(creations.length, 3, 'one per managed identity');
    for (const creation of creations) {
      assert.equal(creation.split(',').length, 5, `five arguments required: ${creation}`);
      assert.ok(creation.includes("'service'"), 'managed identities are service principals');
    }
    assert.ok(sql.includes('ALTER DEFAULT PRIVILEGES FOR ROLE'), 'the migrator grant model is replayed');

    const invocation = sandbox.callsFor('psql')[0].join(' ');
    assert.ok(invocation.includes('--set=api_role=vistara_api_runtime'));
    assert.ok(invocation.includes('--set=worker_role=vistara_worker_runtime'));
    assert.ok(invocation.includes('--set=migrator_role=vistara_migrator'));
    assert.ok(invocation.includes(`--set=api_object_id=${API_PRINCIPAL_ID}`));
    assert.ok(invocation.includes('sslmode=verify-full'), 'the operator connection verifies the server');
  });
});

test('a machine without psql runs the same SQL through a container', () => {
  withSandbox('psql-fallback', (sandbox) => {
    sandbox.removeFake('psql');
    const result = run(sandbox, UP_SCRIPT, upArguments(), {
      env: { VISTARA_PSQL_IMAGE: 'docker.io/library/postgres:17-alpine' },
    });
    assert.equal(result.status, 0, result.output);
    const docker = sandbox.callsFor('docker')[0];
    assert.ok(docker, 'the container fallback was used');
    const joined = docker.join(' ');
    assert.ok(joined.includes('psql'));
    assert.ok(joined.includes('PGPASSFILE=/vistara-run/pgpass'), 'the token still travels as a file');
    assert.ok(joined.includes(':ro'), 'nothing is mounted writable');
    assert.ok(!joined.includes('PGPASSWORD'), 'and never as a password variable');
  });
});

test('--what-if previews the deployment and changes nothing', () => {
  withSandbox('preview', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments(['--what-if']));
    assert.equal(result.status, 0, result.output);
    assert.ok(
      sandbox.calls().some((call) => call.join(' ').includes('deployment sub what-if')),
      'what-if is asked for',
    );
    assert.equal(
      sandbox.callsFor('azd').filter((call) => call[1] === 'provision').length,
      0,
      'nothing is provisioned',
    );
    const whatIf = sandbox
      .calls()
      .find((call) => call[0] === 'az' && call[1] === 'deployment')
      .join(' ');
    assert.ok(whatIf.includes('deployApplications=false'));
    assert.ok(whatIf.includes(`apiImage=ghcr.io/cody-sims/vistara-api@${API_DIGEST}`));
  });
});

test('a non-interactive run must name the first owner rather than infer it', () => {
  withSandbox('yes-without-owner', (sandbox) => {
    const withoutOwner = [];
    const original = upArguments();
    for (let index = 0; index < original.length; index += 1) {
      if (original[index] === '--owner-object-id') {
        index += 1;
        continue;
      }
      withoutOwner.push(original[index]);
    }
    const result = run(sandbox, UP_SCRIPT, withoutOwner);
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /--yes requires --owner-object-id/);
  });
});

test('an interactive run refuses to guess when it has no terminal', () => {
  withSandbox('no-terminal', (sandbox) => {
    const interactive = upArguments().filter((argument) => argument !== '--yes');
    const result = run(sandbox, UP_SCRIPT, interactive);
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /rerun with --yes/);
    assert.equal(sandbox.callsFor('azd').filter((call) => call[1] === 'provision').length, 0);
  });
});

test('the first owner and tenant are reported so the operator can check them', () => {
  withSandbox('owner-summary', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    assert.ok(result.output.includes(`first owner       ${SIGNED_IN_OBJECT_ID}`));
    assert.ok(result.output.includes(TENANT_ID));
    assert.equal(sandbox.environment('eval').VISTARA_FIRST_OWNER_OBJECT_ID, SIGNED_IN_OBJECT_ID);
  });
});

test('a malformed owner object ID or environment name is refused before any call', () => {
  withSandbox('bad-owner', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments(['--owner-object-id', 'not-a-guid']));
    assert.equal(result.status, 64, result.output);
  });

  withSandbox('bad-env-name', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, ['--env-name', 'a', '--yes', '--location', 'eastus']);
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /--env-name must be/);
  });
});
