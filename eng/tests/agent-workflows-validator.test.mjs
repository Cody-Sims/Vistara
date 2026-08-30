import assert from 'node:assert/strict';
import {
  appendFileSync,
  cpSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { dirname, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { validateAgentWorkflows } from '../validate-agent-workflows.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const FIXTURE = resolve(HERE, 'fixtures/agent-valid');

function withFixture(run) {
  const root = resolve(HERE, `.scratch-agent-${process.pid}-${randomUUID()}`);
  cpSync(FIXTURE, root, { recursive: true });
  try {
    run(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('accepts scoped instructions, one repository skill, and hardened workflows', () => {
  assert.doesNotThrow(() => validateAgentWorkflows(FIXTURE));
});

test('rejects instruction frontmatter with unsafe globs', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/instructions/tooling.instructions.md');
    writeFileSync(path, `---
description: Tooling rules
applyTo: '../**'
---

# Tooling

- Keep tooling deterministic.
`);
    assert.throws(() => validateAgentWorkflows(root), /unsafe applyTo glob/);
  });
});

test('rejects stale skill directories', () => {
  withFixture((root) => {
    mkdirSync(resolve(root, '.github/skills/obsolete'), { recursive: true });
    assert.throws(() => validateAgentWorkflows(root), /missing SKILL\.md/);
  });
});

test('rejects exact duplicate rules across instruction owners', () => {
  withFixture((root) => {
    const rule = '\n- Never duplicate this exact rule.\n';
    appendFileSync(resolve(root, 'AGENTS.md'), rule);
    appendFileSync(resolve(root, '.github/copilot-instructions.md'), rule);
    assert.throws(() => validateAgentWorkflows(root), /duplicate rule/);
  });
});

test('requires immutable action SHAs with version comments', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    const workflow = readFileSync(path, 'utf8').replace(
      'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1',
      'actions/checkout@v7',
    );
    writeFileSync(path, workflow);
    assert.throws(() => validateAgentWorkflows(root), /full 40-character SHA/);
  });
});

test('forbids pull_request_target workflows', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    const workflow = readFileSync(path, 'utf8').replace(
      'pull_request:',
      'pull_request_target: {}',
    );
    writeFileSync(path, workflow);
    assert.throws(() => validateAgentWorkflows(root), /pull_request_target/);
  });
});

test('accepts flow mappings, quoted values, and whitespace variants', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    writeFileSync(path, `name: "CI"

on: { pull_request: {} }

permissions: { contents: "read" }

concurrency: { group: "ci-\${{ github.ref }}", cancel-in-progress: true }

jobs:
  test:
    runs-on: "ubuntu-latest"
    timeout-minutes: 10
    steps:
      - uses: "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1" # v7.0.1
        with: { persist-credentials: false }
      - run: "echo test"
`);
    assert.doesNotThrow(() => validateAgentWorkflows(root));
  });
});

test('rejects quoted unpinned actions', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    const workflow = readFileSync(path, 'utf8').replace(
      'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1',
      '"actions/checkout@v7" # v7.0.1',
    );
    writeFileSync(path, workflow);
    assert.throws(() => validateAgentWorkflows(root), /full 40-character SHA/);
  });
});

test('rejects duplicate workflow keys', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    appendFileSync(path, `
permissions:
  contents: read
`);
    assert.throws(() => validateAgentWorkflows(root), /duplicate|unique/i);
  });
});

test('rejects catch-all instruction scopes in scalar, brace, and list forms', () => {
  const applyToForms = [
    "applyTo: '**/*'",
    "applyTo: '**/**'",
    "applyTo: '**/./**'",
    "applyTo: '{**}'",
    "applyTo: '**{,/*}'",
    "applyTo: '{eng/**,**/*}'",
    "applyTo: ['eng/**', '**']",
    "applyTo:\n  - 'eng/**'\n  - '**/**'",
  ];

  for (const applyTo of applyToForms) {
    withFixture((root) => {
      const path = resolve(root, '.github/instructions/tooling.instructions.md');
      writeFileSync(path, `---
description: Tooling rules
${applyTo}
---

# Tooling

- Keep tooling deterministic.
`);
      assert.throws(
        () => validateAgentWorkflows(root),
        /catch-all applyTo glob/,
        applyTo,
      );
    });
  }
});

