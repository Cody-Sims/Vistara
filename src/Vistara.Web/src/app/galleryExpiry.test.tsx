import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PlatformApiClient } from '../api/platform';
import { createAppQueryClient } from '../api/queryClient';
import { currentUser } from '../features/session/sessionTestData';
import { galleryClient } from './apiClients';
import { ApplicationProviders } from './ApplicationProviders';
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

function renderApplication(
  entry: string,
  sessionFetch: typeof globalThis.fetch,
  onSessionEnd?: () => void,
) {
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
      sessionClient={new PlatformApiClient({ fetch: sessionFetch })}
      {...(onSessionEnd ? { onSessionEnd } : {})}
    />,
  );

  return { queryClient, router };
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('expired session seen by the gallery client', () => {
  it('ends the session once when timeline and asset reads are refused', async () => {
    const galleryFetch = vi
      .fn<typeof globalThis.fetch>()
      .mockImplementation(async () => unauthorizedResponse());
    vi.stubGlobal('fetch', galleryFetch);
    const { queryClient, router } = renderApplication('/library', async () =>
      jsonResponse(currentUser()),
    );

    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/library'),
    );
    queryClient.setQueryData(['assets'], { owner: 'expired-account' });

    await Promise.all([
      galleryClient.getTimeline().catch(() => undefined),
      galleryClient.listAssets().catch(() => undefined),
    ]);

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(router.state.location.search).toBe(
      `?returnTo=${encodeURIComponent('/library')}`,
    );
    expect(
      await screen.findByRole('heading', { name: 'Sign in' }),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(queryClient.getQueryData(['assets'])).toBeUndefined(),
    );
  });

  it('drops the account data once however many gallery reads fail', async () => {
    const galleryFetch = vi
      .fn<typeof globalThis.fetch>()
      .mockImplementation(async () => unauthorizedResponse());
    vi.stubGlobal('fetch', galleryFetch);
    const cleared = vi.fn();
    const { router } = renderApplication(
      '/library',
      async () => jsonResponse(currentUser()),
      cleared,
    );

    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/library'),
    );

    await Promise.all([
      galleryClient.getTimeline().catch(() => undefined),
      galleryClient.listAssets().catch(() => undefined),
      galleryClient.listAlbums().catch(() => undefined),
    ]);

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(cleared).toHaveBeenCalledTimes(1);
  });

  it('leaves a public share alone when its link is refused', async () => {
    const galleryFetch = vi
      .fn<typeof globalThis.fetch>()
      .mockImplementation(async () => unauthorizedResponse());
    vi.stubGlobal('fetch', galleryFetch);
    const cleared = vi.fn();
    const { router } = renderApplication(
      '/s/public-token',
      async () => jsonResponse(currentUser()),
      cleared,
    );

    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/s/public-token'),
    );

    await galleryClient.getPublicShare('public-token').catch(() => undefined);
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(router.state.location.pathname).toBe('/s/public-token');
    expect(cleared).not.toHaveBeenCalled();
  });
});
