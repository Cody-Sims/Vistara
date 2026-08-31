import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PlatformApiClient } from '../api/platform';
import { currentUser } from '../features/session/sessionTestData';
import { ApplicationProviders } from './ApplicationProviders';
import { createAppQueryClient } from '../api/queryClient';
import { createAppRouter } from './router';

function jsonResponse(body: unknown) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

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

function renderApplication(fetch: typeof globalThis.fetch, entry: string) {
  const client = new PlatformApiClient({ fetch });
  const queryClient = createAppQueryClient();
  const router = createAppRouter({
    initialEntries: [entry],
    liveFeatures: true,
    staticPreview: false,
  });

  render(
    <ApplicationProviders
      queryClient={queryClient}
      router={router}
      sessionClient={client}
    />,
  );

  return { client, queryClient, router };
}

afterEach(() => {
  sessionStorage.clear();
});

describe('expired session', () => {
  it('sends a private route to sign in once, keeping a safe destination', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser()))
      .mockResolvedValue(unauthorizedResponse());
    const { client, queryClient, router } = renderApplication(
      fetch,
      '/settings?tab=account',
    );

    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/settings'),
    );
    queryClient.setQueryData(['assets'], { owner: 'expired-account' });

    // Any later call finds the session gone.
    await client.listTenants().catch(() => undefined);

    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/login'),
    );
    expect(router.state.location.search).toBe(
      `?returnTo=${encodeURIComponent('/settings?tab=account')}`,
    );
    expect(
      await screen.findByRole('heading', { name: 'Sign in' }),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(queryClient.getQueryData(['assets'])).toBeUndefined(),
    );
  });

  it('transitions once however many calls fail', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser()))
      .mockResolvedValue(unauthorizedResponse());
    const cleared = vi.fn();
    const client = new PlatformApiClient({ fetch });
    const router = createAppRouter({
      initialEntries: ['/library'],
      liveFeatures: true,
      staticPreview: false,
    });

    render(
      <ApplicationProviders
        queryClient={new QueryClient()}
        router={router}
        sessionClient={client}
        onSessionEnd={cleared}
      />,
    );

    await waitFor(() => expect(router.state.location.pathname).toBe('/library'));

    await Promise.all([
      client.listTenants().catch(() => undefined),
      client.listApiKeys().catch(() => undefined),
      client.getStorageSummary().catch(() => undefined),
    ]);

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(cleared).toHaveBeenCalledTimes(1);
    expect(router.state.location.search).toBe(
      `?returnTo=${encodeURIComponent('/library')}`,
    );
  });

  it('leaves a public share where it is', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser()))
      .mockResolvedValue(unauthorizedResponse());
    const { client, router } = renderApplication(fetch, '/s/public-token');

    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/s/public-token'),
    );

    await client.listTenants().catch(() => undefined);

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(router.state.location.pathname).toBe('/s/public-token');
  });

  it('does not loop when the sign-in page itself sees a rejection', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(unauthorizedResponse());
    const { client, router } = renderApplication(fetch, '/login');

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    const before = router.state.location.search;

    await client.getSetupState().catch(() => undefined);
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(router.state.location.pathname).toBe('/login');
    expect(router.state.location.search).toBe(before);
  });

  it('refuses an unsafe destination handed to the sign-in page', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockImplementationOnce(async () => unauthorizedResponse())
      .mockImplementationOnce(async () =>
        jsonResponse({ user: currentUser(), csrfToken: 'token-1' }),
      )
      .mockImplementation(async () => jsonResponse(currentUser()));
    const user = userEvent.setup();
    const { router } = renderApplication(
      fetch,
      `/login?returnTo=${encodeURIComponent('https://evil.example/steal')}`,
    );

    await screen.findByRole('heading', { name: 'Sign in' });
    await user.click(screen.getByLabelText('Email address or user name'));
    await user.paste('ada@example.test');
    await user.click(screen.getByLabelText('Password'));
    await user.paste('correct horse');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/library'),
    );
    expect(router.state.location.search).toBe('');
    expect(
      fetch.mock.calls.map(([input]) => String(input)),
    ).not.toContain('https://evil.example/steal');
  });

  it('refuses a protocol-relative destination handed to the sign-in page', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockImplementationOnce(async () => unauthorizedResponse())
      .mockImplementationOnce(async () =>
        jsonResponse({ user: currentUser(), csrfToken: 'token-1' }),
      )
      .mockImplementation(async () => jsonResponse(currentUser()));
    const user = userEvent.setup();
    const { router } = renderApplication(
      fetch,
      `/login?returnTo=${encodeURIComponent('//evil.example/steal')}`,
    );

    await screen.findByRole('heading', { name: 'Sign in' });
    await user.click(screen.getByLabelText('Email address or user name'));
    await user.paste('ada@example.test');
    await user.click(screen.getByLabelText('Password'));
    await user.paste('correct horse');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/library'),
    );
    expect(router.state.location.pathname).toBe('/library');
  });
});
