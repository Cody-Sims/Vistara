import { describe, expect, it } from 'vitest';
import { socialPreviewTags } from './socialPreview';

describe('social preview tags', () => {
  it('advertises an absolute image for the deployment origin', () => {
    const tags = socialPreviewTags('https://gallery.example', '/');

    expect(tags.map((tag) => tag.attrs.content)).toContain(
      'https://gallery.example/social-preview.png',
    );
    expect(
      tags.some((tag) => tag.attrs.property === 'og:image'),
    ).toBe(true);
  });

  it('keeps the base path of a sub-path deployment', () => {
    const tags = socialPreviewTags('https://example.github.io', '/Vistara/');

    expect(
      tags.find((tag) => tag.attrs.property === 'og:image')?.attrs.content,
    ).toBe('https://example.github.io/Vistara/social-preview.png');
  });

  it('emits nothing when the origin is unknown or unusable', () => {
    expect(socialPreviewTags(undefined, '/')).toEqual([]);
    expect(socialPreviewTags('', '/')).toEqual([]);
    expect(socialPreviewTags('not a url', '/')).toEqual([]);
    expect(socialPreviewTags('file:///tmp', '/')).toEqual([]);
    expect(socialPreviewTags('javascript:alert(1)', '/')).toEqual([]);
  });
});
