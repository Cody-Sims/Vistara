/* global Buffer */

import { deflateSync } from 'node:zlib';

/**
 * Original Vistara artwork rendered without any drawing dependency. Every mark
 * is described as geometry here and rasterised into PNG bytes, so the
 * committed assets can always be regenerated and verified from source.
 */

export const palette = {
  ink: [17, 17, 15],
  inkSoft: [30, 32, 27],
  parchment: [242, 240, 233],
  accent: [121, 201, 169],
  accentDeep: [47, 128, 103],
  accentDark: [25, 56, 46],
  sun: [239, 189, 103],
};

const crcTable = (() => {
  const table = new Uint32Array(256);
  for (let index = 0; index < 256; index += 1) {
    let value = index;
    for (let bit = 0; bit < 8; bit += 1) {
      value = value & 1 ? 0xed_b8_83_20 ^ (value >>> 1) : value >>> 1;
    }
    table[index] = value >>> 0;
  }
  return table;
})();

function crc32(bytes) {
  let crc = 0xff_ff_ff_ff;
  for (const byte of bytes) {
    crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  }
  return (crc ^ 0xff_ff_ff_ff) >>> 0;
}

function chunk(type, data) {
  const typeBytes = Buffer.from(type, 'ascii');
  const body = Buffer.concat([typeBytes, Buffer.from(data)]);
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([length, body, crc]);
}

export function encodePng(width, height, rgba) {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8;
  header[9] = 6;

  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y += 1) {
    raw[y * (stride + 1)] = 0;
    Buffer.from(rgba.buffer, y * stride, stride).copy(
      raw,
      y * (stride + 1) + 1,
    );
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** Wraps a PNG in the legacy icon container that `/favicon.ico` still fetches. */
export function encodeIco(png, size) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(1, 4);

  const entry = Buffer.alloc(16);
  entry[0] = size >= 256 ? 0 : size;
  entry[1] = size >= 256 ? 0 : size;
  entry.writeUInt16LE(1, 4);
  entry.writeUInt16LE(32, 6);
  entry.writeUInt32LE(png.length, 8);
  entry.writeUInt32LE(header.length + entry.length, 12);

  return Buffer.concat([header, entry, png]);
}

function clamp01(value) {
  return value < 0 ? 0 : value > 1 ? 1 : value;
}

function mix(from, to, amount) {
  const t = clamp01(amount);
  return [
    from[0] + (to[0] - from[0]) * t,
    from[1] + (to[1] - from[1]) * t,
    from[2] + (to[2] - from[2]) * t,
  ];
}

function over(base, color, alpha) {
  const a = clamp01(alpha);
  return [
    base[0] + (color[0] - base[0]) * a,
    base[1] + (color[1] - base[1]) * a,
    base[2] + (color[2] - base[2]) * a,
    Math.max(base[3], a),
  ];
}

function insideRoundedRect(x, y, rect) {
  const { left, top, width, height, radius } = rect;
  const right = left + width;
  const bottom = top + height;
  if (x < left || x > right || y < top || y > bottom) {
    return false;
  }

  const cx = Math.min(Math.max(x, left + radius), right - radius);
  const cy = Math.min(Math.max(y, top + radius), bottom - radius);
  return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2;
}

function insideCircle(x, y, cx, cy, radius) {
  return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2;
}

function insideTriangle(x, y, [ax, ay], [bx, by], [cx, cy]) {
  const area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
  const s = ((bx - ax) * (y - ay) - (x - ax) * (by - ay)) / area;
  const t = ((x - ax) * (cy - ay) - (cx - ax) * (y - ay)) / area;
  return s >= 0 && t >= 0 && s + t <= 1;
}

function render(width, height, shade, samples = 3) {
  const rgba = new Uint8Array(width * height * 4);

  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      let red = 0;
      let green = 0;
      let blue = 0;
      let alpha = 0;

      for (let sy = 0; sy < samples; sy += 1) {
        for (let sx = 0; sx < samples; sx += 1) {
          const px = x + (sx + 0.5) / samples;
          const py = y + (sy + 0.5) / samples;
          const [r, g, b, a] = shade(px, py);
          red += r * a;
          green += g * a;
          blue += b * a;
          alpha += a;
        }
      }

      const total = samples * samples;
      const offset = (y * width + x) * 4;
      const coverage = alpha / total;
      rgba[offset] = coverage === 0 ? 0 : Math.round(red / alpha);
      rgba[offset + 1] = coverage === 0 ? 0 : Math.round(green / alpha);
      rgba[offset + 2] = coverage === 0 ? 0 : Math.round(blue / alpha);
      rgba[offset + 3] = Math.round(coverage * 255);
    }
  }

  return rgba;
}

/**
 * The Vistara mark: a framed vista with a low sun and two ridges, drawn on a
 * square tile. `inset` reserves the maskable safe area.
 */
