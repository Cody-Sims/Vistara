import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const GATE = resolve(REPOSITORY_ROOT, 'deploy/containers/tests/compose-startup.sh');

const FAKE_DOCKER = `#!/usr/bin/env bash
set -uo pipefail

printf '%s\\n' "$*" >> "$FAKE_DOCKER_LOG"

if [[ "$1" == "compose" ]]; then
  if [[ "$*" == *" config"* ]]; then
    exit "\${FAKE_CONFIG_EXIT:-0}"
  fi
  if [[ "$*" == *" up"* ]]; then
    exit "\${FAKE_UP_EXIT:-0}"
  fi
  if [[ "$*" == *" ps "* ]]; then
    # A real "docker compose ps --quiet" omits exited one-shot containers, so
    # the fake only reports a container when the gate passes --all.
    if [[ "\${!#}" == "--quiet" ]]; then
      if [[ -n "\${FAKE_EXISTING_CONTAINERS:-}" ]]; then
        printf '%s\\n' "\${FAKE_EXISTING_CONTAINERS}"
      fi
      exit 0
    fi
    if [[ "$*" == *" --all "* ]]; then
      printf '%s\\n' "container-\${!#}"
    fi
    exit 0
  fi
  exit 0
fi

if [[ "$1" == "inspect" ]]; then
  if [[ "$*" == *"State.Health"* ]]; then
    if [[ "$*" == *"\${FAKE_UNHEALTHY_SERVICE:-__none__}"* ]]; then
      echo "unhealthy"
    else
      echo "healthy"
    fi
    exit 0
  fi
  if [[ "$*" == *"ExitCode"* ]]; then
    echo "\${FAKE_EXIT_CODE:-0}"
    exit 0
  fi
fi
exit 0
`;

const FAKE_CURL = `#!/usr/bin/env bash
printf 'curl %s\\n' "$*" >> "$FAKE_DOCKER_LOG"
exit "\${FAKE_CURL_EXIT:-0}"
`;

function withGate(body) {
  const root = resolve(HERE, `.scratch-compose-${process.pid}-${randomUUID()}`);
  mkdirSync(resolve(root, 'bin'), { recursive: true });
  const dockerPath = resolve(root, 'bin/docker');
  const curlPath = resolve(root, 'bin/curl');
  writeFileSync(dockerPath, FAKE_DOCKER);
  writeFileSync(curlPath, FAKE_CURL);
  chmodSync(dockerPath, 0o755);
  chmodSync(curlPath, 0o755);
  const logPath = resolve(root, 'docker.log');
  writeFileSync(logPath, '');
  const envFile = resolve(root, 'stack.env');
  writeFileSync(envFile, 'VISTARA_IMAGE_TAG=test\n');

  try {
    body({
      logPath,
      run(environment = {}, extraArguments = []) {
        return spawnSync(
          'bash',
          [
            GATE,
            '--file',
            'deploy/compose.starter.yml',
            '--env-file',
            envFile,
            '--service',
            'api',
            '--service',
            'proxy',
            '--completed',
            'migrate',
            '--probe',
            'http://127.0.0.1:8080/health/ready',
            ...extraArguments,
          ],
          {
            cwd: REPOSITORY_ROOT,
            encoding: 'utf8',
            env: {
              ...process.env,
              DOCKER: dockerPath,
              CURL: curlPath,
              FAKE_DOCKER_LOG: logPath,
              ...environment,
            },
          },
        );
      },
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('passes when every service is healthy, one-shot jobs succeed, and probes answer', () => {
  withGate(({ run, logPath }) => {
    const result = run();
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(result.stdout, /compose startup gate passed/i);

    const invocations = readFileSync(logPath, 'utf8');
    assert.match(invocations, /compose .*--wait/);
    assert.match(invocations, /curl .*health\/ready/);
    assert.match(invocations, /compose .*down --volumes/);
  });
});

test('fails when a required service never becomes healthy', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_UNHEALTHY_SERVICE: 'container-proxy' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /proxy reported health status 'unhealthy'/);
  });
});

test('fails when the migration job exits nonzero', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_EXIT_CODE: '3' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /migrate exited with 3/);
  });
});

test('fails when a health probe does not answer', () => {
  withGate(({ run }) => {
    const result = run({ FAKE_CURL_EXIT: '7' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /health\/ready did not answer/);
  });
});

test('fails and tears the stack down when startup times out', () => {
  withGate(({ run, logPath }) => {
    const result = run({ FAKE_UP_EXIT: '1' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /did not start successfully/);
    assert.match(readFileSync(logPath, 'utf8'), /compose .*down --volumes/);
  });
});

test('rejects an invalid compose file before starting containers', () => {
  withGate(({ run, logPath }) => {
    const result = run({ FAKE_CONFIG_EXIT: '1' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /is not valid/);
    assert.doesNotMatch(readFileSync(logPath, 'utf8'), /compose .* up /);
  });
});

test('requires an existing environment file', () => {
  withGate(({ run }) => {
    const result = run({}, ['--env-file', 'deploy/.env.absent']);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /was not found/);
  });
});

test('inspects exited one-shot containers by listing all project containers', () => {
  withGate(({ run, logPath }) => {
    const result = run();
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);

    const invocations = readFileSync(logPath, 'utf8');
    assert.match(invocations, /compose .* ps --all --quiet migrate/);
    assert.match(invocations, /compose .* ps --all --quiet api/);
  });
});

test('generates a unique project when none is supplied', () => {
  withGate(({ run, logPath }) => {
    const result = run();
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);

    const invocations = readFileSync(logPath, 'utf8');
    const generated = invocations.match(/--project-name (vistara-gate-[0-9]+-[0-9]+)/);
    assert.ok(generated, 'the gate must name its own project');
    assert.match(
      invocations,
      new RegExp(`--project-name ${generated[1]} down --volumes`),
    );
    assert.match(result.stdout, new RegExp(`project ${generated[1]}`));
  });
});

test('refuses a supplied project that already has containers', () => {
  withGate(({ run, logPath }) => {
    const result = run({ FAKE_EXISTING_CONTAINERS: 'deadbeefcafe' }, [
      '--project',
      'vistara-postgres',
    ]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /vistara-postgres already has containers/);

    const invocations = readFileSync(logPath, 'utf8');
    assert.doesNotMatch(invocations, /down --volumes/);
    assert.doesNotMatch(invocations, /compose .* up /);
  });
});

test('accepts a supplied project that has no containers', () => {
  withGate(({ run, logPath }) => {
    const result = run({}, ['--project', 'vistara-starter-gate']);
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(
      readFileSync(logPath, 'utf8'),
      /--project-name vistara-starter-gate down --volumes/,
    );
  });
});
