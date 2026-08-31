/** Derives the URL-safe workspace address the API accepts from a display name. */
export function slugify(value: string): string {
  return value
    .normalize('NFKD')
    // Diacritics decomposed by NFKD are dropped rather than turned into
    // separators, so "Ålesund" becomes "alesund".
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 63);
}

export function isValidSlug(value: string): boolean {
  return /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/.test(value);
}
