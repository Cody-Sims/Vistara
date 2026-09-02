// The hosted bootstrap contract: the `azd` project, the hook wiring, the
// parameters the wrapper must supply, and the hygiene rules the scripts are
// written to. These are static assertions, so they fail on a change that would
// only show up in a live deployment hours later.

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import test from 'node:test';
import YAML from 'yaml';

import {
  AZURE_DIR,
  REPOSITORY_ROOT,
  UP_SCRIPT,
  createSandbox,
  run,
  shellcheckAvailable,
  upArguments,
} from './azure-bootstrap-harness.mjs';

const AZURE_YAML = readFileSync(resolve(AZURE_DIR, 'azure.yaml'), 'utf8');
const PROJECT = YAML.parse(AZURE_YAML);
const PARAMETERS = readFileSync(resolve(AZURE_DIR, 'infra/main.parameters.json'), 'utf8');

const SCRIPTS = [
  resolve(AZURE_DIR, 'up.sh'),
  resolve(AZURE_DIR, 'down.sh'),
  ...readdirSync(resolve(AZURE_DIR, 'hooks'))
    .filter((entry) => entry.endsWith('.sh'))
    .map((entry) => resolve(AZURE_DIR, 'hooks', entry)),
];

test('the azd project runs prebuilt images and never rebuilds from source', () => {
  assert.equal(PROJECT.name, 'vistara');
  assert.equal(PROJECT.infra.provider, 'bicep');
  assert.equal(PROJECT.infra.path, 'infra');

  assert.deepEqual(Object.keys(PROJECT.services), ['api', 'worker']);
  for (const [name, service] of Object.entries(PROJECT.services)) {
    assert.equal(service.host, 'containerapp', `${name} runs on Container Apps`);
    assert.ok(service.image, `${name} deploys a prebuilt image`);
    assert.match(service.image, /^\$\{VISTARA_[A-Z_]+_IMAGE\}$/, `${name} takes the resolved digest`);
    assert.equal(service.project, undefined, `${name} must not build from source`);
    assert.equal(service.language, undefined, `${name} has no source language to build`);
  }
});

test('every hook azd declares exists, is executable, and is the one the wrapper relies on', () => {
  const expected = {
    preprovision: 'hooks/preprovision-preflight.sh',
    postprovision: 'hooks/postprovision.sh',
    postdeploy: 'hooks/postdeploy-health.sh',
    predown: 'hooks/predown-retention.sh',
  };

  assert.deepEqual(Object.keys(PROJECT.hooks).sort(), Object.keys(expected).sort());

  for (const [event, path] of Object.entries(expected)) {
    const hook = PROJECT.hooks[event];
    assert.equal(hook.run, path, `${event} runs ${path}`);
    assert.equal(hook.shell, 'sh');
    assert.equal(hook.continueOnError, false, `${event} must stop the command it belongs to`);
    const resolved = resolve(AZURE_DIR, path);
    const mode = statSync(resolved).mode;
    assert.ok((mode & 0o111) !== 0, `${path} must be executable`);
  }
});

test('the postprovision chain runs the steps in dependency order', () => {
  const orchestrator = readFileSync(resolve(AZURE_DIR, 'hooks/postprovision.sh'), 'utf8');
  const order = [...orchestrator.matchAll(/run_step '([^']+)'/g)].map((match) => match[1]);
  assert.deepEqual(order, [
    'postprovision-app-registration.sh',
    'postprovision-verify-fic.sh',
    'postprovision-secrets.sh',
    'postprovision-database.sh',
    'postprovision-migrate.sh',
    'postdeploy-health.sh',
  ]);
  for (const step of order) {
    statSync(resolve(AZURE_DIR, 'hooks', step));
  }
});

test('every parameter the template cannot default is supplied by a run', () => {
  const required = [...PARAMETERS.matchAll(/\$\{([A-Z0-9_]+)\}/g)]
    .map((match) => match[1])
    .filter((name, index, all) => all.indexOf(name) === index);
  assert.ok(required.includes('VISTARA_API_IMAGE'), 'sanity: the parameter file is being read');

  const sandbox = createSandbox('parameters');
  try {
    const result = run(sandbox, UP_SCRIPT, upArguments());
    assert.equal(result.status, 0, result.output);
    const environment = sandbox.environment('eval');
    for (const name of required) {
      assert.ok(
        environment[name] !== undefined && environment[name] !== '',
        `${name} has no default in main.parameters.json, so the bootstrap must set it`,
      );
    }
  } finally {
    sandbox.cleanup();
  }
});

