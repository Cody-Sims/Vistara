/**
 * The credential this browser session spends, held in memory for exactly as
 * long as the session lasts.
 *
 * A cookie session is refused by the API on every unsafe request that arrives
 * without the antiforgery token published by `GET /api/v1/me` and by login, so
 * every client that mutates on the session's behalf needs the same token under
 * the same header. One provider holds it, so a rotation reaches all of them and
 * signing out leaves none of them holding it.
 *
 * The token is never written to storage, never logged, and never sent to an
 * origin other than the API this page was served from.
 */

export type CredentialKind = 'cookie' | 'apiKey' | 'bearer';

/** Header the platform uses until `GET /api/v1/me` names another one. */
const defaultHeaderName = 'X-Vistara-CSRF';

/** A header name the API may publish: an HTTP token, and nothing else. */
const headerNamePattern = /^[\w-]+$/;

/** The published shape of a session, from `GET /api/v1/me` or from login. */
export interface SessionCredentialSource {
  readonly csrfHeaderName?: string;
  readonly csrfToken?: string;
  readonly authenticationKind?: CredentialKind;
}

export class SessionCredentials {
  #headerName = defaultHeaderName;
  #token = '';
  /** The credential the API last said it authenticated this browser with. */
  #kind: CredentialKind | undefined;
  #read: (() => Promise<unknown>) | undefined;
  #reading: Promise<void> | undefined;

  /** Header the deployment expects the antiforgery token in. */
  public get headerName(): string {
    return this.#headerName;
  }

  public get kind(): CredentialKind | undefined {
    return this.#kind;
  }

  public get carriesToken(): boolean {
    return this.#token.length > 0;
  }

  /**
   * Whether an antiforgery token is part of this credential at all. A key or
   * bearer token is issued none, so a request carrying one never spends one.
   */
  public get spendsAntiforgeryToken(): boolean {
    return this.#kind !== 'apiKey' && this.#kind !== 'bearer';
  }

  /** Publishes what a session read, a login, or a rotation just answered. */
  public adopt(session: SessionCredentialSource): void {
    if (
      session.csrfHeaderName &&
      headerNamePattern.test(session.csrfHeaderName)
    ) {
      this.#headerName = session.csrfHeaderName;
    }

    if (session.authenticationKind) {
      this.#kind = session.authenticationKind;
    }

    if (!this.spendsAntiforgeryToken) {
      this.#token = '';
      return;
    }

    if (session.csrfToken) {
      this.#token = session.csrfToken;
    }
  }

  /**
   * Drops the credential when the session ends: signing out, a refused
   * request, or another account taking this device over. The header name is
   * kept because it describes the deployment rather than the account.
   */
  public clear(): void {
    this.#token = '';
    this.#kind = undefined;
    this.#reading = undefined;
  }

  /** Returns the provider to its start, for a test that shares this module. */
  public reset(): void {
    this.clear();
    this.#headerName = defaultHeaderName;
  }

  /**
   * Registers how the session is read again. The platform client owns the
   * session routes, so it registers itself; a build with no API registers
   * nothing and the provider simply never refreshes.
   */
  public useRefresher(read: () => Promise<unknown>): void {
    this.#read = read;
  }

  /**
   * Reads the session when an unsafe request is about to be sent without a
   * token, which is where a reloaded browser starts: it holds the session
   * cookie and no token. Concurrent callers share one read.
   */
  public async ensure(): Promise<void> {
    if (this.#token || !this.spendsAntiforgeryToken) {
      return;
    }

    await this.refresh();
  }

  /**
   * Reads the session even when a token is held, which is how a token the
   * server rotated away is replaced. Concurrent callers share one read, and a
   * failed read leaves the request to fail on its own terms.
   */
  public refresh(): Promise<void> {
    const read = this.#read;
    if (!read) {
      return Promise.resolve();
    }

    this.#reading ??= read().then(
      () => {
        this.#reading = undefined;
      },
      () => {
        this.#reading = undefined;
      },
    );

    return this.#reading;
  }

  /**
   * Writes the antiforgery header for an unsafe request and answers the token
   * it now carries. A token the caller set is kept: it is never replaced and
   * never joined by a second copy of the header, which the API refuses.
   */
  public applyTo(headers: Headers): string | undefined {
    if (!this.spendsAntiforgeryToken) {
      return undefined;
    }

    const supplied = headers.get(this.#headerName);
    if (supplied) {
      return supplied;
    }

    if (!this.#token) {
      return undefined;
    }

    headers.set(this.#headerName, this.#token);
    return this.#token;
  }
}

/** The credential of the one browser session this page belongs to. */
export const sessionCredentials = new SessionCredentials();
