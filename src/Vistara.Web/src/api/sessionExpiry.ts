/**
 * One place every client reports a refused request, so a session that expired
 * mid-visit ends once no matter which client noticed it first. The generated
 * gallery client is not edited; it is constructed with the wrapped fetch below.
 */

const listeners = new Set<() => void>();

/** Routes the API answers without a session, where a 401 says nothing about it. */
const anonymousPrefixes = ['/api/v1/public/', '/api/v1/auth/login', '/api/v1/setup'];

export function onSessionExpired(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function reportSessionExpired(): void {
  for (const listener of [...listeners]) {
    listener();
  }
}

function requestPath(input: RequestInfo | URL): string {
  const raw =
    typeof input === 'string'
      ? input
      : input instanceof URL
        ? input.href
        : input.url;

  try {
    return new URL(raw, 'https://vistara.invalid').pathname;
  } catch {
    return raw;
  }
}

function needsSession(input: RequestInfo | URL): boolean {
  const path = requestPath(input);
  return !anonymousPrefixes.some((prefix) => path.startsWith(prefix));
}

/**
 * Wraps a fetch so a 401 from an account-scoped request reports the expiry.
 * The wrapper reads `globalThis.fetch` on every call so a page that swaps it,
 * and a test that stubs it, are both honoured.
 */
export function reportingFetch(inner?: typeof fetch): typeof fetch {
  return async (input, init) => {
    const send = inner ?? globalThis.fetch;
    const response = await send(input, init);
    if (response.status === 401 && needsSession(input)) {
      reportSessionExpired();
    }

    return response;
  };
}
