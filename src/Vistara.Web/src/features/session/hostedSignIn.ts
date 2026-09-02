/**
 * The browser side of hosted sign-in.
 *
 * Everything that matters about a hosted sign-in happens on the server: the
 * state, the nonce, the code verifier, the authorization code, and the session
 * cookie never exist in this application at all. What is left here is
 * navigation, so this module only decides where the browser may be sent and
 * remembers which provider opened this tab's session, so signing out can end
 * that provider's session too.
 *
 * Nothing written here is a credential. The provider key is a published route
 * segment, and no token, code, or state is ever stored, logged, or read back.
 */

/** The provider key vocabulary the API's route constraint accepts. */
const providerKeyPattern = /^[A-Za-z0-9_-]{1,64}$/;

/**
 * Which hosted provider this tab signed in with. It lives in session storage
 * because it describes one tab's session and must not outlive the browser:
 * a tab that never started a hosted sign-in signs out locally instead.
 */
export const hostedProviderKey = 'vistara:signInProvider';

/** The origin every same-origin candidate is resolved and compared against. */
const relativeBase = 'https://vistara.invalid';

/**
 * The URL to navigate to in order to start hosted sign-in, or undefined when
 * the deployment published something that is not a same-origin path.
 *
 * The start route is never fetched: it answers a redirect to the provider that
 * only a top-level navigation may follow. The return target is appended for
 * the server to validate; a candidate that could leave this origin is dropped
 * here rather than sent.
 */
export function hostedStartUrl(
  startUrl: string,
  returnTo?: string,
): string | undefined {
  if (!startUrl.startsWith('/') || startUrl.startsWith('//')) {
    return undefined;
  }

  let resolved: URL;
  try {
    resolved = new URL(startUrl, relativeBase);
  } catch {
    return undefined;
  }

  if (resolved.origin !== relativeBase || resolved.search || resolved.hash) {
    return undefined;
  }

  if (returnTo && returnTo.startsWith('/') && !returnTo.startsWith('//')) {
    resolved.searchParams.set('returnTo', returnTo);
  }

  return `${resolved.pathname}${resolved.search}`;
}

export function rememberHostedProvider(
  providerId: string,
  storage: Pick<Storage, 'setItem'> | undefined = readSessionStorage(),
): void {
  if (!providerKeyPattern.test(providerId)) {
    return;
  }

  try {
    storage?.setItem(hostedProviderKey, providerId);
  } catch {
    // A browser that refuses session storage simply signs out locally.
  }
}

export function readHostedProvider(
  storage: Pick<Storage, 'getItem'> | undefined = readSessionStorage(),
): string | undefined {
  let recorded: string | null | undefined;
  try {
    recorded = storage?.getItem(hostedProviderKey);
  } catch {
    return undefined;
  }

  return recorded && providerKeyPattern.test(recorded) ? recorded : undefined;
}

export function forgetHostedProvider(
  storage: Pick<Storage, 'removeItem'> | undefined = readSessionStorage(),
): void {
  try {
    storage?.removeItem(hostedProviderKey);
  } catch {
    // Nothing here is a credential, so failing to clear it is not a leak.
  }
}

/**
 * The provider URL a sign-out answer may send this browser to. Only an
 * absolute HTTP or HTTPS URL is followed: the session is already revoked by
 * the time this is read, and a scheme the answer chose must never become a
 * script this page runs.
 */
export function providerSignOutUrl(
  candidate: string | null | undefined,
): string | undefined {
  if (!candidate) {
    return undefined;
  }

  try {
    const resolved = new URL(candidate);
    return resolved.protocol === 'https:' || resolved.protocol === 'http:'
      ? resolved.href
      : undefined;
  } catch {
    return undefined;
  }
}

/** Ends the provider session in the browser, after the local one is gone. */
export function navigateToProviderSignOut(url: string): void {
  if (typeof window !== 'undefined') {
    window.location.assign(url);
  }
}

function readSessionStorage(): Storage | undefined {
  try {
    return typeof sessionStorage === 'undefined' ? undefined : sessionStorage;
  } catch {
    return undefined;
  }
}
