import assert from 'node:assert/strict';
import {
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

import { validateShadow } from '../validate-shadow.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const FIXTURE = resolve(HERE, 'fixtures/shadow-valid');

function withFixture(run) {
  const root = resolve(HERE, `.scratch-shadow-${process.pid}-${randomUUID()}`);
  cpSync(FIXTURE, root, { recursive: true });
  try {
    run(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function writeJson(path, value) {
  writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`);
}

test('accepts a complete evidence-linked shadow graph', () => {
  assert.doesNotThrow(() => validateShadow(FIXTURE));
});

test('rejects an anchor locator that is not present exactly', () => {
  withFixture((root) => {
    const path = resolve(root, '.shadow/decisions/DEC-0001.json');
    const decision = JSON.parse(readFileSync(path, 'utf8'));
    decision.anchors[0].locator = 'missing locator';
    writeJson(path, decision);

    assert.throws(() => validateShadow(root), /locator was not found/);
  });
});

test('requires accepted decisions to cite the authoritative specification', () => {
  withFixture((root) => {
    const path = resolve(root, '.shadow/decisions/DEC-0001.json');
    const decision = JSON.parse(readFileSync(path, 'utf8'));
    decision.evidence = [{
      path: 'src/Core/Marker.cs',
      locator: 'public sealed class Marker',
    }];
    writeJson(path, decision);

    assert.throws(() => validateShadow(root), /accepted decisions must cite docs\/specification\.md/);
  });
});

test('requires index entries to stay sorted', () => {
  withFixture((root) => {
    const decision = {
      $schema: '../schema.json',
      id: 'DEC-0002',
      title: 'Second decision',
      status: 'observed',
      date: '2026-08-28',
      statement: 'A second observed decision exists.',
      rationale: 'The marker source records it.',
      owners: ['maintainers'],
      anchors: [{
        path: 'src/Core/Marker.cs',
        locator: 'public sealed class Marker',
      }],
      evidence: [{
        path: 'src/Core/Marker.cs',
        locator: 'public sealed class Marker',
      }],
      relations: [],
    };
    writeJson(resolve(root, '.shadow/decisions/DEC-0002.json'), decision);
    const indexPath = resolve(root, '.shadow/index.json');
    const index = JSON.parse(readFileSync(indexPath, 'utf8'));
    index.entries.unshift({
      id: decision.id,
      title: decision.title,
      status: decision.status,
      file: '.shadow/decisions/DEC-0002.json',
    });
    writeJson(indexPath, index);
    const featuresPath = resolve(root, '.shadow/features.json');
    const features = JSON.parse(readFileSync(featuresPath, 'utf8'));
    features.features[0].decisions.push('DEC-0002');
    writeJson(featuresPath, features);

    assert.throws(() => validateShadow(root), /index entries must be sorted/);
  });
});

test('rejects relation cycles', () => {
  withFixture((root) => {
    const firstPath = resolve(root, '.shadow/decisions/DEC-0001.json');
    const first = JSON.parse(readFileSync(firstPath, 'utf8'));
    first.relations = [{ type: 'depends_on', target: 'DEC-0002' }];
    writeJson(firstPath, first);

    const second = {
      ...first,
      id: 'DEC-0002',
      title: 'Second decision',
      status: 'observed',
      evidence: [{
        path: 'src/Core/Marker.cs',
        locator: 'public sealed class Marker',
      }],
      relations: [{ type: 'depends_on', target: 'DEC-0001' }],
    };
    writeJson(resolve(root, '.shadow/decisions/DEC-0002.json'), second);

    const indexPath = resolve(root, '.shadow/index.json');
    const index = JSON.parse(readFileSync(indexPath, 'utf8'));
    index.entries.push({
      id: second.id,
      title: second.title,
      status: second.status,
      file: '.shadow/decisions/DEC-0002.json',
    });
    writeJson(indexPath, index);

    const featuresPath = resolve(root, '.shadow/features.json');
    const features = JSON.parse(readFileSync(featuresPath, 'utf8'));
    features.features[0].decisions.push('DEC-0002');
    writeJson(featuresPath, features);

    assert.throws(() => validateShadow(root), /relation cycle/);
  });
});

test('rejects self-relations', () => {
  withFixture((root) => {
    const path = resolve(root, '.shadow/decisions/DEC-0001.json');
    const decision = JSON.parse(readFileSync(path, 'utf8'));
    decision.relations = [{ type: 'supports', target: 'DEC-0001' }];
    writeJson(path, decision);

    assert.throws(() => validateShadow(root), /self-relations are forbidden/);
  });
});

test('requires reciprocal supersession links', () => {
  withFixture((root) => {
    const firstPath = resolve(root, '.shadow/decisions/DEC-0001.json');
    const first = JSON.parse(readFileSync(firstPath, 'utf8'));
    first.relations = [{ type: 'supersedes', target: 'DEC-0002' }];
    writeJson(firstPath, first);

    const second = {
      ...first,
      id: 'DEC-0002',
      title: 'Superseded decision',
      status: 'superseded',
      evidence: [{
        path: 'src/Core/Marker.cs',
        locator: 'public sealed class Marker',
      }],
      relations: [],
    };
    writeJson(resolve(root, '.shadow/decisions/DEC-0002.json'), second);

    const indexPath = resolve(root, '.shadow/index.json');
    const index = JSON.parse(readFileSync(indexPath, 'utf8'));
    index.entries.push({
      id: second.id,
      title: second.title,
      status: second.status,
      file: '.shadow/decisions/DEC-0002.json',
    });
    writeJson(indexPath, index);

    const featuresPath = resolve(root, '.shadow/features.json');
    const features = JSON.parse(readFileSync(featuresPath, 'utf8'));
    features.features[0].decisions.push('DEC-0002');
    writeJson(featuresPath, features);

    assert.throws(() => validateShadow(root), /supersession must be reciprocal/);
  });
});

test('rejects decisions orphaned from the feature map', () => {
  withFixture((root) => {
    const featuresPath = resolve(root, '.shadow/features.json');
    const features = JSON.parse(readFileSync(featuresPath, 'utf8'));
    features.features[0].decisions = [];
    writeJson(featuresPath, features);

    assert.throws(() => validateShadow(root), /not linked from any feature/);
  });
});

test('requires feature owners to exist', () => {
  withFixture((root) => {
    const featuresPath = resolve(root, '.shadow/features.json');
    const features = JSON.parse(readFileSync(featuresPath, 'utf8'));
    features.features[0].owner = '.github/instructions/missing.instructions.md';
    writeJson(featuresPath, features);

    assert.throws(() => validateShadow(root), /owner does not exist/);
  });
});
