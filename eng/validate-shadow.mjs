#!/usr/bin/env node

import {
  existsSync,
  lstatSync,
  readdirSync,
  readFileSync,
} from 'node:fs';
import { dirname, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const REPOSITORY_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ALLOWED_STATUSES = ['accepted', 'observed', 'proposed', 'superseded'];
const ALLOWED_RELATIONS = [
  'constrains',
  'depends_on',
  'superseded_by',
  'supersedes',
  'supports',
];
const DECISION_KEYS = [
  '$schema',
  'id',
  'title',
  'status',
  'date',
  'statement',
  'rationale',
  'owners',
  'anchors',
  'evidence',
  'relations',
];

function fail(message) {
  throw new Error(message);
}

function readJson(path, label) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (error) {
    fail(`${label} is not valid JSON: ${error instanceof Error ? error.message : error}`);
  }
}

function assertObject(value, label) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    fail(`${label} must be an object.`);
  }
}

function assertExactKeys(value, keys, label) {
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    fail(`${label} has an invalid schema shape.`);
  }
}

function assertSortedUnique(values, label) {
  if (!Array.isArray(values) || values.some((value) => typeof value !== 'string')) {
    fail(`${label} must be an array of strings.`);
  }
  const sorted = [...values].sort((left, right) => left.localeCompare(right));
  if (new Set(values).size !== values.length || JSON.stringify(values) !== JSON.stringify(sorted)) {
    fail(`${label} must be sorted and unique.`);
  }
}

function resolveRepositoryPath(root, repositoryPath, label) {
  if (typeof repositoryPath !== 'string'
      || repositoryPath.length === 0
      || repositoryPath.startsWith('/')
      || repositoryPath.includes('\\')
      || repositoryPath.split('/').includes('..')
      || /[*?[\]{}]/.test(repositoryPath)) {
    fail(`${label} must be an exact repository-relative path.`);
  }
  const resolved = resolve(root, repositoryPath);
  const relative = resolved.slice(root.length + 1);
  if (resolved === root || relative.startsWith(`..${sep}`) || !existsSync(resolved)) {
    fail(`${label} does not exist: ${repositoryPath}`);
  }
  return resolved;
}

function validateReference(root, reference, label) {
  assertObject(reference, label);
  assertExactKeys(reference, ['path', 'locator'], label);
  const resolved = resolveRepositoryPath(root, reference.path, `${label} path`);
  if (!lstatSync(resolved).isFile()) {
    fail(`${label} path must identify a file: ${reference.path}`);
  }
  if (typeof reference.locator !== 'string' || reference.locator.trim() !== reference.locator
      || reference.locator.length === 0 || reference.locator.includes('\n')) {
    fail(`${label} locator must be one exact non-empty line fragment.`);
  }
  const content = readFileSync(resolved, 'utf8');
  const occurrences = content.split(reference.locator).length - 1;
  if (occurrences === 0) {
    fail(`${label} locator was not found in ${reference.path}: ${reference.locator}`);
  }
  if (occurrences > 1) {
    fail(`${label} locator is ambiguous in ${reference.path}: ${reference.locator}`);
  }
}

