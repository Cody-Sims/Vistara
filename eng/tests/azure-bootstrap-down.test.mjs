// `deploy/azure/down.sh` and the teardown guard — behavior gate.
//
// Teardown is the dangerous half of this bootstrap: it deletes real resources
// in a real subscription. These tests hold it to the rules that make that
// safe — prove the target first, keep the data by default, and never act on a
// destructive request that was not confirmed.

import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import test from 'node:test';

import {
  APP_CLIENT_ID,
  AZURE_DIR,
  DOWN_SCRIPT,
  RESOURCE_GROUP,
  SIGNED_IN_OBJECT_ID,
  SUBSCRIPTION_ID,
  VAULT_NAME,
  createSandbox,
  run,
} from './azure-bootstrap-harness.mjs';

const VAULT_ID =
  `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}`
  + `/providers/Microsoft.KeyVault/vaults/${VAULT_NAME}`;

const COMPUTE = {
  resources_Microsoft_App_containerApps: [
    `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/containerApps/ca-api-eval`,
    `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/containerApps/ca-worker-eval`,
  ].join('\n'),
  resources_Microsoft_App_jobs: `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/jobs/cj-migrate-eval`,
  resources_Microsoft_App_managedEnvironments: `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/managedEnvironments/cae-vistara-eval`,
  resources_Microsoft_OperationalInsights_workspaces: `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.OperationalInsights/workspaces/log-vistara-eval`,
  resources_Microsoft_DBforPostgreSQL_flexibleServers: `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-vistara-abcdefghij`,
  resources_Microsoft_Storage_storageAccounts: `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.Storage/storageAccounts/stvistaraabcdefghij`,
  resources_Microsoft_KeyVault_vaults: VAULT_ID,
};

const DEPLOYED_ENVIRONMENT = [
  'AZURE_ENV_NAME=eval',
  'AZURE_LOCATION=eastus',
  `AZURE_SUBSCRIPTION_ID=${SUBSCRIPTION_ID}`,
  `AZURE_RESOURCE_GROUP=${RESOURCE_GROUP}`,
  'VISTARA_BUDGET_START_DATE=2026-09-01',
  'VISTARA_DEPLOY_APPLICATIONS=true',
  `VISTARA_APPLICATION_CLIENT_ID=${APP_CLIENT_ID}`,
  `VISTARA_OPERATOR_OBJECT_ID=${SIGNED_IN_OBJECT_ID}`,
  `VISTARA_OPERATOR_KEY_VAULT_ROLE_SCOPE=${VAULT_ID}`,
  '',
].join('\n');

function deployedSandbox(name, overrides = {}) {
  const sandbox = createSandbox(name);
  sandbox.state('current_env_name', 'eval');
  sandbox.state('env-eval.env', overrides.environment ?? DEPLOYED_ENVIRONMENT);
  for (const [file, contents] of Object.entries(COMPUTE)) {
    sandbox.state(file, `${contents}\n`);
  }
  sandbox.state(
    'locks',
    `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-vistara-abcdefghij/providers/Microsoft.Authorization/locks/lock-psql\n`,
  );
  return sandbox;
}

function withSandbox(name, body, overrides) {
  const sandbox = deployedSandbox(name, overrides);
  try {
    return body(sandbox);
  } finally {
    sandbox.cleanup();
  }
}

test('the default teardown removes compute and keeps every byte of data', () => {
  withSandbox('down-retain', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--yes']);
    assert.equal(result.status, 0, result.output);

    const deleted = sandbox.read('deleted-resources.log').split('\n').filter(Boolean);
    assert.deepEqual(deleted, [
      `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/containerApps/ca-api-eval`,
      `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/containerApps/ca-worker-eval`,
      `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/jobs/cj-migrate-eval`,
      `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/managedEnvironments/cae-vistara-eval`,
      `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.OperationalInsights/workspaces/log-vistara-eval`,
    ]);

    assert.ok(!sandbox.has('deleted-locks.log'), 'data locks stay in place');
    assert.ok(!sandbox.has('down_called'), 'azd down is never run in the retaining mode');
    assert.ok(!sandbox.has('app_deleted'), 'the registration survives so the deployment can come back');

    // Each resource is removed with its own command, which waits for the
    // asynchronous teardown and reports a refusal in usable terms.
    const commands = sandbox
      .calls()
      .filter((call) => call.includes('--ids'))
      .map((call) => call.slice(0, 4).join(' '));
    assert.deepEqual(commands, [
      'az containerapp delete --ids',
      'az containerapp delete --ids',
      'az containerapp job delete',
      'az containerapp env delete',
      'az monitor log-analytics workspace',
    ]);

    assert.match(result.output, /Retained/);
    assert.ok(result.output.includes('flexibleServers/psql-vistara-abcdefghij'));
    assert.ok(result.output.includes('storageAccounts/stvistaraabcdefghij'));
    assert.ok(result.output.includes(`vaults/${VAULT_NAME}`));
    assert.match(result.output, /still cost money/);
    assert.match(result.output, /Month to date against the budget: 12\.34/);
    assert.match(result.output, /--delete-data/);

    const environment = sandbox.environment('eval');
    assert.equal(environment.VISTARA_BUDGET_START_DATE, '2026-09-01', 'an existing budget keeps its period');
    assert.equal(environment.VISTARA_DEPLOY_APPLICATIONS, 'false');
  });
});

