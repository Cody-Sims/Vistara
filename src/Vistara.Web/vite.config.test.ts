import { execFile } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { promisify } from 'node:util';
import { resolveConfig } from 'vite';
import { describe, expect, it } from 'vitest';

const assetReference = /(?:src|href)=["'](?<path>[^"'?#]+\.(?:js|css))["']/gi;
const execFileAsync = promisify(execFile);
const viteCli = resolve('node_modules/vite/bin/vite.js');

describe('Vite build artifacts', () => {
  it('preserves production assets when the Pages artifact is built', async () => {
    await buildWithMode('production');
    const production = await resolveConfig({ mode: 'production' }, 'build');
    const productionIndex = readFileSync(
      resolve(production.root, production.build.outDir, 'index.html'),
      'utf8',
    );

    await buildWithMode('pages');
    const pages = await resolveConfig({ mode: 'pages' }, 'build');

    expect(pages.build.outDir).not.toBe(production.build.outDir);
    expect(
      readFileSync(
        resolve(production.root, production.build.outDir, 'index.html'),
        'utf8',
      ),
    ).toBe(productionIndex);
    expectReferencedAssetsToExist(production.root, production.build.outDir, production.base);
    expectReferencedAssetsToExist(pages.root, pages.build.outDir, pages.base);
  }, 120_000);
});

async function buildWithMode(mode: string) {
  await execFileAsync(process.execPath, [viteCli, 'build', '--mode', mode], {
    env: { ...process.env, NODE_ENV: 'production' },
  });
}

function expectReferencedAssetsToExist(root: string, outDir: string, base: string) {
  const artifactRoot = resolve(root, outDir);
  const html = readFileSync(resolve(artifactRoot, 'index.html'), 'utf8');
  const references = [...html.matchAll(assetReference)].map(
    (match) => match.groups?.path,
  );

  expect(references.length).toBeGreaterThan(0);
  for (const reference of references) {
    expect(reference).toBeDefined();
    expect(reference?.startsWith(base)).toBe(true);
    expect(existsSync(resolve(artifactRoot, reference!.slice(base.length)))).toBe(true);
  }
}
