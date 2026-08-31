import { sessionCredentials, type SessionCredentials } from './credentials';

/**
 * Wraps a fetch so every unsafe same-origin request made on behalf of a cookie
 * session carries the antiforgery header the API published. The generated
 * gallery client is not edited; it is constructed with the wrapped fetch, and
 * the upload client wraps whichever fetch it was handed.
 *
 * Requests that need no token are left exactly as the caller made them: safe
 * methods, another origin, and a session authenticated by an API key or a
 * bearer token, which the API issues no antiforgery token to.
 */

const safeMethods = new Set(['GET', 'HEAD', 'OPTIONS', 'TRACE']);

/** Routes the API answers without a session, so no session is read for them. */
const anonymousPrefixes = [
  '/api/v1/public/',
  '/api/v1/auth/login',
  '/api/v1/setup',
];

/**
 * The one refusal the API answers when a cookie session presents no valid
 * antiforgery token. It is decided before the request reaches an endpoint, so
 * a request refused with it changed nothing and may be sent again.
 */
const antiforgeryRefusal = 'cookie_auth.antiforgery_required';

export function credentialedFetch(
  inner?: typeof fetch,
  credentials: SessionCredentials = sessionCredentials,
): typeof fetch {
  return async (input, init) => {
    const send = inner ?? globalThis.fetch;
    const request = input instanceof Request ? input : undefined;
    const url =
      typeof input === 'string'
        ? input
        : input instanceof URL
          ? input.href
          : input.url;
    const method = (init?.method ?? request?.method ?? 'GET').toUpperCase();
    if (safeMethods.has(method) || !isSameOrigin(url)) {
      return send(input, init);
    }

    if (!isAnonymous(url)) {
      await credentials.ensure();
    }

    const headers = mergedHeaders(request, init);
    const presented = credentials.applyTo(headers);
    // Decided, and the body duplicated, before the first send consumes it:
    // afterwards a request has been read and can no longer answer for itself.
    const replay = prepareReplay(request, init);
    const response = await send(input, { ...init, headers });
    if (
      response.status !== 403 ||
      !credentials.spendsAntiforgeryToken ||
      !replay.possible
    ) {
      return response;
    }

    if (!(await refusedForAntiforgery(response))) {
      return response;
    }

    // The session rotated its token, and the refusal proves the request was
    // stopped before it did anything. Only a genuinely newer token is worth
    // sending again; anything else would be a blind replay of a mutation.
    await credentials.refresh();
    const retryHeaders = mergedHeaders(request, init);
    const rotated = credentials.applyTo(retryHeaders);
    if (!rotated || rotated === presented) {
      return response;
    }

    // The duplicate carries the body, the method, and the abort signal of the
    // request that was refused; one attempt, and no further replay.
    return send(replay.request ?? input, { ...init, headers: retryHeaders });
  };
}

/** Whether the request goes back to the origin that served this page. */
export function isSameOrigin(url: string): boolean {
  const origin = globalThis.location?.origin;
  if (!origin || origin === 'null') {
    // With no page origin to compare against, only a relative URL can be
    // certain to reach the API this client was built for.
    return !/^[a-z][a-z\d+.-]*:/i.test(url);
  }

  try {
    return new URL(url, origin).origin === origin;
  } catch {
    return false;
  }
}

function isAnonymous(url: string): boolean {
  const path = pathOf(url);
  return anonymousPrefixes.some((prefix) => path.startsWith(prefix));
}

function pathOf(url: string): string {
  try {
    return new URL(url, globalThis.location?.origin ?? 'https://vistara.invalid')
      .pathname;
  } catch {
    return url;
  }
}

function mergedHeaders(
  request: Request | undefined,
  init: RequestInit | undefined,
): Headers {
  const headers = new Headers(request?.headers);
  if (init?.headers) {
    // The caller's own headers win, exactly as fetch resolves them.
    for (const [name, value] of new Headers(init.headers)) {
      headers.set(name, value);
    }
  }

  return headers;
}

interface Replay {
  /** Whether the request may be sent a second time at all. */
  readonly possible: boolean;
  /** The duplicate to send, taken before the original was read. */
  readonly request?: Request;
}

const noReplay: Replay = { possible: false };

/**
 * Decides whether a request can be sent again, and takes the duplicate that
 * would be sent, before anything has read the body.
 *
 * A body the caller streams is read once and is gone, and a request that has
 * already been read cannot answer again; neither is ever replayed. A request
 * object is duplicated instead of reused, because sending the original a
 * second time would send a body that the first attempt consumed.
 */
function prepareReplay(
  request: Request | undefined,
  init: RequestInit | undefined,
): Replay {
  if (isStreamedBody(init?.body)) {
    return noReplay;
  }

  if (!request) {
    return { possible: true };
  }

  if (request.bodyUsed) {
    return noReplay;
  }

  try {
    return { possible: true, request: request.clone() };
  } catch {
    // A body that cannot be duplicated is a body that is sent once.
    return noReplay;
  }
}

function isStreamedBody(body: BodyInit | null | undefined): boolean {
  return (
    typeof ReadableStream !== 'undefined' && body instanceof ReadableStream
  );
}

/**
 * Reads the refusal without consuming the response the caller will read. Only
 * the antiforgery refusal is answered here; every other 403 is the API's word
 * on the request itself.
 */
async function refusedForAntiforgery(response: Response): Promise<boolean> {
  try {
    const problem = (await response.clone().json()) as { code?: string };
    return problem.code === antiforgeryRefusal;
  } catch {
    return false;
  }
}
