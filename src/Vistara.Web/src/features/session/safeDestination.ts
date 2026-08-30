/**
 * Only a same-origin absolute path is followed after sign-in, so a crafted
 * `returnTo` cannot forward a visitor to another site.
 */
export function safeDestination(value: string | null): string {
  if (!value || !value.startsWith('/')) {
    return '/library';
  }

  const base = 'https://vistara.invalid';
  try {
    const resolved = new URL(value, base);
    return resolved.origin === base
      ? `${resolved.pathname}${resolved.search}${resolved.hash}`
      : '/library';
  } catch {
    return '/library';
  }
}
