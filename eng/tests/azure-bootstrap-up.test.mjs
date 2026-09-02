// `deploy/azure/up.sh` — behavior gate.
//
// Every external command is a recording stand-in (see
// `azure-bootstrap-harness.mjs`), so these run the real scripts end to end
// without an Azure subscription and without creating anything billable.

import assert from 'node:assert/strict';
import test from 'node:test';

import { execFileSync, spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

import {
  ACCESS_TOKEN_MARKER,
  AZURE_DIR,
  REPOSITORY_ROOT,
  API_DIGEST,
  APP_CLIENT_ID,
  API_PRINCIPAL_ID,
  MIGRATION_DIGEST,
  PEPPER_MARKER,
  SERVICE_API_URI,
  SIGNED_IN_OBJECT_ID,
  SUBSCRIPTION_ID,
  TENANT_ID,
  UP_SCRIPT,
  WORKER_DIGEST,
  createSandbox,
  interactiveRunsAvailable,
  run,
  runInteractive,
  upArguments,
} from './azure-bootstrap-harness.mjs';

/** Commands that change something in Azure or in the recorded environment. */
function mutatingCalls(sandbox) {
  return sandbox.calls().filter((call) => {
    const joined = call.join(' ');
    if (call[0] === 'azd') {
      return /\b(provision|deploy|down|env set|env new|env select)\b/.test(joined);
    }
    if (call[0] !== 'az') {
      return false;
    }
    return /\b(create|delete|set|update|purge|register|start|import)\b/.test(joined);
  });
}

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

function withoutOption(argumentList, option) {
  const kept = [];
  for (let index = 0; index < argumentList.length; index += 1) {
    if (argumentList[index] === option) {
      index += 1;
      continue;
    }
    kept.push(argumentList[index]);
  }
  return kept;
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

test('the release lookup follows the redirect, bounded, over HTTPS, and checks where it landed', () => {
  withSandbox('release-invocation', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, withoutOption(upArguments(), '--release'));
    assert.equal(result.status, 0, result.output);

    const lookup = sandbox
      .callsFor('curl')
      .find((call) => call.join(' ').includes('/releases/latest'));
    assert.ok(lookup, 'the release page is asked for');

    // GitHub answers /releases/latest with a redirect, so a lookup that does
    // not follow one resolves nothing at all.
    assert.ok(lookup.includes('--location'), 'the redirect is followed');
    assert.equal(lookup[lookup.indexOf('--proto') + 1], '=https', 'the request itself is HTTPS only');
    assert.equal(
      lookup[lookup.indexOf('--proto-redir') + 1],
      '=https',
      'and so is every hop it is redirected to',
    );
    assert.match(lookup[lookup.indexOf('--max-redirs') + 1], /^[0-9]+$/, 'the chain is bounded');
    assert.match(lookup[lookup.indexOf('--max-time') + 1], /^[0-9]+$/, 'and so is the time it may take');
    assert.ok(
      lookup.join(' ').includes('%{http_code} %{url_effective}'),
      'the status and the URL it ended on are both read back',
    );
  });
});

test('a lookup that stops following redirects resolves nothing', () => {
  // Proves the assertion above is load-bearing: the same script with
  // --location removed sees the redirect itself, which is not a release.
  withSandbox('release-no-location', (sandbox) => {
    const source = readFileSync(resolve(AZURE_DIR, 'hooks/lib/common.sh'), 'utf8');
    const mutated = source.replace('    --location \\\n', '');
    assert.notEqual(mutated, source, 'the redirect flag was found and removed');

    const library = resolve(sandbox.root, 'common.sh');
    writeFileSync(library, mutated, 'utf8');
    const probe = resolve(sandbox.root, 'probe.sh');
    writeFileSync(
      probe,
      `#!/usr/bin/env bash\nset -euo pipefail\n. '${library}'\n`
        + 'if tag=$(vistara_resolve_latest_release_tag cody-sims Vistara); then\n'
        + '  printf \'resolved:%s\\n\' "$tag"\nelse\n  printf \'unresolved\\n\'\nfi\n',
      'utf8',
    );

    const result = run(sandbox, probe, []);
    assert.match(result.output, /unresolved/, 'a redirect that is not followed resolves no tag');
  });
});

