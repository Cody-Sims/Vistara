import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  createMemoryRouter,
  Link,
  RouterProvider,
  useLocation,
} from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { CurrentUser } from '../../api/platform';
import {
  RequireAdministration,
  RequireSession,
  SessionProvider,
  useSession,
} from './index';
import { currentUser } from './sessionTestData';

function apiError(status: number) {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: 'Failed',
    status,
    code: 'failed',
    errors: {},
  });
}

interface SessionClientDouble {
  getSession: () => Promise<CurrentUser>;
  login: (request: {
    login: string;
    password: string;
  }) => Promise<{ user: CurrentUser; csrfToken: string }>;
  logout: () => Promise<void>;
}

function fakeClient(
  overrides: Partial<SessionClientDouble> = {},
): SessionClientDouble & { logout: ReturnType<typeof vi.fn> } {
  return {
    getSession: vi.fn(async () => currentUser()),
    login: vi.fn(async () => ({ user: currentUser(), csrfToken: 'token-1' })),
    logout: vi.fn(async () => undefined),
    ...overrides,
  } as SessionClientDouble & { logout: ReturnType<typeof vi.fn> };
}

function SessionProbe() {
  const session = useSession();
  return (
    <div>
      <p data-testid="status">{session.status}</p>
      <p data-testid="name">{session.user?.displayName ?? 'none'}</p>
      <button type="button" onClick={() => void session.signOut()}>
        Sign out
      </button>
      <button type="button" onClick={() => void session.reload()}>
        Try again
      </button>
    </div>
  );
}

function LoginProbe() {
  const location = useLocation();
  return (
    <div>
      <h1>Sign in</h1>
      <p data-testid="search">{location.search}</p>
    </div>
  );
}

function renderWithSession(
  ui: React.ReactNode,
  client: SessionClientDouble,
  initialEntries = ['/settings'],
  onSessionEnd?: () => void | Promise<void>,
) {
  const router = createMemoryRouter(
    [
      { path: '/login', element: <LoginProbe /> },
      { path: '*', element: ui },
    ],
    { initialEntries },
  );

  return render(
    <SessionProvider client={client} onSessionEnd={onSessionEnd}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );
}

describe('session provider', () => {
  it('resolves the signed-in account from the session route', async () => {
    renderWithSession(<SessionProbe />, fakeClient());

    expect(screen.getByTestId('status')).toHaveTextContent('loading');
    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated'),
    );
    expect(screen.getByTestId('name')).toHaveTextContent('Ada Lovelace');
  });

  it('treats an unauthenticated session as anonymous rather than an error', async () => {
    renderWithSession(
      <SessionProbe />,
      fakeClient({
        getSession: vi.fn(async () => {
          throw apiError(401);
        }),
      }),
    );

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('anonymous'),
    );
  });

  it('reports an unavailable session and recovers when retried', async () => {
    const getSession = vi
      .fn<() => Promise<CurrentUser>>()
      .mockRejectedValueOnce(apiError(503))
      .mockResolvedValueOnce(currentUser());
    const user = userEvent.setup();

    renderWithSession(<SessionProbe />, fakeClient({ getSession }));

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('error'),
    );

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated'),
    );
  });

  it('returns to an anonymous session after signing out', async () => {
    const client = fakeClient();
    const user = userEvent.setup();

    renderWithSession(<SessionProbe />, client);
    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated'),
    );

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('anonymous'),
    );
    expect(client.logout).toHaveBeenCalledTimes(1);
  });

  it('clears account data even when the sign-out request fails', async () => {
    const cleared = vi.fn();
    const user = userEvent.setup();

    renderWithSession(
      <SessionProbe />,
      fakeClient({
        logout: vi.fn(async () => {
          throw apiError(503);
        }),
      }),
      ['/settings'],
      cleared,
    );
    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated'),
    );

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('anonymous'),
    );
    expect(cleared).toHaveBeenCalledTimes(1);
  });
});

