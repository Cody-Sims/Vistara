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

    for (const target of [production, pages]) {
      expectInstallableAssets(target.root, target.build.outDir, target.base);
      expectShellOnlyServiceWorker(
        target.root,
        target.build.outDir,
        target.base,
      );
    }
  }, 120_000);
});

function expectShellOnlyServiceWorker(
  root: string,
  outDir: string,
  base: string,
) {
  const artifactRoot = resolve(root, outDir);
  const worker = readFileSync(resolve(artifactRoot, 'sw.js'), 'utf8');
  const precache = JSON.parse(
    worker.slice(worker.indexOf('['), worker.indexOf(']') + 1),
  ) as string[];

  expect(worker).toMatch(/const CACHE = "vistara-shell-[0-9a-f]{12}"/);
  expect(precache).toContain(`${base}index.html`);
  expect(precache.length).toBeGreaterThan(3);

  for (const entry of precache) {
    expect(entry.startsWith(base)).toBe(true);
    expect(entry).not.toContain('/api/');
    expect(entry).not.toContain('/media/');
    expect(entry).not.toContain('/delivery/');
    expect(entry).not.toMatch(/\.map$/);
    expect(existsSync(resolve(artifactRoot, entry.slice(base.length)))).toBe(
      true,
    );
  }
}

function expectInstallableAssets(root: string, outDir: string, base: string) {
  const artifactRoot = resolve(root, outDir);
  const html = readFileSync(resolve(artifactRoot, 'index.html'), 'utf8');

  for (const asset of [
    'manifest.webmanifest',
    'favicon.svg',
    'favicon.ico',
    'apple-touch-icon.png',
    'icon-192.png',
    'icon-512.png',
    'icon-maskable-512.png',
  ]) {
    expect(existsSync(resolve(artifactRoot, asset))).toBe(true);
  }

  expect(html).toContain(`href="${base}manifest.webmanifest"`);
  expect(html).toContain(`href="${base}favicon.svg"`);
  expect(html).toContain(`href="${base}apple-touch-icon.png"`);
  expect(html).not.toContain('%BASE_URL%');

  // No deployment origin is configured for these builds, so no preview image
  // URL may be advertised at all.
  expect(html).not.toContain('og:image');

  const manifest = JSON.parse(
    readFileSync(resolve(artifactRoot, 'manifest.webmanifest'), 'utf8'),
  ) as { start_url: string; scope: string; icons: { src: string }[] };
  for (const reference of [
    manifest.start_url,
    manifest.scope,
    ...manifest.icons.map((icon) => icon.src),
  ]) {
    expect(new URL(reference, `https://vistara.invalid${base}manifest.webmanifest`).pathname)
      .toContain(base);
  }
}

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
