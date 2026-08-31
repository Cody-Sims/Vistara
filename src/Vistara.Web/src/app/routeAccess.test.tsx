import { render, screen } from '@testing-library/react';
import { RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../api/generated/client';
import type { CurrentUser } from '../api/platform';
import { SessionProvider } from '../features/session';
import { currentUser } from '../features/session/sessionTestData';
import { createAppRouter } from './router';

function anonymousClient() {
  return {
    getSession: vi.fn(async (): Promise<CurrentUser> => {
      throw new VistaraApiError(401, {
        type: 'about:blank',
        title: 'Unauthorized',
        status: 401,
        code: 'auth.unauthenticated',
        errors: {},
      });
    }),
    login: vi.fn(async () => ({ user: currentUser(), csrfToken: 'token-1' })),
    logout: vi.fn(async () => undefined),
  };
}

function renderRoute(path: string, client = anonymousClient()) {
  const router = createAppRouter({
    initialEntries: [path],
    liveFeatures: true,
    staticPreview: false,
  });

  render(
    <SessionProvider client={client}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );

  return router;
}

const privateRoutes = [
  '/library',
  '/library/recent',
  '/search?q=harbour',
  '/assets/asset-1',
  '/uploads',
  '/albums',
  '/albums/new',
  '/albums/album-1',
  '/tags',
  '/tags/tag-1',
  '/favorites',
  '/shared/links',
  '/trash',
  '/settings',
  '/admin/users',
  '/admin/storage',
  '/admin/jobs',
  '/admin/policies',
  '/admin/audit',
];

describe('route access', () => {
  for (const path of privateRoutes) {
    it(`sends an anonymous visitor from ${path} to sign in`, async () => {
      const router = renderRoute(path);

      expect(
        await screen.findByRole('heading', { name: 'Sign in' }),
      ).toBeInTheDocument();
      expect(router.state.location.pathname).toBe('/login');
      expect(router.state.location.search).toBe(
        `?returnTo=${encodeURIComponent(path)}`,
      );
    }, 30_000);
  }

  it('keeps the sign-in page reachable without a session', async () => {
    renderRoute('/login');

    expect(
      await screen.findByRole('heading', { name: 'Sign in' }),
    ).toBeInTheDocument();
  });

  it('keeps a public share reachable without a session', async () => {
    const client = anonymousClient();
    renderRoute('/s/public-token', client);

    expect(
      await screen.findByRole('heading', { name: /Shared|Loading|gallery/i }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Sign in' }),
    ).not.toBeInTheDocument();
  });
});
