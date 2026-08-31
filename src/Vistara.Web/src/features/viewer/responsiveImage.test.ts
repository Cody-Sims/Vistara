import { describe, expect, it } from 'vitest';
import type { AssetSummary } from '../../api/generated';
import { buildResponsiveImage } from './responsiveImage';

const asset: AssetSummary = {
  id: 'asset-1',
  title: 'Alpine lake',
  description: 'A still lake below a snowy ridge',
  status: 'ready',
  visibility: 'private',
  revisionNumber: 1,
  contentType: 'image/jpeg',
  format: 'jpeg',
  width: 4000,
  height: 3000,
  sizeBytes: 2_000_000,
  importedAt: '2026-01-01T12:00:00Z',
  updatedAt: '2026-01-01T12:00:00Z',
  favorite: false,
  tags: [],
  version: 1,
  renditions: [
    {
      kind: 'thumb',
      path: '/media/grid-512.jpg',
      width: 512,
      height: 384,
      contentType: 'image/jpeg',
    },
    {
      kind: 'grid',
      path: '/media/grid-1024.webp',
      width: 1024,
      height: 768,
      contentType: 'image/webp',
    },
    {
      kind: 'viewer',
      path: '/media/viewer-2400.webp',
      width: 2400,
      height: 1800,
      contentType: 'image/webp',
    },
  ],
};

/**
 * The byte-for-byte gallery list payload pinned by
 * tests/Vistara.Api.ContractTests/AssetQueries/AssetEnumSerializationContractTests.cs,
 * so the browser is exercised against the casing the server really emits.
 */
const readyPrivateAssetJson = String.raw`{"id":"01990a2a-bc00-7000-8000-0000000007a1","title":"Alpine lake","description":"A still lake below a snowy ridge","status":"ready","visibility":"private","revisionNumber":1,"contentType":"image/jpeg","format":"jpeg","width":4000,"height":3000,"sizeBytes":2000000,"capturedAt":"2026-08-28T09:00:00+00:00","importedAt":"2026-08-29T09:00:00+00:00","updatedAt":"2026-08-29T09:00:00+00:00","favorite":false,"tags":[],"renditions":[{"kind":"thumb","path":"/media/pipeline/source/thumb-512.webp","width":512,"height":384,"contentType":"image/webp"},{"kind":"grid","path":"/media/pipeline/source/grid-1024.webp","width":1024,"height":768,"contentType":"image/webp"},{"kind":"viewer","path":"/media/pipeline/source/viewer-2400.webp","width":2400,"height":1800,"contentType":"image/webp"}],"version":1}`;

describe('responsive image delivery', () => {
  it('builds immutable-path srcset and viewport-aware sizes without provider URLs', () => {
    expect(buildResponsiveImage(asset, 'grid', false)).toEqual({
      alt: 'A still lake below a snowy ridge',
      src: '/media/viewer-2400.webp',
      srcSet:
        '/media/grid-512.jpg 512w, /media/grid-1024.webp 1024w, /media/viewer-2400.webp 2400w',
      sizes:
        '(max-width: 30rem) 50vw, (max-width: 64rem) 33vw, min(20vw, 20rem)',
      loading: 'lazy',
      fetchPriority: 'auto',
      width: 4000,
      height: 3000,
    });
  });

  it('allows exactly the caller-designated image to receive high priority', () => {
    expect(buildResponsiveImage(asset, 'viewer', true)).toMatchObject({
      loading: 'eager',
      fetchPriority: 'high',
      sizes: '100vw',
    });
  });

  it('does not invent an image while an asset is processing', () => {
    expect(
      buildResponsiveImage({ ...asset, status: 'processing' }, 'grid', true),
    ).toBeNull();
  });

  it('rejects network-path and absolute rendition URLs from untrusted responses', () => {
    const unsafe = {
      ...asset,
      renditions: [
        ...asset.renditions,
        {
          kind: 'viewer' as const,
          path: '//images.example.test/private.jpg',
          width: 4000,
          height: 3000,
          contentType: 'image/jpeg',
        },
        {
          kind: 'viewer' as const,
          path: 'https://images.example.test/private.jpg',
          width: 5000,
          height: 3750,
          contentType: 'image/jpeg',
        },
      ],
    };

    expect(buildResponsiveImage(unsafe, 'viewer', true)?.src).toBe(
      '/media/viewer-2400.webp',
    );
  });

  it('serves a shared asset that carries no gallery status', () => {
    const shared = {
      title: 'Alpine lake',
      width: 4000,
      height: 3000,
      renditions: asset.renditions,
    };

    expect(buildResponsiveImage(shared, 'share', true)).toEqual({
      alt: 'Alpine lake',
      src: '/media/viewer-2400.webp',
      srcSet:
        '/media/grid-512.jpg 512w, /media/grid-1024.webp 1024w, /media/viewer-2400.webp 2400w',
      sizes: '(max-width: 40rem) 100vw, (max-width: 70rem) 50vw, 33vw',
      loading: 'eager',
      fetchPriority: 'high',
      width: 4000,
      height: 3000,
    });
  });

  it('renders an image from the payload the gallery API really serves', () => {
    const served = JSON.parse(readyPrivateAssetJson) as AssetSummary;

    expect(served.status).toBe('ready');
    expect(served.visibility).toBe('private');
    expect(buildResponsiveImage(served, 'grid', false)).toEqual({
      alt: 'A still lake below a snowy ridge',
      src: '/media/pipeline/source/viewer-2400.webp',
      srcSet:
        '/media/pipeline/source/thumb-512.webp 512w, ' +
        '/media/pipeline/source/grid-1024.webp 1024w, ' +
        '/media/pipeline/source/viewer-2400.webp 2400w',
      sizes:
        '(max-width: 30rem) 50vw, (max-width: 64rem) 33vw, min(20vw, 20rem)',
      loading: 'lazy',
      fetchPriority: 'auto',
      width: 4000,
      height: 3000,
    });
  });
});