test('a lookup that lands somewhere other than a release of this repository is refused', () => {
  const cases = [
    ['release_status', '404', 'a non-200 answer'],
    ['release_final_url', 'https://github.com/cody-sims/Vistara/releases', 'a repository with no releases'],
    ['release_final_url', 'https://evil.example.com/releases/tag/v9.9.9', 'a redirect to another host'],
    ['release_final_url', 'https://github.com/someone-else/Vistara/releases/tag/v9.9.9', 'another repository'],
    ['release_tag', '../../../etc/passwd', 'a tag that is not a tag'],
  ];

  for (const [file, value, description] of cases) {
    withSandbox(`release-refused-${file}-${value.length}`, (sandbox) => {
      sandbox.state(file, value);
      const result = run(sandbox, UP_SCRIPT, withoutOption(upArguments(), '--release'));
      assert.equal(result.status, 64, `${description}: ${result.output}`);
      assert.match(result.output, /no release tag could be resolved/);
      assert.equal(
        sandbox.callsFor('azd').filter((call) => call[1] === 'provision').length,
        0,
        `${description}: nothing is provisioned from an unresolved release`,
      );
    });
  }
});

test(
  'the real GitHub release redirect is followed the way the resolver expects',
  { skip: process.env.VISTARA_NETWORK_CHECK === '1' ? false : 'set VISTARA_NETWORK_CHECK=1 to reach github.com' },
  () => {
    // A single unauthenticated GET of a public page, which is what the
    // resolver does in a real run. It proves the parts a stand-in cannot:
    // that GitHub still answers /releases/latest with a redirect, that curl
    // follows it under the flags used here, and that the landing URL has the
    // shape the resolver insists on.
    const remote = execFileSync('git', ['-C', REPOSITORY_ROOT, 'remote', 'get-url', 'origin'], {
      encoding: 'utf8',
    }).trim();
    const match = remote.match(/github\.com[:/]([^/]+)\/(.+?)(?:\.git)?$/);
    assert.ok(match, `could not read an owner and repository from ${remote}`);
    const [, owner, repository] = match;

    const probe = spawnSync(
      'bash',
      [
        '-c',
        'set -euo pipefail\n'
          + `. '${resolve(AZURE_DIR, 'hooks/lib/common.sh')}'\n`
          + `if tag=$(vistara_resolve_latest_release_tag '${owner}' '${repository}'); then\n`
          + '  printf \'resolved %s\\n\' "$tag"\n'
          + 'else\n'
          + '  printf \'unresolved\\n\'\n'
          + 'fi\n'
          + 'curl -fsS --location --proto \'=https\' --proto-redir \'=https\' --max-redirs 5 --max-time 30'
          + ' --output /dev/null --write-out \'landed %{http_code} %{url_effective}\\n\''
          + ` 'https://github.com/${owner}/${repository}/releases/latest'\n`,
      ],
      { encoding: 'utf8', timeout: 60_000, env: { ...process.env, VISTARA_STATE_DIR: undefined } },
    );

    assert.equal(probe.status, 0, `${probe.stdout}${probe.stderr}`);
    const landed = probe.stdout.split('\n').find((line) => line.startsWith('landed '));
    assert.ok(landed, `no landing line in: ${probe.stdout}`);
    const [, statusCode, finalUrl] = landed.split(' ');
    assert.equal(statusCode, '200', 'the redirect chain ends in a 200');
    assert.ok(
      finalUrl.startsWith(`https://github.com/${owner}/${repository}/releases`),
      `the chain stayed on this repository, landed on ${finalUrl}`,
    );

    const resolved = probe.stdout.split('\n').find((line) => line.startsWith('resolved '));
    if (finalUrl.includes('/releases/tag/')) {
      assert.ok(resolved, 'a tagged landing resolves a tag');
      assert.match(resolved.slice('resolved '.length), /^[A-Za-z0-9][A-Za-z0-9._+-]*$/);
    } else {
      assert.match(probe.stdout, /unresolved/, 'a repository with no releases resolves nothing');
    }
  },
);

