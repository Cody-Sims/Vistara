import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { inflateSync } from 'node:zlib';
import { describe, expect, it } from 'vitest';
import {
  buildBrandAssets,
  encodeIco,
  encodePng,
  palette,
  zlibCompress,
} from '../../scripts/brand-assets.mjs';

function readPublic(name: string) {
  return readFileSync(resolve(process.cwd(), 'public', name));
}

function digest(bytes: Buffer) {
  return createHash('sha256').update(bytes).digest('hex');
}

/** Deterministic stand-in artwork; no clock, randomness, or platform input. */
function syntheticPixels(width: number, height: number) {
  const rgba = new Uint8Array(width * height * 4);
  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      const offset = (y * width + x) * 4;
      rgba[offset] = (x * 7 + y * 13) % 256;
      rgba[offset + 1] = (x * x + y * 3) % 256;
      rgba[offset + 2] = (x * 29 + y * y) % 256;
      rgba[offset + 3] = x < 3 || y < 3 ? 0 : 255;
    }
  }
  return rgba;
}

function imageData(png: Buffer) {
  const parts: Buffer[] = [];
  let offset = 8;
  while (offset < png.length) {
    const length = png.readUInt32BE(offset);
    if (png.subarray(offset + 4, offset + 8).toString('ascii') === 'IDAT') {
      parts.push(png.subarray(offset + 8, offset + 8 + length));
    }
    offset += 12 + length;
  }
  return Buffer.concat(parts);
}

const RASTER_ASSETS = [
  'icon-192.png',
  'icon-512.png',
  'icon-maskable-512.png',
  'apple-touch-icon.png',
  'social-preview.png',
];

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
    expect(manifest.start_url).toBe('./');
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
  }, 180_000);

  it('resolves every manifest reference relative to the manifest itself', () => {
    const manifest = JSON.parse(
      readPublic('manifest.webmanifest').toString('utf8'),
    ) as {
      start_url: string;
      scope: string;
      id?: string;
      icons: { src: string }[];
    };

    expect(manifest.scope).toBe('./');
    expect(manifest.id).toBeUndefined();
    for (const reference of [
      manifest.start_url,
      manifest.scope,
      ...manifest.icons.map((icon) => icon.src),
    ]) {
      expect(reference.startsWith('./')).toBe(true);
    }
  });

  it('links the icons and manifest through the configured base', () => {
    expect(html).toContain('rel="manifest" href="%BASE_URL%manifest.webmanifest"');
    expect(html).toContain('href="%BASE_URL%favicon.svg"');
    expect(html).toContain('href="%BASE_URL%apple-touch-icon.png"');
    expect(html).toContain('property="og:title"');
  });

  it('never ships an unresolvable preview image URL in the source document', () => {
    expect(html).not.toContain('og:image');
    expect(html).not.toContain('twitter:image');
  });

  it('keeps the scalable mark free of raster payloads', () => {
    const svg = readPublic('favicon.svg').toString('utf8');

    expect(svg).toContain('<svg');
    expect(svg).not.toContain('base64');
    expect(svg).not.toContain('<image');
  });
});