test('requires bounded artifact uploads', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    const workflow = readFileSync(path, 'utf8').replace('          retention-days: 7\n', '');
    writeFileSync(path, workflow);
    assert.throws(() => validateAgentWorkflows(root), /retention-days/);
  });
});

test('requires bounded Pages artifact uploads', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/pages-artifact.yml');
    writeFileSync(path, `name: Pages artifact
on:
  pull_request:
permissions:
  contents: read
concurrency:
  group: pages-artifact
  cancel-in-progress: true
jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/upload-pages-artifact@fc324d3547104276b827a68afc52ff2a11cc49c9 # v5.0.0
        with:
          path: .
`);
    assert.throws(() => validateAgentWorkflows(root), /Pages artifact upload path must be bounded/);
  });
});

test('requires Pages deployment to be main-only', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/pages.yml');
    writeFileSync(path, `name: Pages
on:
  push:
    branches:
      - develop
permissions:
  contents: read
  pages: write
concurrency:
  group: pages
  cancel-in-progress: true
jobs:
  deploy:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/deploy-pages@decdde0ac072f6acb5d2f0344f372b3cae4c5f9b # v4.0.5
`);
    assert.throws(() => validateAgentWorkflows(root), /Pages deployment must be limited to main/);
  });
});

test('requires a main ref guard on Pages deploy jobs', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/pages.yml');
    writeFileSync(path, `name: Pages
on:
  workflow_dispatch:
  push:
    branches:
      - main
  pull_request:
permissions:
  contents: read
  pages: write
concurrency:
  group: pages
  cancel-in-progress: true
jobs:
  deploy:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/deploy-pages@decdde0ac072f6acb5d2f0344f372b3cae4c5f9b # v4.0.5
`);
    assert.throws(() => validateAgentWorkflows(root), /Pages deploy job must guard the main ref/);
  });
});

test('rejects Pages guards where an OR can bypass the main-only condition', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/pages.yml');
    writeFileSync(path, `name: Pages
on:
  push:
    branches:
      - main
permissions:
  contents: read
  pages: write
concurrency:
  group: pages
  cancel-in-progress: true
jobs:
  deploy:
    if: always() || (github.event_name != 'pull_request' && github.ref == 'refs/heads/main')
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/deploy-pages@decdde0ac072f6acb5d2f0344f372b3cae4c5f9b # v4.0.5
`);
    assert.throws(() => validateAgentWorkflows(root), /Pages deploy job must guard/);
  });
});

test('allows package writes only from release workflows', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/packages.yml');
    writeFileSync(path, `name: Packages
on:
  pull_request:
permissions:
  contents: read
  packages: write
concurrency:
  group: packages
  cancel-in-progress: true
jobs:
  publish:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - run: echo publish
`);
    assert.throws(() => validateAgentWorkflows(root), /package writes are release-only/);
  });
});

test('validates Copilot setup workflow shape when present', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/copilot-setup-steps.yml');
    writeFileSync(path, `name: Wrong setup
on:
  workflow_dispatch:
permissions:
  contents: read
concurrency:
  group: setup
  cancel-in-progress: true
jobs:
  setup:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - run: echo setup
`);
    assert.throws(() => validateAgentWorkflows(root), /Copilot setup workflow/);
  });
});

test('rejects pull-request workflows that read repository secrets', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    const workflow = readFileSync(path, 'utf8').replace(
      '      - run: echo test\n',
      '      - run: echo "${{ secrets.LIVE_PROVIDER_KEY }}"\n',
    );
    writeFileSync(path, workflow);
    assert.throws(
      () => validateAgentWorkflows(root),
      /must not read the secret LIVE_PROVIDER_KEY/,
    );
  });
});