describe('session guards', () => {
  it('sends anonymous visitors to sign in and remembers the destination', async () => {
    renderWithSession(
      <RequireSession>
        <h1>Settings</h1>
      </RequireSession>,
      fakeClient({
        getSession: vi.fn(async () => {
          throw apiError(401);
        }),
      }),
      ['/settings?tab=account'],
    );

    expect(
      await screen.findByRole('heading', { name: 'Sign in' }),
    ).toBeInTheDocument();
    expect(screen.getByTestId('search')).toHaveTextContent(
      'returnTo=%2Fsettings%3Ftab%3Daccount',
    );
  });

  it('shows an accessible pending state while the session resolves', () => {
    renderWithSession(
      <RequireSession>
        <h1>Settings</h1>
      </RequireSession>,
      fakeClient({
        getSession: vi.fn(() => new Promise<CurrentUser>(() => {})),
      }),
    );

    expect(screen.getByRole('status')).toHaveTextContent(
      'Checking your session',
    );
  });

  it('offers a retry when the session cannot be checked', async () => {
    const getSession = vi
      .fn<() => Promise<CurrentUser>>()
      .mockRejectedValueOnce(apiError(500))
      .mockResolvedValueOnce(currentUser());
    const user = userEvent.setup();

    renderWithSession(
      <RequireSession>
        <h1>Settings</h1>
      </RequireSession>,
      fakeClient({ getSession }),
    );

    expect(
      await screen.findByRole('heading', { name: 'Session unavailable' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(
      await screen.findByRole('heading', { name: 'Settings' }),
    ).toBeInTheDocument();
  });

  it('keeps administration away from members without hiding the way back', async () => {
    renderWithSession(
      <RequireAdministration>
        <h1>People</h1>
      </RequireAdministration>,
      fakeClient(),
      ['/admin/users'],
    );

    expect(
      await screen.findByRole('heading', { name: 'Administration unavailable' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Return to library' }),
    ).toHaveAttribute('href', '/library');
  });

  it('admits an administrator of the active tenant', async () => {
    renderWithSession(
      <RequireAdministration>
        <h1>People</h1>
      </RequireAdministration>,
      fakeClient({
        getSession: vi.fn(async () => currentUser({}, 'TenantAdmin')),
      }),
      ['/admin/users'],
    );

    expect(
      await screen.findByRole('heading', { name: 'People' }),
    ).toBeInTheDocument();
  });

  it('refuses administration borrowed from another tenant', async () => {
    renderWithSession(
      <RequireAdministration>
        <h1>People</h1>
      </RequireAdministration>,
      fakeClient({
        getSession: vi.fn(async () =>
          currentUser({
            tenantId: 'tenant-b',
            role: 'Member',
            tenants: [
              {
                id: 'tenant-a',
                slug: 'studio',
                name: 'Studio',
                role: 'TenantOwner',
                membershipStatus: 'Active',
              },
              {
                id: 'tenant-b',
                slug: 'annex',
                name: 'Annex',
                role: 'Member',
                membershipStatus: 'Active',
              },
            ],
          }),
        ),
      }),
      ['/admin/users'],
    );

    expect(
      await screen.findByRole('heading', { name: 'Administration unavailable' }),
    ).toBeInTheDocument();
  });

  it('renders routes without a session check in the static preview', async () => {
    const client = fakeClient();

    const router = createMemoryRouter(
      [
        {
          path: '*',
          element: (
            <RequireSession>
              <h1>Settings</h1>
              <Link to="/library">Return to library</Link>
            </RequireSession>
          ),
        },
      ],
      { initialEntries: ['/settings'] },
    );
    render(
      <SessionProvider client={client} mode="preview">
        <RouterProvider router={router} />
      </SessionProvider>,
    );

    expect(
      await screen.findByRole('heading', { name: 'Settings' }),
    ).toBeInTheDocument();
    expect(client.getSession).not.toHaveBeenCalled();
  });
});