test('the latest release tag is resolved when none is given', () => {
  withSandbox('release-lookup', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, withoutOption(upArguments(), '--release'));
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

test('a firewall rule that may have been created is removed even when the call fails', () => {
  withSandbox('firewall-create-failure', (sandbox) => {
    sandbox.touch('firewall_create_fails');
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 70, result.output);
    assert.ok(
      sandbox.calls().some((call) => call.join(' ').includes('firewall-rule delete')),
      'a create that reports failure may still have reached Azure, so the delete is attempted anyway',
    );
    assert.equal(sandbox.callsFor('psql').length, 0, 'and nothing connects through it');
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
    // The template constrains this to the first day of a month, so an empty
    // value fails against real Azure exactly where the preview is supposed to
    // be safe. The shape is what is asserted, not the presence of the word.
    assert.match(
      whatIf,
      /budgetStartDate=\d{4}-\d{2}-01(\s|$)/,
      'the preview passes a budget period the template will accept',
    );

    // A preview is a question, not a change: it must not select a
    // subscription, create an environment, or record a budget period that a
    // later real run would then be stuck with.
    assert.deepEqual(
      mutatingCalls(sandbox).map((call) => call.join(' ')),
      [],
      'a preview changes nothing at all',
    );
    assert.equal(sandbox.read('env-eval.env'), '', 'no environment is written');
  });
});

test('a non-interactive run must name the first owner rather than infer it', () => {
  withSandbox('yes-without-owner', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, withoutOption(upArguments(), '--owner-object-id'));
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /--yes requires --owner-object-id/);
  });
});