test('the scripts are written to fail loudly and run under the system bash', () => {
  for (const script of SCRIPTS) {
    const source = readFileSync(script, 'utf8');
    const name = script.slice(REPOSITORY_ROOT.length + 1);

    assert.match(source, /^#!\/usr\/bin\/env bash\n/, `${name} declares bash`);
    assert.ok(source.includes('set -euo pipefail'), `${name} stops on the first failure`);
    assert.ok(
      source.includes('if [ -z "${BASH_VERSION:-}" ]'),
      `${name} re-executes under bash when a POSIX shell invoked it`,
    );

    const mode = statSync(script).mode;
    assert.ok((mode & 0o111) !== 0, `${name} must be executable`);
  }
});

test('no script hands a secret to a command line or exports one', () => {
  for (const script of SCRIPTS) {
    const source = readFileSync(script, 'utf8');
    const name = script.slice(REPOSITORY_ROOT.length + 1);

    assert.ok(
      !/keyvault secret set[^\n]*--value/.test(source),
      `${name} must write secrets from a file, never as an argument`,
    );
    assert.ok(!/\bexport PGPASSWORD/.test(source), `${name} must not export a database password`);
    assert.ok(!/PGPASSWORD=/.test(source), `${name} must not set PGPASSWORD at all`);
    assert.ok(
      !/azd env set [A-Z_]*(PEPPER|PASSWORD|TOKEN|SECRET_VALUE)/.test(source),
      `${name} must not store secret material in the azd environment`,
    );
  }

  const secrets = readFileSync(resolve(AZURE_DIR, 'hooks/postprovision-secrets.sh'), 'utf8');
  assert.ok(secrets.includes('openssl rand -base64 32 >"$pepper_file"'), 'the pepper goes straight to a file');
  assert.ok(secrets.includes('--file "$pepper_file"'), 'and Key Vault reads that file');
});

test('the documented exit codes are the ones the scripts use', () => {
  const common = readFileSync(resolve(AZURE_DIR, 'hooks/lib/common.sh'), 'utf8');
  const declared = Object.fromEntries(
    [...common.matchAll(/^VISTARA_EXIT_([A-Z_]+)=(\d+)$/gm)].map((match) => [match[1], match[2]]),
  );
  assert.deepEqual(declared, {
    USAGE: '64',
    MISSING_TOOL: '69',
    PROVISION: '70',
    MIGRATION: '71',
    HEALTH: '75',
    PERMISSION: '77',
  });

  const up = readFileSync(UP_SCRIPT, 'utf8');
  assert.ok(
    up.includes('Exit codes: 0 ok · 64 usage · 69 missing tool · 70 provisioning failure ·'),
    'up.sh documents the taxonomy it implements',
  );
});

test('the reply URL and credential constants match the Bicep module', () => {
  const common = readFileSync(resolve(AZURE_DIR, 'hooks/lib/common.sh'), 'utf8');
  const bicep = readFileSync(resolve(AZURE_DIR, 'infra/entra/app-registration.bicep'), 'utf8');

  for (const literal of [
    '/api/v1/auth/oidc/entra/callback',
    '/api/v1/auth/oidc/entra/signed-out',
    'api-managed-identity',
    'api://AzureADTokenExchange',
  ]) {
    assert.ok(common.includes(literal), `common.sh pins ${literal}`);
    assert.ok(bicep.includes(literal), `the Bicep module pins ${literal}`);
  }

  const main = readFileSync(resolve(AZURE_DIR, 'infra/main.bicep'), 'utf8');
  assert.ok(main.includes("var apiKeyPepperSecretName = 'api-key-pepper'"), 'the secret name is shared');
  assert.ok(
    common.includes("VISTARA_API_KEY_PEPPER_SECRET_NAME='api-key-pepper'"),
    'and the bootstrap writes that exact secret',
  );
});

test('azd environment state stays out of the repository', () => {
  const ignore = readFileSync(join(REPOSITORY_ROOT, '.gitignore'), 'utf8');
  assert.ok(
    ignore.split('\n').some((line) => line.trim() === '.azure/'),
    '.azure/ holds plaintext outputs and the scratch directory for secret material',
  );
});

test('shellcheck is clean when it is installed', (t) => {
  if (!shellcheckAvailable()) {
    t.skip('shellcheck is not installed');
    return;
  }
  execFileSync(
    'shellcheck',
    ['--severity=warning', '--external-sources', ...SCRIPTS, resolve(AZURE_DIR, 'hooks/lib/common.sh')],
    {
      cwd: REPOSITORY_ROOT,
      stdio: 'pipe',
    },
  );
});
