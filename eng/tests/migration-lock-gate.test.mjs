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
const CONCURRENCY = 2;

const FAKE_DOCKER = `#!/usr/bin/env bash
set -uo pipefail

printf '%s\\n' "$*" >> "$FAKE_DOCKER_LOG"

log_contains() {
  grep --quiet --fixed-strings "$1" "$FAKE_DOCKER_LOG"
}

claim_concurrent_index() {
  local index=1
  while ! mkdir "$FAKE_CLAIM_DIR/$index" 2>/dev/null; do
    index=$((index + 1))
  done
  printf '%s' "$index"
}

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
    if [[ "$*" == *"DELETE FROM"* ]]; then
      printf '%s\\n' "replay-phase" >> "$FAKE_DOCKER_LOG"
      echo "DELETE 1"
      exit 0
    fi
    if [[ "$*" == *"ORDER BY \\"MigrationId\\" DESC"* ]]; then
      echo "\${FAKE_NEWEST_MIGRATION:-20260831002849_AddUserPreferences}"
      exit 0
    fi
    if [[ "$*" == *__EFMigrationsHistory* ]]; then
      printf '%s\\n' "ledger-read" >> "$FAKE_DOCKER_LOG"
      if log_contains "repeat-run"; then
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
      if [[ -n "\${FAKE_LIBRARY_ERROR:-}" ]]; then
        echo "Cannot load library libgssapi_krb5.so.2"
        echo "Error: libgssapi_krb5.so.2: cannot open shared object file: No such file or directory"
      fi
      if log_contains "replay-phase"; then
        echo "\${FAKE_REPLAY_OUTPUT:-42P07: relation \\"user_preferences\\" already exists}"
        exit "\${FAKE_REPLAY_EXIT:-1}"
      fi
      if log_contains "ledger-read"; then
        printf '%s\\n' "repeat-run" >> "$FAKE_DOCKER_LOG"
        exit "\${FAKE_REPEAT_EXIT:-0}"
      fi
      index="$(claim_concurrent_index)"
      if [[ "$index" == "\${FAKE_FAILING_INDEX:-}" ]]; then
        echo "42701: column \\"activated_asset_id\\" of relation \\"upload_sessions\\" already exists"
        exit 1
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
  mkdirSync(resolve(root, 'claims'), { recursive: true });
  const dockerPath = resolve(root, 'bin/docker');
  writeFileSync(dockerPath, FAKE_DOCKER);
  chmodSync(dockerPath, 0o755);
  const logPath = resolve(root, 'docker.log');
  writeFileSync(logPath, '');
  try {
    body({
      root,
      logPath,
      run(environment = {}, argv = ['--concurrency', String(CONCURRENCY)]) {
        return spawnSync('bash', [GATE, '--log-directory', resolve(root, 'logs'), ...argv], {
          cwd: REPOSITORY_ROOT,
          encoding: 'utf8',
          env: {
            ...process.env,
            DOCKER: dockerPath,
            MIGRATION_IMAGE: 'vistara-migrations:test',
            FAKE_MIGRATION_IMAGE: 'vistara-migrations:test',
            FAKE_DOCKER_LOG: logPath,
            FAKE_CLAIM_DIR: resolve(root, 'claims'),
            ...environment,
          },
        });
      },
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('passes when every concurrent migration bundle succeeds with one ledger', () => {
  withGate(({ run, logPath }) => {
    const result = run();
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(result.stdout, /migration lock gate passed/i);

    const invocations = readFileSync(logPath, 'utf8').split('\n');
    assert.equal(
      invocations.filter((line) => line === 'migration-run').length,
      CONCURRENCY + 2,
      'the concurrent runs plus the idempotent re-run and the replay probe are required',
    );
    assert.ok(
      invocations.some((line) => line.startsWith('network create')),
      'the gate must create its own isolated network',
    );
  });
});

test('rejects a concurrency that cannot exercise the migration lock', () => {
  withGate(({ run }) => {
    const result = run({}, ['--concurrency', '1']);
    assert.equal(result.status, 64);
    assert.match(result.stderr, /concurrency/i);
  });
});

test('fails when one concurrent migration bundle is rejected', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_FAILING_INDEX: '2' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /concurrent migration/i);
  });
});

test('fails when a migration bundle cannot load a native library', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_LIBRARY_ERROR: '1' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /missing native library/i);
    assert.match(result.stderr, /libgssapi_krb5/);
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
    const result = run({ FAKE_REPEAT_EXIT: '2' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /repeat/i);
  });
});

test('fails when a replayed migration is swallowed instead of failing', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_REPLAY_EXIT: '0' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /must fail the migration bundle/i);
  });
});

test('fails when a replayed migration hides the underlying database error', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_REPLAY_OUTPUT: 'the bundle gave up' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /underlying PostgreSQL error/i);
  });
});