describe('platform-independent asset encoding', () => {
  // These digests pin the encoder itself. The generator deliberately does not
  // use `node:zlib`, whose DEFLATE output differs between the compressor a
  // macOS build links against and the one bundled with the Linux build, which
  // previously made every committed raster look stale on CI only.
  it('pins the encoded bytes for fixed input on every platform', () => {
    const pixels = syntheticPixels(37, 23);

    expect(digest(encodePng(37, 23, pixels))).toBe(
      '1ad957a4860fc84c28b2a07f2303c13c1f71a2567d88da598cd4ed9c88debf1c',
    );
    expect(
      digest(encodeIco([{ size: 16, rgba: pixels.subarray(0, 16 * 16 * 4) }])),
    ).toBe('e785e070ccf2487202a3099994cb560044c07447718ed5954b5ca2dcc00d9b5a');
  });

  it('rebuilds identical bytes when the encoders run twice', () => {
    const pixels = syntheticPixels(64, 48);

    expect(
      encodePng(64, 48, pixels).equals(encodePng(64, 48, pixels)),
    ).toBe(true);
    expect(
      encodeIco([{ size: 32, rgba: pixels.subarray(0, 32 * 32 * 4) }]).equals(
        encodeIco([{ size: 32, rgba: pixels.subarray(0, 32 * 32 * 4) }]),
      ),
    ).toBe(true);
  });

  it('emits streams an independent inflater can read back exactly', () => {
    const cases: Record<string, Buffer> = {
      empty: Buffer.alloc(0),
      single: Buffer.from([7]),
      uniform: Buffer.alloc(70_000, 0xab),
      alternating: Buffer.from('ab'.repeat(4000)),
      noisy: Buffer.from(
        Array.from({ length: 70_000 }, (_, index) => (index * index * 31 + index * 7) % 251),
      ),
    };

    for (const [name, data] of Object.entries(cases)) {
      const restored = inflateSync(zlibCompress(data));
      expect(`${name}:${restored.equals(data)}`).toBe(`${name}:true`);
    }
  });

  it('stores every committed raster as inflatable 8-bit RGBA scanlines', () => {
    for (const name of RASTER_ASSETS) {
      const png = readPublic(name);
      const width = png.readUInt32BE(16);
      const height = png.readUInt32BE(20);

      expect(`${name}:depth:${png[24]}`).toBe(`${name}:depth:8`);
      expect(`${name}:colour:${png[25]}`).toBe(`${name}:colour:6`);
      expect(`${name}:interlace:${png[28]}`).toBe(`${name}:interlace:0`);

      const raw = inflateSync(imageData(png));
      const stride = width * 4 + 1;
      expect(`${name}:${raw.length}`).toBe(`${name}:${stride * height}`);

      let worstFilter = 0;
      for (let y = 0; y < height; y += 1) {
        worstFilter = Math.max(worstFilter, raw.readUInt8(y * stride));
      }
      expect(`${name}:filter:${worstFilter <= 4}`).toBe(`${name}:filter:true`);
    }
  });
});

describe('favicon.ico icon directory', () => {
  const ico = readPublic('favicon.ico');
  const entryOffset = 6;
  const dibOffset = 22;
  const colourBytes = 32 * 32 * 4;
  const maskBytes = 32 * 4;

  it('declares a single 32x32 true-colour image', () => {
    expect(ico.readUInt16LE(0)).toBe(0);
    expect(ico.readUInt16LE(2)).toBe(1);
    expect(ico.readUInt16LE(4)).toBe(1);
    expect(ico[entryOffset]).toBe(32);
    expect(ico[entryOffset + 1]).toBe(32);
    expect(ico[entryOffset + 2]).toBe(0);
    expect(ico[entryOffset + 3]).toBe(0);
    expect(ico.readUInt16LE(entryOffset + 4)).toBe(1);
    expect(ico.readUInt16LE(entryOffset + 6)).toBe(32);
    expect(ico.readUInt32LE(entryOffset + 12)).toBe(dibOffset);
    expect(ico.readUInt32LE(entryOffset + 8)).toBe(ico.length - dibOffset);
  });

  it('carries an uncompressed bitmap that cannot drift between platforms', () => {
    const dib = ico.subarray(dibOffset);

    expect(dib.readUInt32LE(0)).toBe(40);
    expect(dib.readInt32LE(4)).toBe(32);
    expect(dib.readInt32LE(8)).toBe(64);
    expect(dib.readUInt16LE(12)).toBe(1);
    expect(dib.readUInt16LE(14)).toBe(32);
    expect(dib.readUInt32LE(16)).toBe(0);
    expect(dib.readUInt32LE(20)).toBe(colourBytes + maskBytes);
    expect(dib.length).toBe(40 + colourBytes + maskBytes);
  });

  it('draws the brand mark bottom-up with a matching transparency mask', () => {
    const colour = ico.subarray(dibOffset + 40, dibOffset + 40 + colourBytes);
    const mask = ico.subarray(dibOffset + 40 + colourBytes);
    const pixelAt = (x: number, y: number) => {
      const offset = ((31 - y) * 32 + x) * 4;
      return [
        colour.readUInt8(offset + 2),
        colour.readUInt8(offset + 1),
        colour.readUInt8(offset),
        colour.readUInt8(offset + 3),
      ];
    };
    const maskedAt = (x: number, y: number) =>
      (mask.readUInt8((31 - y) * 4 + (x >> 3)) >> (7 - (x % 8))) & 1;

    expect(pixelAt(22, 9)).toEqual([...palette.sun, 255]);
    expect(pixelAt(16, 16)).toEqual([...palette.accentDeep, 255]);
    expect(pixelAt(0, 0)[3]).toBe(0);
    expect(maskedAt(0, 0)).toBe(1);
    expect(maskedAt(31, 0)).toBe(1);
    expect(maskedAt(16, 16)).toBe(0);
  });
});
