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
      kind: 'thumbnail',
      path: '/media/grid-512.jpg',
      width: 512,
      height: 384,
      contentType: 'image/jpeg',
    },
    {
      kind: 'preview',
      path: '/media/grid-1024.webp',
      width: 1024,
      height: 768,
      contentType: 'image/webp',
    },
    {
      kind: 'display',
      path: '/media/viewer-2400.webp',
      width: 2400,
      height: 1800,
      contentType: 'image/webp',
    },
  ],
};

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
          kind: 'display' as const,
          path: '//images.example.test/private.jpg',
          width: 4000,
          height: 3000,
          contentType: 'image/jpeg',
        },
        {
          kind: 'display' as const,
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
});
