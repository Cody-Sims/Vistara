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

test("gallery manifest uses the production browser cookie security scheme", async () => {
  const manifest = await loadGalleryManifest(repositoryRoot);

  assert.deepEqual(manifest.components.securitySchemes.browserCookie, {
    type: "apiKey",
    in: "cookie",
    name: "__Host-vistara-session",
  });
});

test("gallery mutations publish required standard concurrency headers", async () => {
  const manifest = await loadGalleryManifest(repositoryRoot);

  for (const [route, method, operation] of operations(manifest)) {
    const expectations = [
      ["If-Match", operation["x-vistara-clr"].requiresIfMatch, "EntityTag"],
      [
        "Idempotency-Key",
        operation["x-vistara-clr"].requiresIdempotencyKey,
        "IdempotencyKey",
      ],
    ];

    for (const [name, required, schemaName] of expectations) {
      const parameter = (operation.parameters ?? [])
        .map((candidate) => resolveParameter(manifest, candidate))
        .find(
          (candidate) =>
            candidate.in === "header" &&
            candidate.name.toLowerCase() === name.toLowerCase(),
        );

      assert.equal(
        parameter !== undefined,
        required,
        `${method.toUpperCase()} ${route} ${name}`,
      );
      if (required) {
        assert.equal(parameter.required, true);
        assert.equal(
          parameter.schema.$ref,
          `#/components/schemas/${schemaName}`,
        );
      }
    }
  }
});

test("gallery nullable mutations accept JSON null values", async () => {
  const manifest = await loadGalleryManifest(repositoryRoot);
  const schemas = manifest.components.schemas;

  assert.deepEqual(schemas.UpdateAssetRequest.properties.description.type, [
    "string",
    "null",
  ]);
  assert.deepEqual(schemas.UpdateAssetRequest.properties.capturedAt.type, [
    "string",
    "null",
  ]);
  assert.deepEqual(schemas.UpdateAlbumRequest.properties.description.type, [
    "string",
    "null",
  ]);
  assert.deepEqual(schemas.UpdateAlbumRequest.properties.coverAssetId.type, [
    "string",
    "null",
  ]);
  assert.deepEqual(schemas.UpdateTagRequest.properties.color.type, [
    "string",
    "null",
  ]);
  assert.deepEqual(schemas.UpdateShareRequest.properties.expiresAt.type, [
    "string",
    "null",
  ]);
});

test("purge barriers are constrained by the OpenAPI schema", async () => {
  const manifest = await loadGalleryManifest(repositoryRoot);
  const barrierItems =
    manifest.components.schemas.PurgeCandidate.properties.barriers.items;

  assert.deepEqual(
    barrierItems.oneOf.map((candidate) => candidate.const),
    [
      "notTrashed",
      "retentionPeriod",
      "activeHold",
      "revisionChanged",
      "blockingReference",
    ],
  );
});

test("timeline query schema includes every asset list filter", async () => {
  const manifest = await loadGalleryManifest(repositoryRoot);
  const schemas = manifest.components.schemas;
  const assetFilters = collectProperties(manifest, schemas.AssetListQuery);
  const timelineFilters = collectProperties(manifest, schemas.TimelineQuery);

  assert.deepEqual(
    [...timelineFilters.keys()].sort(),
    [...assetFilters.keys(), "groupBy"].sort(),
  );
  for (const [name, schema] of assetFilters) {
    assert.deepEqual(timelineFilters.get(name), schema, name);
  }
});

test("generated models follow schema mutations instead of declarations", async () => {
  const manifest = structuredClone(await loadGalleryManifest(repositoryRoot));
  manifest.components.schemas.AssetSummary.properties.title.type = "boolean";

  const artifacts = await generateGalleryArtifacts(manifest);
  const models = artifacts.get(
    "src/Vistara.Web/src/api/generated/models.ts",
  );
  const assetSummary = models.match(
    /export interface AssetSummary \{(?<body>[\s\S]*?)\n\}/u,
  )?.groups?.body;

  assert.ok(assetSummary);
  assert.match(assetSummary, /readonly title: boolean;/u);
  assert.doesNotMatch(assetSummary, /readonly title: string;/u);
});

test("gallery generation rejects client metadata that disagrees with schemas", async () => {
  const manifest = structuredClone(await loadGalleryManifest(repositoryRoot));
  manifest.paths["/api/v1/assets/{id}"].get[
    "x-vistara-client"
  ].responseType = "Tag";

  await assert.rejects(
    generateGalleryArtifacts(manifest),
    /getAsset.*responseType.*AssetDetail.*Tag/iu,
  );
});

test("gallery generation rejects unresolved metadata schema references", async () => {
  const manifest = structuredClone(await loadGalleryManifest(repositoryRoot));
  manifest["x-vistara-catalog-schemas"][0].component =
    "SchemaThatDoesNotExist";

  await assert.rejects(
    generateGalleryArtifacts(manifest),
    /SchemaThatDoesNotExist.*component|component.*SchemaThatDoesNotExist/iu,
  );
});

function* operations(manifest) {
  for (const [route, pathItem] of Object.entries(manifest.paths)) {
    for (const method of ["get", "post", "put", "patch", "delete"]) {
      if (pathItem[method] !== undefined) {
        yield [route, method, pathItem[method]];
      }
    }
  }
}

function resolveParameter(manifest, parameter) {
  if (parameter.$ref === undefined) {
    return parameter;
  }

  const prefix = "#/components/parameters/";
  assert.ok(parameter.$ref.startsWith(prefix));
  return manifest.components.parameters[parameter.$ref.slice(prefix.length)];
}

function collectProperties(manifest, schema, seen = new Set()) {
  if (schema.$ref !== undefined) {
    assert.ok(!seen.has(schema.$ref), `Circular schema reference: ${schema.$ref}`);
    const prefix = "#/components/schemas/";
    assert.ok(schema.$ref.startsWith(prefix));
    const nextSeen = new Set(seen).add(schema.$ref);
    return collectProperties(
      manifest,
      manifest.components.schemas[schema.$ref.slice(prefix.length)],
      nextSeen,
    );
  }

  const properties = new Map(Object.entries(schema.properties ?? {}));
  for (const inherited of schema.allOf ?? []) {
    for (const [name, property] of collectProperties(
      manifest,
      inherited,
      seen,
    )) {
      properties.set(name, property);
    }
  }
  return properties;
}
