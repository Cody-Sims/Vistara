import { describe, expect, it } from 'vitest';
import {
  forgetHostedProvider,
  hostedProviderKey,
  hostedStartUrl,
  providerSignOutUrl,
  readHostedProvider,
  rememberHostedProvider,
} from './hostedSignIn';

function storage() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => void values.set(key, value),
    removeItem: (key: string) => void values.delete(key),
    values,
  };
}

describe('hosted sign-in navigation', () => {
  it('starts sign-in at the same-origin path the deployment published', () => {
    expect(hostedStartUrl('/api/v1/auth/oidc/entra/start', '/albums')).toBe(
      '/api/v1/auth/oidc/entra/start?returnTo=%2Falbums',
    );
  });

  it('starts sign-in without a return target when none was asked for', () => {
    expect(hostedStartUrl('/api/v1/auth/oidc/entra/start')).toBe(
      '/api/v1/auth/oidc/entra/start',
    );
  });

  /**
   * A start URL is published by the deployment, but it reaches this page over
   * the network, so a value that would leave this origin is not navigated to.
   */
  it.each([
    'https://attacker.example/start',
    '//attacker.example/start',
    'javascript:alert(1)',
    'api/v1/auth/oidc/entra/start',
    '/api/v1/auth/oidc/entra/start?returnTo=https://attacker.example',
  ])('refuses %s as a start URL', (candidate) => {
    expect(hostedStartUrl(candidate, '/albums')).toBeUndefined();
  });

  /** The return target comes from this page's own URL and is never absolute. */
  it.each(['https://attacker.example/steal', '//attacker.example', 'albums'])(
    'drops %s as a return target instead of sending it',
    (candidate) => {
      expect(hostedStartUrl('/api/v1/auth/oidc/entra/start', candidate)).toBe(
        '/api/v1/auth/oidc/entra/start',
      );
    },
  );

  it('remembers only a provider key, and forgets it on request', () => {
    const store = storage();

    rememberHostedProvider('entra', store);

    expect(store.values.get(hostedProviderKey)).toBe('entra');
    expect(readHostedProvider(store)).toBe('entra');

    forgetHostedProvider(store);
    expect(readHostedProvider(store)).toBeUndefined();
  });

  it('never records a provider key that is not one', () => {
    const store = storage();

    rememberHostedProvider('../../token', store);

    expect(store.values.size).toBe(0);
    expect(readHostedProvider(store)).toBeUndefined();
  });

  it('reads nothing back from a tampered store', () => {
    const store = storage();
    store.setItem(hostedProviderKey, 'eyJhbGciOiJIUzI1NiJ9.payload.signature');

    expect(readHostedProvider(store)).toBeUndefined();
  });
});

describe('provider sign-out targets', () => {
  it('follows the absolute provider URL the API answered', () => {
    expect(
      providerSignOutUrl('https://login.example.test/logout?client_id=v'),
    ).toBe('https://login.example.test/logout?client_id=v');
  });

  it.each([
    'javascript:alert(1)',
    'data:text/html,<script></script>',
    '/logout',
    '',
    null,
    undefined,
  ])('refuses %s as a provider sign-out target', (candidate) => {
    expect(providerSignOutUrl(candidate)).toBeUndefined();
  });
});
