import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const GATE = resolve(REPOSITORY_ROOT, 'deploy/containers/tests/migration-lock.sh');

const FAKE_DOCKER = `#!/usr/bin/env bash
set -uo pipefail

printf '%s\\n' "$*" >> "$FAKE_DOCKER_LOG"

case "$1" in
  network)
    exit 0
    ;;
  rm)
    exit 0
    ;;
  logs)
    echo "container log"
    exit 0
    ;;
  exec)
    if [[ "$*" == *pg_isready* ]]; then
      exit 0
    fi
    if [[ "$*" == *"HAVING count(*) > 1"* ]]; then
      echo "\${FAKE_DUPLICATES:-0}"
      exit 0
    fi
    if [[ "$*" == *__EFMigrationsHistory* ]]; then
      count=$(grep -c 'migration-run' "$FAKE_DOCKER_LOG" || true)
      if [[ "$count" -gt 2 ]]; then
        echo "\${FAKE_LEDGER_COUNT_AFTER:-\${FAKE_LEDGER_COUNT:-7}}"
      else
        echo "\${FAKE_LEDGER_COUNT:-7}"
      fi
      exit 0
    fi
    exit 0
    ;;
  run)
    if [[ "$*" == *"$FAKE_MIGRATION_IMAGE"* ]]; then
      printf '%s\\n' "migration-run" >> "$FAKE_DOCKER_LOG"
      migrations=$(grep -c 'migration-run' "$FAKE_DOCKER_LOG" || true)
      if [[ "$migrations" == "2" ]]; then
        exit "\${FAKE_SECOND_MIGRATION_EXIT:-0}"
      fi
      if [[ "$migrations" -gt 2 ]]; then
        exit "\${FAKE_THIRD_MIGRATION_EXIT:-0}"
      fi
      exit 0
    fi
    echo "fake-container-id"
    exit 0
    ;;
esac
exit 0
`;

function withGate(body) {
  const root = resolve(HERE, `.scratch-migration-lock-${process.pid}-${randomUUID()}`);
  mkdirSync(resolve(root, 'bin'), { recursive: true });
  const dockerPath = resolve(root, 'bin/docker');
  writeFileSync(dockerPath, FAKE_DOCKER);
  chmodSync(dockerPath, 0o755);
  const logPath = resolve(root, 'docker.log');
  writeFileSync(logPath, '');
  try {
    body({
      root,
      logPath,
      run(environment = {}) {
        return spawnSync('bash', [GATE, '--log-directory', resolve(root, 'logs')], {
          cwd: REPOSITORY_ROOT,
          encoding: 'utf8',
          env: {
            ...process.env,
            DOCKER: dockerPath,
            MIGRATION_IMAGE: 'vistara-migrations:test',
            FAKE_MIGRATION_IMAGE: 'vistara-migrations:test',
            FAKE_DOCKER_LOG: logPath,
            ...environment,
          },
        });
      },
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('passes when concurrent migration bundles both succeed with one ledger', () => {
  withGate(({ run, logPath }) => {
    const result = run();
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(result.stdout, /migration lock gate passed/i);

    const invocations = readFileSync(logPath, 'utf8').split('\n');
    assert.equal(
      invocations.filter((line) => line === 'migration-run').length,
      3,
      'two concurrent runs plus one idempotent re-run are required',
    );
    assert.ok(
      invocations.some((line) => line.startsWith('network create')),
      'the gate must create its own isolated network',
    );
  });
});

test('fails when a concurrent migration bundle is rejected', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_SECOND_MIGRATION_EXIT: '1' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /concurrent migration/i);
  });
});

test('fails when the migration ledger records duplicate applications', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_DUPLICATES: '1' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /duplicate/i);
  });
});

test('fails when no migration was applied at all', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_LEDGER_COUNT: '0' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /no migrations/i);
  });
});

test('fails when a repeated bundle run is not idempotent', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_LEDGER_COUNT: '7', FAKE_LEDGER_COUNT_AFTER: '8' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /idempotent/i);
  });
});

test('fails when the repeated bundle run reports an error', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_THIRD_MIGRATION_EXIT: '2' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /repeat/i);
  });
});
