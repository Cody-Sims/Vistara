/* global process, structuredClone */

import assert from "node:assert/strict";
import { mkdir, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  findGeneratedDrift,
  generateGalleryArtifacts,
  loadGalleryManifest,
} from "./gallery-api.mjs";

const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const webDirectory = path.dirname(scriptsDirectory);
const repositoryRoot = path.resolve(webDirectory, "../..");
const scratchRoot = path.join(scriptsDirectory, `.gallery-api-test-${process.pid}`);

test("gallery generation is deterministic and path-independent", async () => {
  const manifest = await loadGalleryManifest(repositoryRoot);
  const first = await generateGalleryArtifacts(manifest);
  const second = await generateGalleryArtifacts(manifest);

  assert.deepEqual(first, second);
  for (const content of first.values()) {
    assert.doesNotMatch(content, /\/Users\/|[A-Z]:\\/u);
    assert.doesNotMatch(content, /generated at|timestamp/iu);
  }
});

test("gallery drift detection reports stale and missing generated files", async () => {
  await rm(scratchRoot, { force: true, recursive: true });
  await mkdir(scratchRoot, { recursive: true });

  try {
    const manifest = await loadGalleryManifest(repositoryRoot);
    const artifacts = await generateGalleryArtifacts(manifest);
    for (const [relativePath, content] of artifacts) {
      const target = path.join(scratchRoot, relativePath);
      await mkdir(path.dirname(target), { recursive: true });
      await writeFile(target, content);
    }

    assert.deepEqual(await findGeneratedDrift(artifacts, scratchRoot), []);

    const [changedPath] = artifacts.keys();
    assert.ok(changedPath);
    await writeFile(
      path.join(scratchRoot, changedPath),
      "// manual stale change\n",
    );

    assert.deepEqual(await findGeneratedDrift(artifacts, scratchRoot), [
      changedPath,
    ]);
  } finally {
    await rm(scratchRoot, { force: true, recursive: true });
  }
});

test("gallery generation rejects an unresolved local schema reference", async () => {
  const manifest = structuredClone(await loadGalleryManifest(repositoryRoot));
  manifest.components.schemas.Uuid.$ref =
    "#/components/schemas/SchemaThatDoesNotExist";

  await assert.rejects(
    generateGalleryArtifacts(manifest),
    /SchemaThatDoesNotExist|reference|resolve/iu,
  );
});

test("gallery generation rejects external references without resolving them", async () => {
  const manifest = structuredClone(await loadGalleryManifest(repositoryRoot));
  manifest.components.schemas.Uuid.$ref =
    "https://example.invalid/external-schema.json";

  await assert.rejects(
    generateGalleryArtifacts(manifest),
    /local.*\$ref|external reference/iu,
  );
});

test("gallery generation rejects a malformed schema", async () => {
  const manifest = structuredClone(await loadGalleryManifest(repositoryRoot));
  manifest.paths["/api/v1/assets"].get.responses["200"].content[
    "application/json"
  ].schema = "not-a-schema-object";

  await assert.rejects(
    generateGalleryArtifacts(manifest),
    /schema|object|boolean|valid/iu,
  );
});

test("gallery generation rejects duplicate operation identifiers", async () => {
  const manifest = structuredClone(await loadGalleryManifest(repositoryRoot));
  manifest.paths["/api/v1/assets/{id}"].get.operationId = "listAssets";

  await assert.rejects(
    generateGalleryArtifacts(manifest),
    /duplicate|operationId|unique/iu,
  );
});