test('rejects pull-request workflows that inherit secrets', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    const workflow = `${readFileSync(path, 'utf8')}    secrets: inherit\n`;
    writeFileSync(path, workflow);
    assert.throws(() => validateAgentWorkflows(root), /must not inherit secrets/);
  });
});

test('accepts the built-in token in pull-request workflows', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    const workflow = readFileSync(path, 'utf8').replace(
      '      - run: echo test\n',
      '      - run: gh api rate_limit\n        env:\n          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}\n',
    );
    writeFileSync(path, workflow);
    assert.doesNotThrow(() => validateAgentWorkflows(root));
  });
});

test('requires a Dependabot configuration', () => {
  withFixture((root) => {
    rmSync(resolve(root, '.github/dependabot.yml'));
    assert.throws(
      () => validateAgentWorkflows(root),
      /Dependabot configuration is required/,
    );
  });
});

test('requires Dependabot entries to declare a recurring schedule', () => {
  withFixture((root) => {
    writeFileSync(
      resolve(root, '.github/dependabot.yml'),
      `version: 2

updates:
  - package-ecosystem: github-actions
    directory: /
    schedule:
      interval: quarterly
    open-pull-requests-limit: 5
`,
    );
    assert.throws(
      () => validateAgentWorkflows(root),
      /schedule\.interval must be daily, weekly, or monthly/,
    );
  });
});

test('requires Dependabot to bound open pull requests', () => {
  withFixture((root) => {
    writeFileSync(
      resolve(root, '.github/dependabot.yml'),
      `version: 2

updates:
  - package-ecosystem: github-actions
    directory: /
    schedule:
      interval: weekly
`,
    );
    assert.throws(
      () => validateAgentWorkflows(root),
      /open-pull-requests-limit must be between 1 and 10/,
    );
  });
});

test('requires Dependabot to keep pinned actions current', () => {
  withFixture((root) => {
    writeFileSync(
      resolve(root, '.github/dependabot.yml'),
      `version: 2

updates:
  - package-ecosystem: nuget
    directory: /
    schedule:
      interval: weekly
    open-pull-requests-limit: 5
`,
    );
    assert.throws(
      () => validateAgentWorkflows(root),
      /github-actions updates are required/,
    );
  });
});

test('requires hidden artifact paths to include hidden files', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    writeFileSync(path, hiddenArtifactWorkflow(false));
    assert.throws(
      () => validateAgentWorkflows(root),
      /hidden paths require include-hidden-files/,
    );
  });
});

test('accepts hidden artifact paths that opt into hidden files', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    writeFileSync(path, hiddenArtifactWorkflow(true));
    assert.doesNotThrow(() => validateAgentWorkflows(root));
  });
});

test('leaves visible artifact paths unaffected', () => {
  withFixture((root) => {
    const path = resolve(root, '.github/workflows/ci.yml');
    writeFileSync(
      path,
      hiddenArtifactWorkflow(false).replace(
        'tests/Vistara.E2E/.artifacts/*.log',
        'tests/Vistara.E2E/reports/*.log',
      ),
    );
    assert.doesNotThrow(() => validateAgentWorkflows(root));
  });
});

function hiddenArtifactWorkflow(includeHiddenFiles) {
  return `name: CI

on:
  pull_request:

permissions:
  contents: read

concurrency:
  group: ci-\${{ github.ref }}
  cancel-in-progress: true

jobs:
  test:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          persist-credentials: false
      - run: echo test
      - uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
        with:
          name: diagnostics
          path: |
            tests/Vistara.E2E/playwright-report/**
            tests/Vistara.E2E/.artifacts/*.log
${includeHiddenFiles ? '          include-hidden-files: true\n' : ''}          if-no-files-found: ignore
          retention-days: 7
`;
}
