import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PlatformApiClient } from '../api/platform';
import { createAppQueryClient } from '../api/queryClient';
import { ApplicationProviders } from './ApplicationProviders';
import { createAppRouter } from './router';

function unauthorizedResponse() {
  return new Response(
    JSON.stringify({
      type: 'about:blank',
      title: 'auth.unauthenticated',
      status: 401,
      code: 'auth.unauthenticated',
      errors: {},
    }),
    { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('cold load of a deferred top-level route', () => {
  it('announces loading immediately and warns about nothing', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockImplementation(async () => unauthorizedResponse());

    render(
      <ApplicationProviders
        queryClient={createAppQueryClient()}
        router={createAppRouter({
          initialEntries: ['/setup'],
          liveFeatures: true,
          staticPreview: false,
        })}
        sessionClient={new PlatformApiClient({ fetch })}
      />,
    );

    // The very first paint, before the deferred module resolves.
    const loading = screen.getByRole('status');
    expect(loading).toBeVisible();
    expect(loading).toHaveTextContent('Loading Vistara');
    expect(loading).toHaveAttribute('aria-live', 'polite');

    expect(
      await screen.findByRole('heading', { name: 'Set up Vistara' }),
    ).toBeInTheDocument();

    const messages = [...warn.mock.calls, ...error.mock.calls]
      .map((call) => call.map(String).join(' '))
      .join('\n');
    expect(messages).not.toMatch(/hydratefallback/i);
    expect(messages).toBe('');
  });
});
