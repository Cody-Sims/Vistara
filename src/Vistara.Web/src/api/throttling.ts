import { VistaraApiError } from './generated/client';

/**
 * The generated client reads only the problem body from a failed answer, so a
 * `429` would arrive without the `Retry-After` the API sent. Rather than edit
 * generated code, the header is folded into the problem body before the client
 * parses it, and read back through `retryAfterSeconds`.
 */

const field = 'retryAfterSeconds';

interface ThrottledProblem {
  readonly [field]?: number;
}

function secondsFrom(header: string | null, now: number): number | undefined {
  if (header === null) {
    return undefined;
  }

  const seconds = Number(header);
  if (Number.isFinite(seconds)) {
    return Math.max(0, Math.ceil(seconds));
  }

  const date = Date.parse(header);
  return Number.isNaN(date)
    ? undefined
    : Math.max(0, Math.ceil((date - now) / 1000));
}

export function withRetryAfter(inner?: typeof fetch): typeof fetch {
  return async (input, init) => {
    const send = inner ?? globalThis.fetch;
    const response = await send(input, init);
    if (response.status !== 429) {
      return response;
    }

    const seconds = secondsFrom(response.headers.get('Retry-After'), Date.now());
    if (seconds === undefined) {
      return response;
    }

    let problem: unknown;
    try {
      problem = await response.clone().json();
    } catch {
      return response;
    }

    if (typeof problem !== 'object' || problem === null) {
      return response;
    }

    return new Response(JSON.stringify({ ...problem, [field]: seconds }), {
      status: response.status,
      statusText: response.statusText,
      headers: response.headers,
    });
  };
}

/** How long the API asked a caller to wait, when it said so. */
export function retryAfterSeconds(error: unknown): number | undefined {
  if (!(error instanceof VistaraApiError) || error.status !== 429) {
    return undefined;
  }

  const seconds = (error.problem as ThrottledProblem)[field];
  return typeof seconds === 'number' && Number.isFinite(seconds)
    ? seconds
    : undefined;
}