test('an interactive run refuses to guess when it has no terminal, having changed nothing', () => {
  withSandbox('no-terminal', (sandbox) => {
    const interactive = upArguments().filter((argument) => argument !== '--yes');
    const result = run(sandbox, UP_SCRIPT, interactive);
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /rerun with --yes/);
    assert.deepEqual(
      mutatingCalls(sandbox).map((call) => call.join(' ')),
      [],
      'a run that was never agreed to changes nothing, locally or in Azure',
    );
    assert.ok(
      !sandbox.calls().some((call) => call.join(' ').includes('account set')),
      'not even the subscription the operator has selected in their own shell',
    );
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

test('a subscription in another tenant is refused rather than guessed at', () => {
  withSandbox('foreign-tenant', (sandbox) => {
    sandbox.touch('allow_subscription_switch');
    sandbox.state('foreign_tenant_id', '12121212-1212-1212-1212-121212121212');
    const result = run(
      sandbox,
      UP_SCRIPT,
      upArguments(['--subscription', '99999999-9999-9999-9999-999999999999']),
    );
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /belongs to tenant 12121212/, 'the two tenants are named');
    assert.match(result.output, /az account set --subscription 99999999/, 'and the fix is spelled out');
    assert.deepEqual(
      mutatingCalls(sandbox).map((call) => call.join(' ')),
      [],
      'a directory lookup that would answer for the wrong tenant stops the run before it changes anything',
    );
  });
});

test('a subscription in the same tenant is switched to, after the confirmation', () => {
  withSandbox('same-tenant-switch', (sandbox) => {
    // The tenant guard must not fire on the ordinary case of deploying into a
    // second subscription of the same directory.
    sandbox.touch('allow_subscription_switch');
    const result = run(
      sandbox,
      UP_SCRIPT,
      upArguments(['--subscription', '99999999-9999-9999-9999-999999999999']),
    );
    assert.equal(result.status, 0, result.output);

    const calls = sandbox.calls().map((call) => call.join(' '));
    const switched = calls.findIndex((call) => call.includes('account set'));
    assert.notEqual(switched, -1, 'the CLI is pointed at the subscription being deployed into');
    assert.equal(
      calls[switched].includes('--subscription 99999999-9999-9999-9999-999999999999'),
      true,
    );

    // Reads may precede the switch; anything that writes may not.
    const firstEnvironmentWrite = calls.findIndex((call) =>
      /^azd (env (new|select|set)|provision|deploy)\b/.test(call),
    );
    assert.notEqual(firstEnvironmentWrite, -1);
    assert.ok(
      switched < firstEnvironmentWrite,
      'the CLI is pointed at the right subscription before azd writes anything',
    );
    assert.equal(
      sandbox.environment('eval').AZURE_SUBSCRIPTION_ID,
      '99999999-9999-9999-9999-999999999999',
    );
  });
});

test('an explicit tenant that is not the subscription tenant is refused before anything is created', () => {
  withSandbox('tenant-mismatch', (sandbox) => {
    const result = run(
      sandbox,
      UP_SCRIPT,
      upArguments(['--tenant-id', '12121212-1212-1212-1212-121212121212']),
    );
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /--tenant-id is 12121212-1212-1212-1212-121212121212/);
    assert.match(result.output, /--tenant-id does not match the tenant of the subscription/);
    assert.deepEqual(
      mutatingCalls(sandbox).map((call) => call.join(' ')),
      [],
      'a deployment pinned to the wrong directory never starts',
    );
  });
});

test('an explicit tenant that is not the signed-in tenant is refused', () => {
  withSandbox('tenant-not-signed-in', (sandbox) => {
    // The subscription agrees with --tenant-id, but the CLI is signed in
    // somewhere else, and every az ad lookup answers for where it is signed in.
    sandbox.touch('allow_subscription_switch');
    sandbox.state('foreign_tenant_id', '12121212-1212-1212-1212-121212121212');
    const result = run(
      sandbox,
      UP_SCRIPT,
      upArguments([
        '--subscription',
        '99999999-9999-9999-9999-999999999999',
        '--tenant-id',
        '12121212-1212-1212-1212-121212121212',
      ]),
    );
    assert.notEqual(result.status, 0, result.output);
    assert.deepEqual(mutatingCalls(sandbox).map((call) => call.join(' ')), []);
  });
});

test('the same tenant written in a different case is the same tenant', () => {
  withSandbox('tenant-case', (sandbox) => {
    const result = run(sandbox, UP_SCRIPT, upArguments(['--tenant-id', TENANT_ID.toUpperCase()]));
    assert.equal(result.status, 0, result.output);
    assert.equal(
      sandbox.environment('eval').AZURE_TENANT_ID,
      TENANT_ID,
      'and it is recorded in one canonical form',
    );
  });
});

test('a tenant that is wrong everywhere is still caught, because Azure is asked', () => {
  withSandbox('tenant-self-consistent', (sandbox) => {
    // The hostile case: every value derived from the tenant agrees with every
    // other. The environment says one tenant, the registered federated
    // credential was issued by that same tenant, and the byte comparison the
    // verification makes would therefore pass. Only Azure disagrees.
    const wrongTenant = '12121212-1212-1212-1212-121212121212';
    sandbox.state(
      'outputs.env',
      sandbox.read('outputs.env').replace(`AZURE_TENANT_ID=${TENANT_ID}`, `AZURE_TENANT_ID=${wrongTenant}`),
    );
    sandbox.touch('app_exists');
    sandbox.state('fic_issuer', `https://login.microsoftonline.com/${wrongTenant}/v2.0`);
    sandbox.state('fic_subject', API_PRINCIPAL_ID);
    sandbox.state('fic_audience', 'api://AzureADTokenExchange');

    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 70, result.output);
    assert.match(result.output, /AZURE_TENANT_ID does not match the tenant of AZURE_SUBSCRIPTION_ID/);
    assert.match(result.output, new RegExp(`AZURE_TENANT_ID is ${wrongTenant}`));
    assert.match(result.output, new RegExp(`reports for AZURE_SUBSCRIPTION_ID .* is ${TENANT_ID}`));

    assert.ok(
      !sandbox.calls().some((call) => call[0] === 'az' && call[1] === 'rest'),
      'no registration is created in the wrong directory',
    );
    assert.equal(sandbox.read('provision_count').trim(), '1', 'and the applications are never turned on');
  });
});

