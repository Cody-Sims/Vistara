import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { buildBrandAssets } from '../../scripts/brand-assets.mjs';

function readPublic(name: string) {
  return readFileSync(resolve(process.cwd(), 'public', name));
}

function pngSize(bytes: Buffer) {
  expect([...bytes.subarray(0, 8)]).toEqual([
    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
  ]);
  expect(bytes.subarray(12, 16).toString('ascii')).toBe('IHDR');
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

const html = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8');

describe('brand and installable assets', () => {
  it('ships icons at the declared sizes', () => {
    expect(pngSize(readPublic('icon-192.png'))).toEqual({
      width: 192,
      height: 192,
    });
    expect(pngSize(readPublic('icon-512.png'))).toEqual({
      width: 512,
      height: 512,
    });
    expect(pngSize(readPublic('icon-maskable-512.png'))).toEqual({
      width: 512,
      height: 512,
    });
    expect(pngSize(readPublic('apple-touch-icon.png'))).toEqual({
      width: 180,
      height: 180,
    });
    expect(pngSize(readPublic('social-preview.png'))).toEqual({
      width: 1200,
      height: 630,
    });
  });

  it('describes an installable application in the manifest', () => {
    const manifest = JSON.parse(
      readPublic('manifest.webmanifest').toString('utf8'),
    ) as {
      name: string;
      start_url: string;
      display: string;
      theme_color: string;
      background_color: string;
      icons: { src: string; sizes: string; purpose?: string }[];
    };

    expect(manifest.name).toBe('Vistara');
    expect(manifest.start_url).toBe('/');
    expect(manifest.display).toBe('standalone');
    expect(manifest.theme_color).toBe('#11110f');
    expect(manifest.background_color).toBe('#11110f');
    expect(manifest.icons.map((icon) => icon.sizes)).toContain('512x512');
    expect(manifest.icons.some((icon) => icon.purpose === 'maskable')).toBe(
      true,
    );
  });

  it('regenerates byte-identical assets from the drawing source', () => {
    for (const [relativePath, contents] of buildBrandAssets()) {
      const committed = readFileSync(resolve(process.cwd(), relativePath));
      expect(
        `${relativePath}:${committed.equals(contents)}`,
        `${relativePath} is stale; run node scripts/write-brand-assets.mjs`,
      ).toBe(`${relativePath}:true`);
    }
  }, 60_000);

  it('links the icons, manifest, and social preview from the document', () => {
    expect(html).toContain('rel="manifest" href="/manifest.webmanifest"');
    expect(html).toContain('href="/favicon.svg"');
    expect(html).toContain('rel="apple-touch-icon"');
    expect(html).toContain('property="og:image"');
    expect(html).toContain('/social-preview.png');
    expect(html).toContain('name="twitter:card"');
    expect(html).toContain('property="og:title"');
  });

  it('keeps the scalable mark free of raster payloads', () => {
    const svg = readPublic('favicon.svg').toString('utf8');

    expect(svg).toContain('<svg');
    expect(svg).not.toContain('base64');
    expect(svg).not.toContain('<image');
  });
});
