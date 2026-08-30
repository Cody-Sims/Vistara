#!/usr/bin/env node

import {
  existsSync,
  lstatSync,
  readdirSync,
  readFileSync,
} from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseDocument } from 'yaml';

const REPOSITORY_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const INSTRUCTION_KEYS = ['description', 'applyTo'];
const SKILL_KEYS = [
  'argument-hint',
  'compatibility',
  'description',
  'disable-model-invocation',
  'license',
  'metadata',
  'name',
  'user-invocable',
];
const SKILL_CHILD_DIRECTORIES = new Set(['assets', 'references', 'scripts']);
const PERMISSION_LEVELS = new Set(['none', 'read', 'write']);
const VERSION_COMMENT = /^v\d+(?:\.\d+){0,2}(?:[-+][A-Za-z0-9.-]+)?$/;
const DEPENDABOT_INTERVALS = new Set(['daily', 'weekly', 'monthly']);

function fail(message) {
  throw new Error(message);
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function assertObject(value, label) {
  if (!isObject(value)) fail(`${label} must be a mapping.`);
}

function assertExactKeys(value, keys, label) {
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    fail(`${label} has an invalid schema shape.`);
  }
}

function listFiles(root, predicate = () => true) {
  if (!existsSync(root)) return [];
  const files = [];
  const visit = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = resolve(directory, entry.name);
      if (entry.isDirectory()) visit(path);
      else if (entry.isFile() && predicate(path)) files.push(path);
    }
  };
  visit(root);
  return files.sort();
}

function parseYaml(source, label) {
  const document = parseDocument(source, {
    prettyErrors: true,
    strict: true,
    uniqueKeys: true,
    version: '1.2',
  });
  if (document.errors.length > 0) {
    fail(`${label}: invalid YAML: ${document.errors.map((error) => error.message).join('; ')}`);
  }

  let value;
  try {
    value = document.toJS({ maxAliasCount: 0 });
  } catch (error) {
    fail(`${label}: invalid YAML: ${error instanceof Error ? error.message : error}`);
  }
  return { document, value };
}

function parseFrontmatter(path) {
  const content = readFileSync(path, 'utf8');
  const match = content.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n/);
  if (!match) fail(`${path}: missing YAML frontmatter.`);
  const parsed = parseYaml(match[1], path);
  assertObject(parsed.value, path);
  return {
    content,
    body: content.slice(match[0].length),
    data: parsed.value,
  };
}

function splitGlobScalar(value, path) {
  const globs = [];
  let start = 0;
  let braceDepth = 0;
  let bracketDepth = 0;
  for (const [index, character] of [...value].entries()) {
    if (character === '{') braceDepth++;
    else if (character === '}') braceDepth--;
    else if (character === '[') bracketDepth++;
    else if (character === ']') bracketDepth--;
    else if (character === ',' && braceDepth === 0 && bracketDepth === 0) {
      globs.push(value.slice(start, index).trim());
      start = index + 1;
    }
    if (braceDepth < 0 || bracketDepth < 0) {
      fail(`${path}: malformed applyTo glob list.`);
    }
  }
  if (braceDepth !== 0 || bracketDepth !== 0) {
    fail(`${path}: malformed applyTo glob list.`);
  }
  globs.push(value.slice(start).trim());
  return globs;
}

function readApplyToGlobs(value, path) {
  const values = Array.isArray(value) ? value : [value];
  if (values.some((item) => typeof item !== 'string')) {
    fail(`${path}: applyTo must be a string or list of strings.`);
  }
  return values.flatMap((item) => splitGlobScalar(item, path));
}

function splitBraceAlternatives(value) {
  const alternatives = [];
  let start = 0;
  let depth = 0;
  for (const [index, character] of [...value].entries()) {
    if (character === '{') depth++;
    else if (character === '}') depth--;
    else if (character === ',' && depth === 0) {
      alternatives.push(value.slice(start, index));
      start = index + 1;
    }
  }
  alternatives.push(value.slice(start));
  return alternatives;
}