test('the federated credential verification cannot confirm a wrong tenant against itself', () => {
  withSandbox('fic-self-comparison', (sandbox) => {
    // Run the verification hook alone, with a registration that matches the
    // environment perfectly and a tenant that Azure disagrees with. Every
    // byte comparison in the hook passes; the run must still fail.
    const wrongTenant = '12121212-1212-1212-1212-121212121212';
    const hookEnvironment = {
      AZURE_ENV_NAME: 'eval',
      AZURE_SUBSCRIPTION_ID: SUBSCRIPTION_ID,
      AZURE_TENANT_ID: wrongTenant,
      SERVICE_API_URI,
      API_IDENTITY_PRINCIPAL_ID: API_PRINCIPAL_ID,
      VISTARA_APPLICATION_CLIENT_ID: APP_CLIENT_ID,
      ENTRA_APPLICATION_OBJECT_ID: '88888888-8888-8888-8888-888888888888',
    };
    sandbox.touch('app_exists');
    sandbox.state('fic_issuer', `https://login.microsoftonline.com/${wrongTenant}/v2.0`);
    sandbox.state('fic_subject', API_PRINCIPAL_ID);
    sandbox.state('fic_audience', 'api://AzureADTokenExchange');

    const hook = resolve(AZURE_DIR, 'hooks/postprovision-verify-fic.sh');
    const rejected = run(sandbox, hook, [], { env: hookEnvironment });
    assert.equal(rejected.status, 70, rejected.output);
    assert.match(rejected.output, /AZURE_TENANT_ID does not match/);
    assert.ok(
      !rejected.output.includes('issuer mismatch'),
      'the byte comparison agreed, which is exactly why it cannot be the only check',
    );

    // The same registration, with the tenant Azure actually reports, passes.
    sandbox.state('fic_issuer', `https://login.microsoftonline.com/${TENANT_ID}/v2.0`);
    const accepted = run(sandbox, hook, [], {
      env: { ...hookEnvironment, AZURE_TENANT_ID: TENANT_ID },
    });
    assert.equal(accepted.status, 0, accepted.output);
  });
});

test('the registration hook asks Azure about the tenant before it creates anything', () => {
  withSandbox('registration-tenant-check', (sandbox) => {
    const wrongTenant = '12121212-1212-1212-1212-121212121212';
    const hook = resolve(AZURE_DIR, 'hooks/postprovision-app-registration.sh');
    const result = run(sandbox, hook, [], {
      env: {
        AZURE_ENV_NAME: 'eval',
        AZURE_SUBSCRIPTION_ID: SUBSCRIPTION_ID,
        AZURE_TENANT_ID: wrongTenant,
        SERVICE_API_URI,
        API_IDENTITY_PRINCIPAL_ID: API_PRINCIPAL_ID,
      },
    });
    assert.equal(result.status, 70, result.output);
    assert.match(result.output, /AZURE_TENANT_ID does not match/);
    assert.deepEqual(
      mutatingCalls(sandbox).map((call) => call.join(' ')),
      [],
      'nothing is registered in a directory the subscription does not belong to',
    );
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


test(
  'a deployment the operator declines is never started',
  { skip: interactiveRunsAvailable() ? false : 'script(1) is unavailable' },
  () => {
    withSandbox('up-declined', (sandbox) => {
      const result = runInteractive(
        sandbox,
        UP_SCRIPT,
        upArguments().filter((argument) => argument !== '--yes'),
      );
      assert.equal(result.status, 0, result.output);
      assert.match(result.output, /Create or update this deployment\?/, 'the summary is confirmed first');
      assert.match(result.output, /Nothing was changed\./);
      assert.deepEqual(
        mutatingCalls(sandbox).map((call) => call.join(' ')),
        [],
        'a declined deployment creates no environment, sets no value, and provisions nothing',
      );
    });
  },
);

test(
  'confirming the deployment but not the first owner starts nothing',
  { skip: interactiveRunsAvailable() ? false : 'script(1) is unavailable' },
  () => {
    withSandbox('up-owner-declined', (sandbox) => {
      // The pty delivers an empty answer, so the first question is already
      // declined; the second one is only reachable through --yes, which is
      // covered by the non-interactive tests. What matters here is that the
      // owner is asked about separately at all.
      const source = readFileSync(UP_SCRIPT, 'utf8');
      assert.match(
        source,
        /vistara_confirm "Confirm \$\{owner_object_id\} as the first owner\?"/,
        'the first owner is confirmed on its own, not folded into the summary',
      );
      assert.ok(
        source.indexOf('as the first owner?') < source.indexOf('azd env select'),
        'and before the environment is touched',
      );
    });
  },
);
