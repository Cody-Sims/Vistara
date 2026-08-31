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
    if [[ -n "\${FAKE_PRE_REPLAY_SERVER_SQLSTATE:-}" ]]; then
      echo "2026-08-31 03:41:39.482 UTC [113] ERROR:  \${FAKE_PRE_REPLAY_SERVER_SQLSTATE}: relation \\"__EFMigrationsHistory\\" already exists"
    fi
    if [[ "\${FAKE_REPLAY_LOG_STYLE:-bundle}" == "server" ]] && log_contains "replay-run"; then
      echo "2026-08-31 03:41:40.111 UTC [117] ERROR:  \${FAKE_REPLAY_SQLSTATE:-42P07}: relation \\"assets\\" already exists"
      echo "2026-08-31 03:41:40.111 UTC [117] STATEMENT:  CREATE TABLE assets ()"
    fi
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
      pin="$(printf '%s' "$*" | grep --extended-regexp --only-matching "'[^']+'" | tail -n 1 | tr -d "'")"
      printf '%s %s\\n' "replay-pin" "$pin" >> "$FAKE_DOCKER_LOG"
      printf '%s\\n' "replay-phase" >> "$FAKE_DOCKER_LOG"
      echo "DELETE 1"
      exit 0
    fi
    if [[ "$*" == *"ORDER BY \\"MigrationId\\" ASC"* ]]; then
      echo "\${FAKE_OLDEST_MIGRATION:-20260829000000_InitialCreate}"
      exit 0
    fi
    if [[ "$*" == *"WHERE \\"MigrationId\\" >="* ]]; then
      echo "\${FAKE_REPLAY_LEDGER_ROWS:-4}"
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
        printf '%s\\n' "replay-run" >> "$FAKE_DOCKER_LOG"
        pin="$(grep '^replay-pin ' "$FAKE_DOCKER_LOG" | tail -n 1 | cut -d ' ' -f 2)"
        if [[ "$pin" != "\${FAKE_OLDEST_MIGRATION:-20260829000000_InitialCreate}" ]]; then
          # The newest migrations only move data, so replaying them succeeds.
          echo "No migrations were applied. The database is already up to date."
          exit "\${FAKE_DATA_ONLY_REPLAY_EXIT:-0}"
        fi
        if [[ "\${FAKE_REPLAY_LOG_STYLE:-bundle}" == "bundle" ]]; then
          echo "    SqlState: \${FAKE_REPLAY_SQLSTATE:-42P07}"
          echo "\${FAKE_REPLAY_SQLSTATE:-42P07}: relation \\"assets\\" already exists"
        else
          echo "Npgsql.PostgresException: relation \\"assets\\" already exists"
        fi
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

const OLDEST_MIGRATION = '20260829000000_InitialCreate';
const NEWEST_DATA_ONLY_MIGRATION = '20260901120000_BackfillAssetOwners';
const DUPLICATE_OBJECT_SQLSTATES = ['42P07', '42701', '42710', '42723', '42P06', '42P04'];

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

test('replays the oldest applied migration and every later ledger row', () => {
  withGate(({ run, logPath }) => {
    const result = run();
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);

    const invocations = readFileSync(logPath, 'utf8').split('\n');
    assert.ok(
      invocations.some((line) =>
        line.includes(`DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" >= '${OLDEST_MIGRATION}'`)),
      'the probe must clear the oldest applied migration and everything after it',
    );
    assert.ok(
      !invocations.some((line) => line.includes('ORDER BY "MigrationId" DESC')),
      'the probe must not assume the newest migration conflicts',
    );
  });
});

for (const sqlstate of DUPLICATE_OBJECT_SQLSTATES) {
  test(`accepts a replay rejected with SQLSTATE ${sqlstate}`, () => {
    withGate(({ run }) => {
      const result = run({ FAKE_REPLAY_SQLSTATE: sqlstate });
      assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
      assert.match(result.stdout, /migration lock gate passed/i);
    });
  });
}

test('accepts a replay whose SQLSTATE only reaches the PostgreSQL server log', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_REPLAY_LOG_STYLE: 'server', FAKE_REPLAY_SQLSTATE: '42710' });
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(result.stdout, /migration lock gate passed/i);
  });
});

test('fails when a data-only migration is pinned and the replay succeeds', () => {
  withGate(({ run }) => {
    const result = run({}, [
      '--concurrency',
      String(CONCURRENCY),
      '--replay-migration',
      NEWEST_DATA_ONLY_MIGRATION,
    ]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /must fail the migration bundle/i);
  });
});

test('fails when a replayed migration is swallowed instead of failing', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_REPLAY_EXIT: '0' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /must fail the migration bundle/i);
  });
});

test('fails when the replay is rejected for a reason other than a duplicate object', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_REPLAY_SQLSTATE: '42501' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /duplicate object SQLSTATE/i);
  });
});

test('fails when neither the bundle nor the server reports a SQLSTATE', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_REPLAY_LOG_STYLE: 'none' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /duplicate object SQLSTATE/i);
  });
});

test('ignores a duplicate SQLSTATE logged before the replay started', () => {
  withGate(({ run }) => {
    const result = run({
      FAKE_REPLAY_LOG_STYLE: 'none',
      FAKE_PRE_REPLAY_SERVER_SQLSTATE: '42P07',
    });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /duplicate object SQLSTATE/i);
  });
});

test('fails when the pinned migration is missing from the ledger', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_REPLAY_LEDGER_ROWS: '0' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /no ledger row/i);
  });
});