function expandBraces(value, limit = 64) {
  const open = value.indexOf('{');
  if (open < 0) return [value];

  let depth = 0;
  let close = -1;
  for (let index = open; index < value.length; index++) {
    if (value[index] === '{') depth++;
    else if (value[index] === '}') {
      depth--;
      if (depth === 0) {
        close = index;
        break;
      }
    }
  }
  if (close < 0) return [value];

  const prefix = value.slice(0, open);
  const suffix = value.slice(close + 1);
  const expanded = [];
  for (const alternative of splitBraceAlternatives(value.slice(open + 1, close))) {
    for (const candidate of expandBraces(`${prefix}${alternative}${suffix}`, limit)) {
      expanded.push(candidate);
      if (expanded.length > limit) return ['**'];
    }
  }
  return expanded;
}

function isCatchAllGlob(glob) {
  return expandBraces(glob).some((candidate) => {
    let normalized = candidate.trim();
    while (normalized.startsWith('./')) normalized = normalized.slice(2);
    const segments = normalized
      .split('/')
      .filter((segment) => segment.length > 0 && segment !== '.');
    return segments.length > 0
      && segments[0] === '**'
      && segments.every((segment) => segment === '*' || segment === '**');
  });
}

function validateGlob(glob, path) {
  if (glob.length === 0
      || glob.startsWith('/')
      || glob.includes('\\')
      || glob.includes('//')
      || glob.split('/').includes('..')
      || !/^[A-Za-z0-9._/*?{},[\]-]+$/.test(glob)) {
    fail(`${path}: unsafe applyTo glob ${glob}.`);
  }
  if (isCatchAllGlob(glob)) {
    fail(`${path}: catch-all applyTo glob ${glob} is forbidden.`);
  }
  const pairs = [['{', '}'], ['[', ']']];
  for (const [open, close] of pairs) {
    if ((glob.match(new RegExp(`\\${open}`, 'g')) ?? []).length
        !== (glob.match(new RegExp(`\\${close}`, 'g')) ?? []).length) {
      fail(`${path}: malformed applyTo glob ${glob}.`);
    }
  }
}

function validateInstructions(root) {
  const instructionsRoot = resolve(root, '.github/instructions');
  const files = listFiles(instructionsRoot);
  for (const path of files) {
    const repositoryPath = relative(root, path);
    if (!path.endsWith('.instructions.md') || dirname(path) !== instructionsRoot) {
      fail(`${repositoryPath}: stale or noncanonical instruction file.`);
    }
    const frontmatter = parseFrontmatter(path);
    assertExactKeys(frontmatter.data, INSTRUCTION_KEYS, repositoryPath);
    if (typeof frontmatter.data.description !== 'string'
        || frontmatter.data.description.trim().length === 0) {
      fail(`${repositoryPath}: description is required.`);
    }
    const globs = readApplyToGlobs(frontmatter.data.applyTo, repositoryPath);
    if (globs.some((glob) => glob.length === 0) || new Set(globs).size !== globs.length) {
      fail(`${repositoryPath}: applyTo globs must be non-empty and unique.`);
    }
    for (const glob of globs) validateGlob(glob, repositoryPath);
  }
  return files;
}

function validateSkill(root, skillRoot) {
  const path = resolve(skillRoot, 'SKILL.md');
  const repositoryPath = relative(root, path);
  if (!existsSync(path)) fail(`${relative(root, skillRoot)}: missing SKILL.md.`);
  const frontmatter = parseFrontmatter(path);
  for (const key of Object.keys(frontmatter.data)) {
    if (!SKILL_KEYS.includes(key)) fail(`${repositoryPath}: forbidden frontmatter key ${key}.`);
  }
  for (const required of ['name', 'description', 'license', 'metadata']) {
    if (!Object.hasOwn(frontmatter.data, required)) {
      fail(`${repositoryPath}: missing frontmatter key ${required}.`);
    }
  }

  const name = frontmatter.data.name;
  const directoryName = skillRoot.split('/').at(-1);
  if (typeof name !== 'string'
      || name !== directoryName
      || !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(name)
      || name.length > 64) {
    fail(`${repositoryPath}: skill name must match its directory.`);
  }
  const description = frontmatter.data.description;
  if (typeof description !== 'string'
      || description.length > 1024
      || !description.includes('Use when ')
      || /[<>]/.test(description)) {
    fail(`${repositoryPath}: skill description must state when to use it.`);
  }
  if (frontmatter.data.license !== 'MIT') {
    fail(`${repositoryPath}: skill license must be MIT.`);
  }

  const metadata = frontmatter.data.metadata;
  assertObject(metadata, `${repositoryPath} metadata`);
  assertExactKeys(metadata, ['author', 'tier', 'version'], `${repositoryPath} metadata`);
  if (typeof metadata.version !== 'string'
      || !/^\d+\.\d+\.\d+$/.test(metadata.version)
      || !['core', 'extended', 'experimental'].includes(metadata.tier)
      || typeof metadata.author !== 'string'
      || metadata.author.trim().length === 0) {
    fail(`${repositoryPath}: invalid skill metadata.`);
  }
  if (frontmatter.content.split(/\r?\n/).length > 500) {
    fail(`${repositoryPath}: SKILL.md exceeds 500 lines.`);
  }
  for (const match of frontmatter.body.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
    const target = match[1];
    if (/^[a-z]+:/i.test(target) || target.startsWith('#')) continue;
    if (target.startsWith('/') || target.split('/').includes('..')
        || !existsSync(resolve(skillRoot, target))) {
      fail(`${repositoryPath}: unresolved relative link ${target}.`);
    }
  }
  for (const entry of readdirSync(skillRoot, { withFileTypes: true })) {
    if (entry.isDirectory() && !SKILL_CHILD_DIRECTORIES.has(entry.name)) {
      fail(`${relative(root, skillRoot)}: stale skill directory ${entry.name}.`);
    }
  }
}

function validateSkills(root) {
  const skillsRoot = resolve(root, '.github/skills');
  if (!existsSync(skillsRoot)) fail('.github/skills/maintain-shadow is required.');
  const entries = readdirSync(skillsRoot, { withFileTypes: true });
  if (entries.some((entry) => !entry.isDirectory())) {
    fail('.github/skills must contain only skill directories.');
  }
  for (const entry of entries) {
    if (!existsSync(resolve(skillsRoot, entry.name, 'SKILL.md'))) {
      fail(`${relative(root, resolve(skillsRoot, entry.name))}: missing SKILL.md.`);
    }
  }
  const names = entries.map((entry) => entry.name).sort();
  if (JSON.stringify(names) !== JSON.stringify(['maintain-shadow'])) {
    fail('.github/skills must contain exactly the maintain-shadow repository skill.');
  }
  validateSkill(root, resolve(skillsRoot, 'maintain-shadow'));

  const agentsRoot = resolve(root, '.github/agents');
  if (existsSync(agentsRoot) && listFiles(agentsRoot).length > 0) {
    fail('Custom agents are not permitted in this repository.');
  }
}

function validateDuplicateRules(root, instructionFiles) {
  const files = [
    resolve(root, 'AGENTS.md'),
    resolve(root, '.github/copilot-instructions.md'),
    ...instructionFiles,
  ].filter(existsSync);
  const owners = new Map();
  for (const path of files) {
    for (const line of readFileSync(path, 'utf8').split(/\r?\n/)) {
      const match = line.match(/^\s*-\s+(.+\S)\s*$/);
      if (!match) continue;
      const rule = match[1].replace(/\s+/g, ' ').trim();
      const previous = owners.get(rule);
      if (previous && previous !== path) {
        fail(`Exact duplicate rule appears in ${relative(root, previous)} and ${relative(root, path)}: ${rule}`);
      }
      owners.set(rule, path);
    }
  }
}

function normalizeTriggers(value, path) {
  if (typeof value === 'string') return { [value]: null };
  if (Array.isArray(value)) {
    if (value.some((trigger) => typeof trigger !== 'string')) {
      fail(`${path}: workflow triggers must be strings or a mapping.`);
    }
    return Object.fromEntries(value.map((trigger) => [trigger, null]));
  }
  assertObject(value, `${path} on`);
  return value;
}

function validatePermissions(value, label) {
  assertObject(value, label);
  for (const [permission, level] of Object.entries(value)) {
    if (!/^[a-z][a-z-]*$/.test(permission)
        || typeof level !== 'string'
        || !PERMISSION_LEVELS.has(level)) {
      fail(`${label}: invalid permission ${permission}.`);
    }
  }
}

function validateConcurrency(value, path) {
  assertObject(value, `${path} concurrency`);
  if (typeof value.group !== 'string' || value.group.trim().length === 0) {
    fail(`${path}: concurrency.group is required.`);
  }
  if (!Object.hasOwn(value, 'cancel-in-progress')
      || (typeof value['cancel-in-progress'] !== 'boolean'
          && typeof value['cancel-in-progress'] !== 'string')) {
    fail(`${path}: concurrency.cancel-in-progress is required.`);
  }
}

function getActionReference(value, path) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    fail(`${path}: uses must be a non-empty string.`);
  }
  if (value.startsWith('./') || value.startsWith('docker://')) {
    return { name: value, reference: null };
  }
  const separator = value.lastIndexOf('@');
  if (separator <= 0 || separator === value.length - 1) {
    fail(`${path}: action references must include @<sha>.`);
  }
  return {
    name: value.slice(0, separator),
    reference: value.slice(separator + 1),
  };
}

function boundedArtifactPath(value) {
  if (typeof value !== 'string') return false;
  const paths = value.split(/\r?\n/).map((path) => path.trim()).filter(Boolean);
  return paths.length > 0 && paths.every((path) =>
    !['.', './', '/', '**', '**/*', '**/**'].includes(path)
    && !path.startsWith('/')
    && !path.split('/').includes('..')
    && !isCatchAllGlob(path));
}

function collectActionUses(workflow, document, path) {
  const uses = [];
  for (const [jobName, job] of Object.entries(workflow.jobs)) {
    assertObject(job, `${path}: job ${jobName}`);
    if (Object.hasOwn(job, 'uses')) {
      uses.push({
        jobName,
        step: job,
        value: job.uses,
        node: document.getIn(['jobs', jobName, 'uses'], true),
      });
    }
    if (!Object.hasOwn(job, 'steps')) continue;
    if (!Array.isArray(job.steps)) fail(`${path}: job ${jobName} steps must be a list.`);
    for (const [stepIndex, step] of job.steps.entries()) {
      assertObject(step, `${path}: job ${jobName} step ${stepIndex + 1}`);
      if (!Object.hasOwn(step, 'uses')) continue;
      uses.push({
        jobName,
        step,
        value: step.uses,
        node: document.getIn(['jobs', jobName, 'steps', stepIndex, 'uses'], true),
      });
    }
  }
  return uses;
}

function validateActionUse(use, path) {
  const action = getActionReference(use.value, path);
  if (action.reference === null) return action;
  if (!/^[0-9a-f]{40}$/.test(action.reference)) {
    fail(`${path}: ${action.name} must use a full 40-character SHA.`);
  }
  const comment = typeof use.node?.comment === 'string' ? use.node.comment.trim() : '';
  if (!VERSION_COMMENT.test(comment)) {
    fail(`${path}: ${action.name} SHA requires a version comment.`);
  }

  if (action.name === 'actions/checkout'
      && use.step.with?.['persist-credentials'] !== false) {
    fail(`${path}: actions/checkout must set persist-credentials to false.`);
  }
  if (action.name === 'actions/upload-artifact') {
    const retention = use.step.with?.['retention-days'];
    if (!Number.isInteger(retention) || retention < 1 || retention > 30) {
      fail(`${path}: artifact uploads require retention-days between 1 and 30.`);
    }
    if (!boundedArtifactPath(use.step.with?.path)) {
      fail(`${path}: artifact upload path must be bounded.`);
    }
    if (!['error', 'ignore', 'warn'].includes(use.step.with?.['if-no-files-found'])) {
      fail(`${path}: artifact uploads require if-no-files-found.`);
    }
  }
  if (action.name === 'actions/upload-pages-artifact') {
    if (!boundedArtifactPath(use.step.with?.path)) {
      fail(`${path}: Pages artifact upload path must be bounded.`);
    }
    const retention = use.step.with?.['retention-days'];
    if (!Number.isInteger(retention) || retention < 1 || retention > 30) {
      fail(`${path}: Pages artifact uploads require retention-days between 1 and 30.`);
    }
  }
  return action;
}

function asStringList(value) {
  if (typeof value === 'string') return [value];
  return Array.isArray(value) && value.every((item) => typeof item === 'string')
    ? value
    : [];
}

function validatePagesBranches(triggers, path) {
  const push = triggers.push;
  if (!isObject(push)
      || JSON.stringify(asStringList(push.branches)) !== JSON.stringify(['main'])) {
    fail(`${path}: Pages deployment must be limited to main.`);
  }
}

const permissionIsWrite = (permissions, name) =>
  isObject(permissions) && permissions[name] === 'write';

function hasRequiredPagesGuard(condition) {
  if (typeof condition !== 'string') return false;
  let expression = condition.trim();
  if (expression.startsWith('${{') && expression.endsWith('}}')) {
    expression = expression.slice(3, -2).trim();
  }
  if (expression.includes('||')) return false;

  const clauses = expression.split('&&').map((clause) => clause.trim());
  if (clauses.length !== 2) return false;
  const eventGuard = /^github\.event_name\s*!=\s*(['"])pull_request\1$/;
  const refGuard = /^(?:github\.ref\s*==\s*(['"])refs\/heads\/main\1|github\.ref_name\s*==\s*(['"])main\2)$/;
  return clauses.some((clause) => eventGuard.test(clause))
    && clauses.some((clause) => refGuard.test(clause));
}

function validatePullRequestSecretIsolation(source, path) {
  if (/secrets\s*:\s*inherit/.test(source)) {
    fail(`${path}: pull-request workflows must not inherit secrets.`);
  }
  for (const match of source.matchAll(/secrets\.([A-Za-z_][A-Za-z0-9_-]*)/g)) {
    if (match[1] !== 'GITHUB_TOKEN') {
      fail(`${path}: pull-request workflows must not read the secret ${match[1]}.`);
    }
  }
}

function validateWorkflow(root, path) {
  const repositoryPath = relative(root, path);
  const source = readFileSync(path, 'utf8');
  const parsed = parseYaml(source, repositoryPath);
  assertObject(parsed.value, repositoryPath);
  const workflow = parsed.value;
  if (!Object.hasOwn(workflow, 'on')) fail(`${repositoryPath}: on is required.`);
  const triggers = normalizeTriggers(workflow.on, repositoryPath);
  if (Object.hasOwn(triggers, 'pull_request_target')) {
    fail(`${repositoryPath}: pull_request_target is forbidden.`);
  }
  if (!Object.hasOwn(workflow, 'permissions')) {
    fail(`${repositoryPath}: explicit top-level permissions are required.`);
  }
  validatePermissions(workflow.permissions, `${repositoryPath} permissions`);
  if (Object.hasOwn(triggers, 'pull_request')) {
    validatePullRequestSecretIsolation(source, repositoryPath);
  }
  if (!Object.hasOwn(workflow, 'concurrency')) {
    fail(`${repositoryPath}: explicit concurrency is required.`);
  }
  validateConcurrency(workflow.concurrency, repositoryPath);
  assertObject(workflow.jobs, `${repositoryPath} jobs`);
  if (Object.keys(workflow.jobs).length === 0) {
    fail(`${repositoryPath}: at least one job is required.`);
  }

  for (const [jobName, job] of Object.entries(workflow.jobs)) {
    assertObject(job, `${repositoryPath}: job ${jobName}`);
    if (!Number.isInteger(job['timeout-minutes']) || job['timeout-minutes'] < 1) {
      fail(`${repositoryPath}: job ${jobName} needs an explicit timeout-minutes.`);
    }
    if (Object.hasOwn(job, 'permissions')) {
      validatePermissions(job.permissions, `${repositoryPath}: job ${jobName} permissions`);
    }
  }

  const actionUses = collectActionUses(workflow, parsed.document, repositoryPath);
  const actions = actionUses.map((use) => ({
    ...validateActionUse(use, repositoryPath),
    jobName: use.jobName,
  }));
  const deploysPages = actions.some((action) => action.name === 'actions/deploy-pages')
    || permissionIsWrite(workflow.permissions, 'pages')
    || Object.values(workflow.jobs).some((job) => permissionIsWrite(job.permissions, 'pages'));
  if (deploysPages) {
    validatePagesBranches(triggers, repositoryPath);
    for (const action of actions.filter((candidate) => candidate.name === 'actions/deploy-pages')) {
      const condition = workflow.jobs[action.jobName].if;
      if (!hasRequiredPagesGuard(condition)) {
        fail(`${repositoryPath}: Pages deploy job must guard the main ref and exclude pull requests.`);
      }
    }
  }

  const packageWrite = permissionIsWrite(workflow.permissions, 'packages')
    || Object.values(workflow.jobs).some((job) => permissionIsWrite(job.permissions, 'packages'));
  if (packageWrite) {
    const push = triggers.push;
    const tagTriggered = isObject(push) && asStringList(push.tags).length > 0;
    const branchTriggered = isObject(push) && asStringList(push.branches).length > 0;
    const releaseTriggered = Object.hasOwn(triggers, 'release');
    if ((!releaseTriggered && !tagTriggered)
        || branchTriggered
        || Object.hasOwn(triggers, 'pull_request')) {
      fail(`${repositoryPath}: package writes are release-only.`);
    }
  }

  if (repositoryPath === '.github/workflows/copilot-setup-steps.yml') {
    const setupJob = workflow.jobs['copilot-setup-steps'];
    if (workflow.name !== 'Copilot Setup Steps'
        || !Object.hasOwn(triggers, 'workflow_dispatch')
        || !isObject(setupJob)
        || workflow.permissions.contents !== 'read'
        || setupJob.permissions?.contents !== 'read') {
      fail(`${repositoryPath}: invalid Copilot setup workflow shape.`);
    }
  }
}

function validateDependabot(root) {
  const repositoryPath = '.github/dependabot.yml';
  const path = resolve(root, repositoryPath);
  if (!existsSync(path)) {
    fail(`${repositoryPath}: a Dependabot configuration is required.`);
  }
  const parsed = parseYaml(readFileSync(path, 'utf8'), repositoryPath);
  const configuration = parsed.value;
  assertObject(configuration, repositoryPath);
  if (configuration.version !== 2) {
    fail(`${repositoryPath}: version 2 is required.`);
  }
  if (!Array.isArray(configuration.updates) || configuration.updates.length === 0) {
    fail(`${repositoryPath}: at least one update entry is required.`);
  }

  const ecosystems = new Set();
  for (const [index, entry] of configuration.updates.entries()) {
    const label = `${repositoryPath}: update ${index + 1}`;
    assertObject(entry, label);
    const ecosystem = entry['package-ecosystem'];
    if (typeof ecosystem !== 'string' || ecosystem.trim().length === 0) {
      fail(`${label}: package-ecosystem is required.`);
    }
    ecosystems.add(ecosystem);
    const directory = entry.directory ?? entry.directories;
    if (typeof directory !== 'string' && !Array.isArray(directory)) {
      fail(`${label}: a directory is required.`);
    }
    assertObject(entry.schedule, `${label} schedule`);
    if (!DEPENDABOT_INTERVALS.has(entry.schedule.interval)) {
      fail(`${label}: schedule.interval must be daily, weekly, or monthly.`);
    }
    const limit = entry['open-pull-requests-limit'];
    if (!Number.isInteger(limit) || limit < 1 || limit > 10) {
      fail(`${label}: open-pull-requests-limit must be between 1 and 10.`);
    }
  }

  if (!ecosystems.has('github-actions')) {
    fail(`${repositoryPath}: github-actions updates are required to refresh pinned actions.`);
  }
}

function validateWorkflows(root) {
  const workflowsRoot = resolve(root, '.github/workflows');
  for (const path of listFiles(workflowsRoot)) {
    if (!/\.(?:yml|yaml)$/.test(path)) {
      fail(`${relative(root, path)}: workflow files must use .yml or .yaml.`);
    }
    validateWorkflow(root, path);
  }
}

export function validateAgentWorkflows(root = REPOSITORY_ROOT) {
  const instructionFiles = validateInstructions(root);
  validateSkills(root);
  validateDuplicateRules(root, instructionFiles);
  validateWorkflows(root);
  validateDependabot(root);
}

const isEntryPoint = typeof process.argv[1] === 'string'
  && pathToFileURL(resolve(process.argv[1])).href === import.meta.url;

if (isEntryPoint) {
  try {
    validateAgentWorkflows();
    console.log('Agent instructions, skills, and workflows are valid.');
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}