function validateDecision(root, decision, file) {
  const label = `${file}`;
  assertObject(decision, label);
  assertExactKeys(decision, DECISION_KEYS, label);
  if (decision.$schema !== '../schema.json') {
    fail(`${label} must reference ../schema.json.`);
  }
  if (!/^DEC-\d{4}$/.test(decision.id ?? '')) {
    fail(`${label} has an invalid stable decision ID.`);
  }
  if (file !== `.shadow/decisions/${decision.id}.json`) {
    fail(`${label} must match decision ID ${decision.id}.`);
  }
  for (const field of ['title', 'statement', 'rationale']) {
    if (typeof decision[field] !== 'string' || decision[field].trim().length === 0) {
      fail(`${decision.id}: ${field} is required.`);
    }
  }
  if (!ALLOWED_STATUSES.includes(decision.status)) {
    fail(`${decision.id}: status must be one of ${ALLOWED_STATUSES.join(', ')}.`);
  }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(decision.date ?? '')) {
    fail(`${decision.id}: date must use YYYY-MM-DD.`);
  }
  assertSortedUnique(decision.owners, `${decision.id} owners`);
  if (decision.owners.length === 0) {
    fail(`${decision.id}: at least one owner is required.`);
  }
  for (const field of ['anchors', 'evidence']) {
    if (!Array.isArray(decision[field]) || decision[field].length === 0) {
      fail(`${decision.id}: ${field} must be non-empty.`);
    }
    for (const [index, reference] of decision[field].entries()) {
      validateReference(root, reference, `${decision.id} ${field}[${index}]`);
    }
  }
  if (decision.status === 'accepted'
      && !decision.evidence.some((reference) => reference.path === 'docs/specification.md')) {
    fail(`${decision.id}: accepted decisions must cite docs/specification.md.`);
  }
  if (!Array.isArray(decision.relations)) {
    fail(`${decision.id}: relations must be an array.`);
  }
  const relationKeys = new Set();
  for (const relation of decision.relations) {
    assertObject(relation, `${decision.id} relation`);
    assertExactKeys(relation, ['type', 'target'], `${decision.id} relation`);
    if (!ALLOWED_RELATIONS.includes(relation.type)) {
      fail(`${decision.id}: unsupported relation type ${relation.type}.`);
    }
    if (!/^DEC-\d{4}$/.test(relation.target ?? '')) {
      fail(`${decision.id}: invalid relation target ${relation.target}.`);
    }
    if (relation.target === decision.id) {
      fail(`${decision.id}: self-relations are forbidden.`);
    }
    const key = `${relation.type}:${relation.target}`;
    if (relationKeys.has(key)) {
      fail(`${decision.id}: duplicate relation ${key}.`);
    }
    relationKeys.add(key);
  }
}

function validateIndex(index) {
  assertObject(index, '.shadow/index.json');
  assertExactKeys(
    index,
    ['schemaVersion', 'allowedStatuses', 'allowedRelations', 'entries'],
    '.shadow/index.json',
  );
  if (index.schemaVersion !== 1) {
    fail('.shadow/index.json: unsupported schemaVersion.');
  }
  if (JSON.stringify(index.allowedStatuses) !== JSON.stringify(ALLOWED_STATUSES)
      || JSON.stringify(index.allowedRelations) !== JSON.stringify(ALLOWED_RELATIONS)) {
    fail('.shadow/index.json: allowed vocabulary does not match the validator.');
  }
  if (!Array.isArray(index.entries) || index.entries.length === 0) {
    fail('.shadow/index.json: entries must be non-empty.');
  }
  const ids = index.entries.map((entry) => entry.id);
  if (new Set(ids).size !== ids.length) {
    fail('.shadow/index.json: decision IDs must be unique.');
  }
  if (JSON.stringify(ids) !== JSON.stringify([...ids].sort())) {
    fail('.shadow/index.json: index entries must be sorted by ID.');
  }
}

function validateRelations(decisions) {
  for (const decision of decisions.values()) {
    for (const relation of decision.relations) {
      const target = decisions.get(relation.target);
      if (!target) {
        fail(`${decision.id}: relation target does not exist: ${relation.target}.`);
      }
      if (relation.type === 'supersedes') {
        if (decision.status !== 'accepted' || target.status !== 'superseded') {
          fail(`${decision.id}: supersedes requires accepted -> superseded statuses.`);
        }
        if (!target.relations.some((candidate) =>
          candidate.type === 'superseded_by' && candidate.target === decision.id)) {
          fail(`${decision.id}: supersession must be reciprocal.`);
        }
      }
      if (relation.type === 'superseded_by') {
        if (decision.status !== 'superseded' || target.status !== 'accepted') {
          fail(`${decision.id}: superseded_by requires superseded -> accepted statuses.`);
        }
        if (!target.relations.some((candidate) =>
          candidate.type === 'supersedes' && candidate.target === decision.id)) {
          fail(`${decision.id}: supersession must be reciprocal.`);
        }
      }
    }
  }

  const visiting = new Set();
  const visited = new Set();
  const visit = (id, path) => {
    if (visiting.has(id)) {
      fail(`Shadow relation cycle detected: ${[...path, id].join(' -> ')}.`);
    }
    if (visited.has(id)) return;
    visiting.add(id);
    const decision = decisions.get(id);
    for (const relation of decision.relations.filter((item) => item.type !== 'superseded_by')) {
      visit(relation.target, [...path, id]);
    }
    visiting.delete(id);
    visited.add(id);
  };
  for (const id of decisions.keys()) visit(id, []);
}

