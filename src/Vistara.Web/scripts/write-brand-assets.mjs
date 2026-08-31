/* global console, process, URL */

import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildBrandAssets } from './brand-assets.mjs';

const projectRoot = path.dirname(fileURLToPath(new URL('.', import.meta.url)));

async function main() {
  const assets = buildBrandAssets();

  for (const [relativePath, contents] of assets) {
    const target = path.join(projectRoot, relativePath);
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, contents);
    console.log(`wrote ${relativePath} (${contents.length} bytes)`);
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
