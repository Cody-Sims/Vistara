import type { AssetSummary } from '../../api/generated';

export type ImageContext = 'grid' | 'viewer';

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
  asset: AssetSummary,
  context: ImageContext,
  highPriority: boolean,
): ResponsiveImage | null {
  if (asset.status !== 'ready') return null;

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