test('the operator role assignment the bootstrap created is handed back', () => {
  withSandbox('down-role', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--yes']);
    assert.equal(result.status, 0, result.output);
    assert.ok(sandbox.has('role_assignment_deleted'));
    const deletion = sandbox
      .calls()
      .find((call) => call.join(' ').includes('role assignment delete'));
    assert.ok(deletion.includes(VAULT_ID), 'scoped to the vault this environment created');
  });
});

test('a role assignment recorded outside the environment is left alone', () => {
  withSandbox(
    'down-foreign-role',
    (sandbox) => {
      const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--yes']);
      assert.equal(result.status, 0, result.output);
      assert.ok(!sandbox.has('role_assignment_deleted'));
      assert.match(result.output, /outside rg-vistara-eval; leaving it alone/);
    },
    {
      environment: DEPLOYED_ENVIRONMENT.replace(
        `VISTARA_OPERATOR_KEY_VAULT_ROLE_SCOPE=${VAULT_ID}`,
        'VISTARA_OPERATOR_KEY_VAULT_ROLE_SCOPE=/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/other/providers/Microsoft.KeyVault/vaults/kv-other',
      ),
    },
  );
});

test('--delete-data removes the locks, tears the environment down, and frees the budget period', () => {
  withSandbox('down-delete-data', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--delete-data', '--yes']);
    assert.equal(result.status, 0, result.output);

    const calls = sandbox.calls();
    const lockDeleted = calls.findIndex((call) => call.join(' ').includes('lock delete'));
    const downCalled = calls.findIndex((call) => call[0] === 'azd' && call[1] === 'down');
    assert.notEqual(lockDeleted, -1, 'the data locks are removed');
    assert.notEqual(downCalled, -1, 'and then the environment is deleted');
    assert.ok(lockDeleted < downCalled, 'locks first, or azd down fails halfway through');

    const down = calls[downCalled];
    assert.ok(down.includes('--force'));
    assert.ok(down.includes('--purge'), 'the vault is purged so its name can be reused');

    assert.ok(sandbox.has('app_deleted'), 'the registration this environment created goes too');

    const environment = sandbox.environment('eval');
    assert.equal(
      environment.VISTARA_BUDGET_START_DATE,
      '',
      'the budget went with the resource group, so a future deployment may create a new one',
    );
    assert.equal(environment.VISTARA_API_KEY_PEPPER_SECRET_URI, '');
    assert.equal(environment.VISTARA_MIGRATION_COMPLETED_DIGEST, '');
  });
});

test('--keep-app-registration and an operator-supplied registration are both respected', () => {
  withSandbox('down-keep-app', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, [
      '--env-name',
      'eval',
      '--delete-data',
      '--keep-app-registration',
      '--yes',
    ]);
    assert.equal(result.status, 0, result.output);
    assert.ok(!sandbox.has('app_deleted'));
  });

  withSandbox('down-operator-app', (sandbox) => {
    sandbox.state('group_app_registration_tag', 'operator-supplied');
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--delete-data', '--yes']);
    assert.equal(result.status, 0, result.output);
    assert.ok(!sandbox.has('app_deleted'), 'a registration this deployment did not create is never deleted');
  });
});

test('a destructive teardown without a confirmation deletes nothing', () => {
  withSandbox('down-unconfirmed', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--delete-data']);
    assert.equal(result.status, 64, result.output);
    assert.match(
      result.output,
      /no terminal is attached; rerun with --yes/,
      'a destructive teardown never proceeds on an answer it did not get',
    );
    assert.ok(!sandbox.has('deleted-locks.log'), 'no lock was removed');
    assert.ok(!sandbox.has('down_called'), 'no environment was deleted');
    assert.ok(!sandbox.has('deleted-resources.log'), 'no resource was deleted');
    assert.equal(sandbox.environment('eval').VISTARA_BUDGET_START_DATE, '2026-09-01');
  });

  withSandbox('down-retain-unconfirmed', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval']);
    assert.equal(result.status, 64, result.output);
    assert.ok(!sandbox.has('deleted-resources.log'));
  });
});

