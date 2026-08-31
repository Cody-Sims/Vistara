import { describe, expect, it, vi } from 'vitest';
import { SessionCredentials } from './credentials';

describe('session credentials held for one browser session', () => {
  it('names the default antiforgery header until a session publishes one', () => {
    const credentials = new SessionCredentials();

    expect(credentials.headerName).toBe('X-Vistara-CSRF');
    expect(credentials.carriesToken).toBe(false);
  });

  it('adopts the header name, token, and credential the session published', () => {
    const credentials = new SessionCredentials();

    credentials.adopt({
      csrfHeaderName: 'X-Deployment-CSRF',
      csrfToken: 'token-1',
      authenticationKind: 'cookie',
    });

    const headers = new Headers();
    expect(credentials.applyTo(headers)).toBe('token-1');
    expect(headers.get('X-Deployment-CSRF')).toBe('token-1');
    expect(headers.get('X-Vistara-CSRF')).toBeNull();
  });

  it('refuses a header name that is not a token, keeping the published one', () => {
    const credentials = new SessionCredentials();

    credentials.adopt({
      csrfHeaderName: 'not a header: value',
      csrfToken: 'token-1',
      authenticationKind: 'cookie',
    });

    const headers = new Headers();
    credentials.applyTo(headers);
    expect(headers.get('X-Vistara-CSRF')).toBe('token-1');
  });

  it('spends no token for a credential the API issues none to', () => {
    const credentials = new SessionCredentials();
    credentials.adopt({ csrfToken: 'token-1', authenticationKind: 'cookie' });

    credentials.adopt({
      csrfHeaderName: 'X-Vistara-CSRF',
      authenticationKind: 'apiKey',
    });

    const headers = new Headers();
    expect(credentials.applyTo(headers)).toBeUndefined();
    expect(credentials.carriesToken).toBe(false);
    expect([...headers.keys()]).toHaveLength(0);
  });

  it('keeps the token a caller set rather than replacing it', () => {
    const credentials = new SessionCredentials();
    credentials.adopt({ csrfToken: 'token-1', authenticationKind: 'cookie' });

    const headers = new Headers({ 'X-Vistara-CSRF': 'caller-token' });
    expect(credentials.applyTo(headers)).toBe('caller-token');
    expect(headers.get('X-Vistara-CSRF')).toBe('caller-token');
  });

  it('leaves every other header the caller set untouched', () => {
    const credentials = new SessionCredentials();
    credentials.adopt({ csrfToken: 'token-1', authenticationKind: 'cookie' });

    const headers = new Headers({
      'Idempotency-Key': 'key-1',
      'If-Match': '"v1"',
    });
    credentials.applyTo(headers);

    expect(headers.get('Idempotency-Key')).toBe('key-1');
    expect(headers.get('If-Match')).toBe('"v1"');
    expect(headers.get('X-Vistara-CSRF')).toBe('token-1');
  });

  it('forgets the token when the session ends', () => {
    const credentials = new SessionCredentials();
    credentials.adopt({
      csrfHeaderName: 'X-Deployment-CSRF',
      csrfToken: 'token-1',
      authenticationKind: 'cookie',
    });

    credentials.clear();

    const headers = new Headers();
    expect(credentials.applyTo(headers)).toBeUndefined();
    expect(credentials.carriesToken).toBe(false);
    // The header name describes the deployment, not the account.
    expect(credentials.headerName).toBe('X-Deployment-CSRF');
  });

  it('reads the session once when concurrent requests start without a token', async () => {
    const credentials = new SessionCredentials();
    const refresh = vi.fn(async () => {
      await Promise.resolve();
      credentials.adopt({ csrfToken: 'token-1', authenticationKind: 'cookie' });
    });
    credentials.useRefresher(refresh);

    await Promise.all([
      credentials.ensure(),
      credentials.ensure(),
      credentials.ensure(),
    ]);

    expect(refresh).toHaveBeenCalledTimes(1);
    expect(credentials.carriesToken).toBe(true);
  });

  it('reads no session when a token is already held', async () => {
    const credentials = new SessionCredentials();
    const refresh = vi.fn(async () => undefined);
    credentials.useRefresher(refresh);
    credentials.adopt({ csrfToken: 'token-1', authenticationKind: 'cookie' });

    await credentials.ensure();

    expect(refresh).not.toHaveBeenCalled();
  });

  it('reads no session for a credential that is issued no token', async () => {
    const credentials = new SessionCredentials();
    const refresh = vi.fn(async () => undefined);
    credentials.useRefresher(refresh);
    credentials.adopt({ authenticationKind: 'bearer' });

    await credentials.ensure();

    expect(refresh).not.toHaveBeenCalled();
  });

  it('replaces a rotated token when the session is read again', async () => {
    const credentials = new SessionCredentials();
    credentials.adopt({ csrfToken: 'stale', authenticationKind: 'cookie' });
    credentials.useRefresher(async () => {
      credentials.adopt({ csrfToken: 'rotated', authenticationKind: 'cookie' });
    });

    await credentials.refresh();

    const headers = new Headers();
    expect(credentials.applyTo(headers)).toBe('rotated');
  });

  it('survives a session read that fails without holding a token', async () => {
    const credentials = new SessionCredentials();
    credentials.useRefresher(async () => {
      throw new Error('offline');
    });

    await expect(credentials.ensure()).resolves.toBeUndefined();
    expect(credentials.carriesToken).toBe(false);
  });

  it('never writes the token to browser storage', () => {
    const credentials = new SessionCredentials();
    const setItem = vi.spyOn(Storage.prototype, 'setItem');

    credentials.adopt({ csrfToken: 'token-1', authenticationKind: 'cookie' });
    credentials.applyTo(new Headers());

    expect(setItem).not.toHaveBeenCalled();
    expect(
      JSON.stringify({ ...credentials, ...JSON.parse(JSON.stringify(credentials)) }),
    ).not.toContain('token-1');
  });
});
