import { describe, expect, it } from 'vitest';
import { isValidSlug, slugify } from './workspaceSlug';

describe('workspace address', () => {
  it('derives a URL-safe address from a display name', () => {
    expect(slugify('Harbour Lights Studio')).toBe('harbour-lights-studio');
    expect(slugify('  Ada’s Photos!  ')).toBe('ada-s-photos');
    expect(slugify('Ålesund Foto')).toBe('alesund-foto');
    expect(slugify('###')).toBe('');
  });

  it('never exceeds the length a tenant slug allows', () => {
    expect(slugify('a'.repeat(200))).toHaveLength(63);
  });

  it('accepts only lowercase, digits, and inner hyphens', () => {
    expect(isValidSlug('studio')).toBe(true);
    expect(isValidSlug('studio-2')).toBe(true);
    expect(isValidSlug('s')).toBe(true);
    expect(isValidSlug('-studio')).toBe(false);
    expect(isValidSlug('studio-')).toBe(false);
    expect(isValidSlug('Studio')).toBe(false);
    expect(isValidSlug('studio space')).toBe(false);
    expect(isValidSlug('')).toBe(false);
    // The API rejects consecutive hyphens, so the browser must too.
    expect(isValidSlug('acme--prod')).toBe(false);
  });

  it('never derives an address the API would reject', () => {
    expect(slugify('Acme -- Prod')).toBe('acme-prod');
    expect(isValidSlug(slugify('Acme -- Prod'))).toBe(true);
  });
});