test('a resource group belonging to another environment is refused', () => {
  withSandbox('down-wrong-group', (sandbox) => {
    sandbox.state('group_env_tag', 'production');
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--yes']);
    assert.equal(result.status, 77, result.output);
    assert.match(result.output, /Refusing to delete resources this environment does not own/);
    assert.ok(!sandbox.has('deleted-resources.log'));
    assert.ok(!sandbox.has('down_called'));
  });
});

test('every destructive call names the subscription explicitly', () => {
  withSandbox('down-subscription', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--yes']);
    assert.equal(result.status, 0, result.output);
    for (const call of sandbox.calls()) {
      const joined = call.join(' ');
      if (call[0] !== 'az') {
        continue;
      }
      const destructive =
        joined.includes('resource list')
        || joined.includes('group show')
        || joined.includes('containerapp delete')
        || joined.includes('containerapp job delete')
        || joined.includes('containerapp env delete')
        || joined.includes('log-analytics workspace delete');
      if (!destructive) {
        continue;
      }
      assert.ok(
        call.includes('--subscription'),
        `${joined} must not rely on whichever subscription the CLI has selected`,
      );
      assert.equal(call[call.indexOf('--subscription') + 1], SUBSCRIPTION_ID);
    }
  });
});

test('a missing resource group is reported rather than treated as an error', () => {
  withSandbox('down-missing-group', (sandbox) => {
    sandbox.touch('group_missing');
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--yes']);
    assert.equal(result.status, 0, result.output);
    assert.match(result.output, /does not exist in subscription/);
    assert.ok(!sandbox.has('deleted-resources.log'));
  });
});

test('an unknown environment or option is refused', () => {
  withSandbox('down-unknown-env', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'does-not-exist', '--yes']);
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /no azd environment named does-not-exist/);
  });

  withSandbox('down-unknown-option', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--delete-everything']);
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /unknown option/);
  });
});

test('a bare azd down is refused while the data locks are in place', () => {
  withSandbox('predown-guard', (sandbox) => {
    const hook = resolve(AZURE_DIR, 'hooks/predown-retention.sh');
    const result = run(sandbox, hook, [], { env: { AZURE_ENV_NAME: 'eval' } });
    assert.equal(result.status, 64, result.output);
    assert.match(result.output, /\.\/deploy\/azure\/down\.sh --env-name eval/);
    assert.match(result.output, /--delete-data/);
  });

  withSandbox('predown-allowed', (sandbox) => {
    const hook = resolve(AZURE_DIR, 'hooks/predown-retention.sh');
    const result = run(sandbox, hook, [], {
      env: { AZURE_ENV_NAME: 'eval', VISTARA_DOWN_CONFIRMED: '1' },
    });
    assert.equal(result.status, 0, result.output);
    assert.match(result.output, /Teardown may proceed/);
    assert.match(result.output, /about to delete Microsoft.DBforPostgreSQL/);
  });
});

test('the budget is read at the scope the template creates it', () => {
  withSandbox('down-budget-scope', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--yes']);
    assert.equal(result.status, 0, result.output);

    const budgetCalls = sandbox
      .calls()
      .filter((call) => call[0] === 'az' && call[1] === 'consumption');
    assert.equal(budgetCalls.length, 1, 'the budget is asked for exactly once');

    const [budget] = budgetCalls;
    assert.deepEqual(
      budget.slice(1, 4),
      ['consumption', 'budget', 'show-with-rg'],
      'a resource-group budget is not addressable through the subscription-scoped form',
    );
    assert.equal(budget[budget.indexOf('--resource-group') + 1], RESOURCE_GROUP);
    assert.equal(
      budget[budget.indexOf('--budget-name') + 1],
      'budget-vistara-eval',
      'the name the template gives it',
    );
    assert.equal(budget[budget.indexOf('--subscription') + 1], SUBSCRIPTION_ID);
    assert.equal(budget[budget.indexOf('--query') + 1], 'currentSpend.amount');

    // The stand-in answers nothing unless the exact command is used, so the
    // figure appearing at all is what proves the right one was.
    assert.match(result.output, /Month to date against the budget: 12\.34/);
  });
});

test('the subscription-scoped budget command is never used', () => {
  withSandbox('down-budget-negative', (sandbox) => {
    const result = run(sandbox, DOWN_SCRIPT, ['--env-name', 'eval', '--yes']);
    assert.equal(result.status, 0, result.output);
    for (const call of sandbox.calls()) {
      if (call[0] !== 'az' || call[1] !== 'consumption') {
        continue;
      }
      assert.notEqual(
        call[3],
        'show',
        'az consumption budget show addresses a subscription budget and answers nothing for this one',
      );
    }
  });
});