function validateFeatures(root, features, decisions) {
  assertObject(features, '.shadow/features.json');
  assertExactKeys(features, ['schemaVersion', 'features'], '.shadow/features.json');
  if (features.schemaVersion !== 1 || !Array.isArray(features.features)
      || features.features.length === 0) {
    fail('.shadow/features.json has an invalid schema shape.');
  }
  const ids = features.features.map((feature) => feature.id);
  if (new Set(ids).size !== ids.length
      || JSON.stringify(ids) !== JSON.stringify([...ids].sort())) {
    fail('.shadow/features.json: feature entries must have unique sorted IDs.');
  }
  const mapped = new Set();
  for (const feature of features.features) {
    assertObject(feature, `feature ${feature.id ?? '<unknown>'}`);
    assertExactKeys(feature, ['id', 'name', 'owner', 'paths', 'decisions'], `feature ${feature.id}`);
    if (!/^F-[A-Z0-9]+(?:-[A-Z0-9]+)*$/.test(feature.id ?? '')
        || typeof feature.name !== 'string' || feature.name.trim().length === 0) {
      fail(`feature ${feature.id ?? '<unknown>'}: invalid ID or name.`);
    }
    resolveRepositoryPath(root, feature.owner, `${feature.id} owner`);
    assertSortedUnique(feature.paths, `${feature.id} paths`);
    if (feature.paths.length === 0) fail(`${feature.id}: paths must be non-empty.`);
    for (const path of feature.paths) resolveRepositoryPath(root, path, `${feature.id} path`);
    assertSortedUnique(feature.decisions, `${feature.id} decisions`);
    for (const id of feature.decisions) {
      if (!decisions.has(id)) fail(`${feature.id}: unknown decision ${id}.`);
      mapped.add(id);
    }
  }
  for (const id of decisions.keys()) {
    if (!mapped.has(id)) fail(`${id}: decision is not linked from any feature.`);
  }
}

export function validateShadow(root = REPOSITORY_ROOT) {
  const shadowRoot = resolve(root, '.shadow');
  for (const file of ['README.md', 'schema.json', 'index.json', 'features.json']) {
    if (!existsSync(resolve(shadowRoot, file))) fail(`.shadow/${file} is required.`);
  }
  const schema = readJson(resolve(shadowRoot, 'schema.json'), '.shadow/schema.json');
  if (schema.$schema !== 'https://json-schema.org/draft/2020-12/schema'
      || schema.type !== 'object') {
    fail('.shadow/schema.json must declare a draft 2020-12 object schema.');
  }

  const index = readJson(resolve(shadowRoot, 'index.json'), '.shadow/index.json');
  validateIndex(index);
  const decisionsRoot = resolve(shadowRoot, 'decisions');
  const files = readdirSync(decisionsRoot, { withFileTypes: true });
  if (files.some((entry) => !entry.isFile() || !/^DEC-\d{4}\.json$/.test(entry.name))) {
    fail('.shadow/decisions must contain only canonical DEC-0000.json files.');
  }
  const decisionFiles = files.map((entry) => entry.name).sort();
  const indexedFiles = index.entries.map((entry) => entry.file.replace('.shadow/decisions/', ''));
  if (JSON.stringify(decisionFiles) !== JSON.stringify(indexedFiles)) {
    fail('.shadow index/file agreement failed.');
  }

  const decisions = new Map();
  for (const entry of index.entries) {
    assertObject(entry, `index entry ${entry.id ?? '<unknown>'}`);
    assertExactKeys(entry, ['id', 'title', 'status', 'file'], `index entry ${entry.id}`);
    if (entry.file !== `.shadow/decisions/${entry.id}.json`) {
      fail(`${entry.id}: index path is not canonical.`);
    }
    const decision = readJson(resolve(root, entry.file), entry.file);
    validateDecision(root, decision, entry.file);
    if (entry.id !== decision.id || entry.title !== decision.title || entry.status !== decision.status) {
      fail(`${entry.id}: index metadata does not agree with its decision file.`);
    }
    decisions.set(decision.id, decision);
  }
  validateRelations(decisions);

  const features = readJson(resolve(shadowRoot, 'features.json'), '.shadow/features.json');
  validateFeatures(root, features, decisions);
}

const isEntryPoint = typeof process.argv[1] === 'string'
  && pathToFileURL(resolve(process.argv[1])).href === import.meta.url;

if (isEntryPoint) {
  try {
    validateShadow();
    console.log('Shadow architecture is valid.');
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}