function markShader(size, { inset = 0, cornerRatio = 0.22 } = {}) {
  const tile = {
    left: inset,
    top: inset,
    width: size - inset * 2,
    height: size - inset * 2,
    radius: (size - inset * 2) * cornerRatio,
  };
  const u = (value) => tile.left + tile.width * value;
  const v = (value) => tile.top + tile.height * value;

  const sun = { x: u(0.7), y: v(0.3), r: tile.width * 0.12 };
  const horizon = v(0.78);
  const ridgeBack = [
    [u(0.02), horizon],
    [u(0.4), v(0.26)],
    [u(0.78), horizon],
  ];
  const ridgeFront = [
    [u(0.3), horizon],
    [u(0.64), v(0.44)],
    [u(0.98), horizon],
  ];

  return (x, y) => {
    if (!insideRoundedRect(x, y, tile)) {
      return [0, 0, 0, 0];
    }

    const depth = (y - tile.top) / tile.height;
    let pixel = [...mix(palette.ink, palette.inkSoft, depth * 0.9), 1];

    if (insideCircle(x, y, sun.x, sun.y, sun.r)) {
      pixel = over(pixel, palette.sun, 1);
    }

    if (insideTriangle(x, y, ...ridgeBack)) {
      pixel = over(pixel, palette.accentDeep, 1);
    }

    if (insideTriangle(x, y, ...ridgeFront)) {
      pixel = over(pixel, palette.accent, 1);
    }

    if (y >= horizon) {
      const below = (y - horizon) / (tile.top + tile.height - horizon);
      pixel = over(pixel, mix(palette.accentDark, palette.ink, below * 0.6), 1);
    }

    return pixel;
  };
}

function socialShader(width, height) {
  const sun = { x: width * 0.78, y: height * 0.26, r: height * 0.11 };
  const horizon = height * 0.74;
  const ridges = [
    {
      points: [
        [width * 0.24, horizon],
        [width * 0.56, height * 0.34],
        [width * 0.88, horizon],
      ],
      color: palette.accentDeep,
    },
    {
      points: [
        [width * 0.52, horizon],
        [width * 0.84, height * 0.48],
        [width * 1.16, horizon],
      ],
      color: palette.accent,
    },
  ];
  const tileSize = Math.round(height * 0.3);
  const tile = {
    left: Math.round(width * 0.07),
    top: Math.round(height * 0.5 - tileSize / 2),
    size: tileSize,
  };
  const tileShader = markShader(tileSize);

  return (x, y) => {
    let pixel = [...mix(palette.ink, palette.inkSoft, (y / height) * 1.1), 1];

    if (insideCircle(x, y, sun.x, sun.y, sun.r)) {
      pixel = over(pixel, palette.sun, 1);
    }

    for (const ridge of ridges) {
      if (insideTriangle(x, y, ...ridge.points)) {
        pixel = over(pixel, ridge.color, 1);
      }
    }

    if (y >= horizon) {
      const below = (y - horizon) / (height - horizon);
      pixel = over(pixel, mix(palette.accentDark, palette.ink, below * 0.7), 1);
    }

    const tx = x - tile.left;
    const ty = y - tile.top;
    if (tx >= 0 && ty >= 0 && tx < tile.size && ty < tile.size) {
      const [r, g, b, a] = tileShader(tx, ty);
      if (a > 0) {
        pixel = over(pixel, [r, g, b], a);
      }
    }

    return pixel;
  };
}

function markPng(size, options) {
  return encodePng(size, size, render(size, size, markShader(size, options)));
}

export const brandSvg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="Vistara">
  <title>Vistara</title>
  <defs>
    <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#11110f" />
      <stop offset="1" stop-color="#1e201b" />
    </linearGradient>
    <clipPath id="frame">
      <rect x="0" y="0" width="64" height="64" rx="14" />
    </clipPath>
  </defs>
  <g clip-path="url(#frame)">
    <rect width="64" height="64" fill="url(#sky)" />
    <circle cx="44.8" cy="19.2" r="7.7" fill="#efbd67" />
    <path d="M1.3 49.9 25.6 16.6 49.9 49.9Z" fill="#2f8067" />
    <path d="M19.2 49.9 41 28.2 62.7 49.9Z" fill="#79c9a9" />
    <rect x="0" y="49.9" width="64" height="14.1" fill="#19382e" />
  </g>
</svg>
`;

export function buildBrandAssets() {
  const icon192 = markPng(192);
  const icon512 = markPng(512);
  const maskable = markPng(512, { inset: 512 * 0.12, cornerRatio: 0.5 });
  const appleTouch = markPng(180, { cornerRatio: 0 });
  const favicon = markPng(32, { cornerRatio: 0.22 });
  const social = encodePng(
    1200,
    630,
    render(1200, 630, socialShader(1200, 630), 2),
  );

  const manifest = {
    name: 'Vistara',
    short_name: 'Vistara',
    description:
      'Vistara, a self-hosted image control plane and responsive gallery.',
    id: '/',
    start_url: '/',
    scope: '/',
    display: 'standalone',
    orientation: 'any',
    background_color: '#11110f',
    theme_color: '#11110f',
    icons: [
      { src: '/favicon.svg', sizes: 'any', type: 'image/svg+xml' },
      { src: '/icon-192.png', sizes: '192x192', type: 'image/png' },
      { src: '/icon-512.png', sizes: '512x512', type: 'image/png' },
      {
        src: '/icon-maskable-512.png',
        sizes: '512x512',
        type: 'image/png',
        purpose: 'maskable',
      },
    ],
  };

  return new Map([
    ['public/favicon.svg', Buffer.from(brandSvg, 'utf8')],
    ['public/favicon.ico', encodeIco(favicon, 32)],
    ['public/icon-192.png', icon192],
    ['public/icon-512.png', icon512],
    ['public/icon-maskable-512.png', maskable],
    ['public/apple-touch-icon.png', appleTouch],
    ['public/social-preview.png', social],
    [
      'public/manifest.webmanifest',
      Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`, 'utf8'),
    ],
  ]);
}
