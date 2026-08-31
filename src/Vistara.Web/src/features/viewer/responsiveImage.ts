import type { AssetRendition, AssetStatus } from '../../api/generated';

export type ImageContext = 'grid' | 'share' | 'viewer';

/**
 * The shape every renderable image shares, whether it comes from the gallery
 * or from a public share where gallery-only fields are withheld.
 */
export interface ResponsiveImageSource {
  readonly title: string;
  readonly description?: string;
  readonly status?: AssetStatus;
  readonly width: number;
  readonly height: number;
  readonly renditions: readonly AssetRendition[];
}

export interface ResponsiveImage {
  alt: string;
  src: string;
  srcSet: string;
  sizes: string;
  loading: 'eager' | 'lazy';
  fetchPriority: 'high' | 'auto';
  width: number;
  height: number;
}

const sizes: Record<ImageContext, string> = {
  grid: '(max-width: 30rem) 50vw, (max-width: 64rem) 33vw, min(20vw, 20rem)',
  share: '(max-width: 40rem) 100vw, (max-width: 70rem) 50vw, 33vw',
  viewer: '100vw',
};

function isSameOriginPath(path: string) {
  if (!path.startsWith('/')) return false;

  try {
    const base = new URL('https://vistara.invalid/');
    return new URL(path, base).origin === base.origin;
  } catch {
    return false;
  }
}

export function buildResponsiveImage(
  asset: ResponsiveImageSource,
  context: ImageContext,
  highPriority: boolean,
): ResponsiveImage | null {
  if (asset.status !== undefined && asset.status !== 'ready') return null;

  const renditions = asset.renditions
    .filter((rendition) => isSameOriginPath(rendition.path))
    .slice()
    .sort((left, right) => left.width - right.width);
  const largest = renditions.at(-1);

  if (!largest) return null;

  return {
    alt: asset.description || asset.title,
    src: largest.path,
    srcSet: renditions
      .map((rendition) => `${rendition.path} ${rendition.width}w`)
      .join(', '),
    sizes: sizes[context],
    loading: highPriority ? 'eager' : 'lazy',
    fetchPriority: highPriority ? 'high' : 'auto',
    width: asset.width,
    height: asset.height,
  };
}
