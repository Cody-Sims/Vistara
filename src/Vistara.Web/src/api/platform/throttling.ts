import { VistaraApiError } from '../generated/client';
import type { ApiProblemDetails } from '../generated/models';

/**
 * A `429` answer. `Retry-After` is published in seconds, so the wait can be
 * shown to the operator instead of a bare failure.
 */
export class VistaraThrottledError extends VistaraApiError {
  public constructor(
    problem: ApiProblemDetails,
    public readonly retryAfterSeconds?: number,
  ) {
    super(429, problem);
    this.name = 'VistaraThrottledError';
  }
}

/** Reads `Retry-After` as whole seconds, ignoring anything unusable. */
export function readRetryAfterSeconds(
  headers: Headers,
): number | undefined {
  const raw = headers.get('Retry-After');
  if (!raw) {
    return undefined;
  }

  const seconds = Number.parseInt(raw.trim(), 10);
  if (Number.isFinite(seconds) && seconds >= 0) {
    return seconds;
  }

  const when = Date.parse(raw);
  if (Number.isNaN(when)) {
    return undefined;
  }

  return Math.max(0, Math.round((when - Date.now()) / 1000));
}

export function describeRetryAfter(seconds: number | undefined): string {
  if (seconds === undefined) {
    return 'Wait a moment and try again.';
  }

  if (seconds <= 1) {
    return 'Try again in a second.';
  }

  if (seconds < 90) {
    return `Try again in ${seconds} seconds.`;
  }

  const minutes = Math.round(seconds / 60);
  return `Try again in about ${minutes} minutes.`;
}
