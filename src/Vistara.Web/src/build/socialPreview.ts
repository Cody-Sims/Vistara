export interface HtmlTag {
  readonly tag: 'meta';
  readonly attrs: Record<string, string>;
  readonly injectTo: 'head';
}

/**
 * Open Graph needs an absolute image URL. The preview image is only advertised
 * when the deployment origin is known at build time, because a relative or
 * guessed URL is worse than no preview at all.
 */
export function socialPreviewTags(
  siteOrigin: string | undefined,
  base: string,
): readonly HtmlTag[] {
  const origin = normalizeOrigin(siteOrigin);
  if (!origin) {
    return [];
  }

  const path = `${base.startsWith('/') ? base : `/${base}`}${
    base.endsWith('/') ? '' : '/'
  }social-preview.png`;
  const image = `${origin}${path}`;

  return [
    property('og:image', image),
    property('og:image:width', '1200'),
    property('og:image:height', '630'),
    property(
      'og:image:alt',
      'The Vistara mark beside a stylised mountain vista at dusk.',
    ),
    named('twitter:card', 'summary_large_image'),
    named('twitter:image', image),
  ];
}

function property(name: string, content: string): HtmlTag {
  return {
    tag: 'meta',
    attrs: { property: name, content },
    injectTo: 'head',
  };
}

function named(name: string, content: string): HtmlTag {
  return { tag: 'meta', attrs: { name, content }, injectTo: 'head' };
}

function normalizeOrigin(value: string | undefined): string | undefined {
  if (!value) {
    return undefined;
  }

  try {
    const url = new URL(value);
    return url.protocol === 'https:' || url.protocol === 'http:'
      ? url.origin
      : undefined;
  } catch {
    return undefined;
  }
}
