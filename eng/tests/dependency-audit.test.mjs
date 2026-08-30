import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const AUDIT = resolve(REPOSITORY_ROOT, 'eng/audit-dependencies.sh');

const FAKE_DOTNET = `#!/usr/bin/env bash
if [[ "$*" == *--vulnerable* ]]; then
  if [[ "\${FAKE_DOTNET_VULNERABLE:-false}" == "true" ]]; then
    echo "Project Vistara.Api has the following vulnerable packages"
    echo "   > System.Example  1.0.0  High  https://github.com/advisories/GHSA-test"
  else
    echo "The given project has no vulnerable packages given the current sources."
  fi
  exit "\${FAKE_DOTNET_EXIT:-0}"
fi
if [[ "$*" == *--deprecated* ]]; then
  if [[ "\${FAKE_DOTNET_DEPRECATED:-false}" == "true" ]]; then
    echo "Project Vistara.Api has the following deprecated packages"
  else
    echo "The given project has no deprecated packages given the current sources."
  fi
  exit 0
fi
exit 0
`;

const FAKE_NPM = `#!/usr/bin/env bash
prefix=""
for index in $(seq 1 $#); do
  if [[ "\${!index}" == "--prefix" ]]; then
    next=$((index + 1))
    prefix="\${!next}"
  fi
done
echo "audited \${prefix}"
if [[ "\${FAKE_NPM_FAILING_PREFIX:-}" == "$prefix" ]]; then
  echo "1 high severity vulnerability"
  exit 1
fi
exit 0
`;

function withAudit(body) {
  const root = resolve(HERE, `.scratch-audit-${process.pid}-${randomUUID()}`);
  mkdirSync(resolve(root, 'bin'), { recursive: true });
  const dotnetPath = resolve(root, 'bin/dotnet');
  const npmPath = resolve(root, 'bin/npm');
  writeFileSync(dotnetPath, FAKE_DOTNET);
  writeFileSync(npmPath, FAKE_NPM);
  chmodSync(dotnetPath, 0o755);
  chmodSync(npmPath, 0o755);
  try {
    body((environment = {}) =>
      spawnSync('bash', [AUDIT], {
        cwd: REPOSITORY_ROOT,
        encoding: 'utf8',
        env: {
          ...process.env,
          DOTNET: dotnetPath,
          NPM: npmPath,
          ...environment,
        },
      }),
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('passes when no vulnerable .NET or npm dependency is reported', () => {
  withAudit((run) => {
    const result = run();
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(result.stdout, /dependency audit passed/i);
    for (const prefix of ['src/Vistara.Web', 'tests/Vistara.E2E', 'eng']) {
      assert.match(result.stdout, new RegExp(`audited ${prefix}`));
    }
  });
});

test('fails when dotnet reports a vulnerable package', () => {
  withAudit((run) => {
    const result = run({ FAKE_DOTNET_VULNERABLE: 'true' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /vulnerable \.NET packages/i);
  });
});

test('fails when the dotnet audit command itself fails', () => {
  withAudit((run) => {
    const result = run({ FAKE_DOTNET_EXIT: '1' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /failed with exit 1/);
  });
});

test('fails when an npm workspace reports a high severity advisory', () => {
  withAudit((run) => {
    const result = run({ FAKE_NPM_FAILING_PREFIX: 'tests/Vistara.E2E' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /tests\/Vistara\.E2E/);
  });
});

test('warns without failing when a package is only deprecated', () => {
  withAudit((run) => {
    const result = run({ FAKE_DOTNET_DEPRECATED: 'true' });
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(result.stdout, /::warning::Deprecated \.NET packages/);
  });
});
