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
import type { SessionSnapshot, TenantRole } from '../../api/platform';
import {
  RequireAdministration,
  RequireSession,
  SessionProvider,
  useSession,
} from './index';

function snapshot(role: TenantRole, platformAdmin = false): SessionSnapshot {
  return {
    user: {
      id: 'user-1',
      displayName: 'Ada Lovelace',
      email: 'ada@example.test',
      platformAdmin,
    },
    memberships: [
      {
        tenantId: 'tenant-1',
        tenantName: 'Studio',
        role,
        status: 'active',
      },
    ],
    activeTenantId: 'tenant-1',
    preferences: { theme: 'system' },
    antiforgeryToken: 'token-1',
  };
}

function apiError(status: number) {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: 'Failed',
    status,
    code: 'failed',
    errors: {},
  });
}

function fakeClient(overrides: Partial<SessionClientDouble> = {}) {
  return {
    getSession: vi.fn(async () => snapshot('Member')),
    login: vi.fn(async () => snapshot('Member')),
    logout: vi.fn(async () => undefined),
    updatePreferences: vi.fn(async () => snapshot('Member')),
    ...overrides,
  };
}

interface SessionClientDouble {
  getSession: () => Promise<SessionSnapshot>;
  login: (request: {
    email: string;
    password: string;
  }) => Promise<SessionSnapshot>;
  logout: () => Promise<void>;
  updatePreferences: (request: object) => Promise<SessionSnapshot>;
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

function renderWithSession(
  ui: React.ReactNode,
  client: SessionClientDouble,
  initialEntries = ['/settings'],
) {
  const router = createMemoryRouter(
    [
      { path: '/login', element: <LoginProbe /> },
      { path: '*', element: ui },
    ],
    { initialEntries },
  );

  return render(
    <SessionProvider client={client}>
      <RouterProvider router={router} />
    </SessionProvider>,
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

describe('session provider', () => {
  it('resolves the signed-in account from the session route', async () => {
    const client = fakeClient();

    renderWithSession(<SessionProbe />, client);

    expect(screen.getByTestId('status')).toHaveTextContent('loading');
    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated'),
    );
    expect(screen.getByTestId('name')).toHaveTextContent('Ada Lovelace');
  });

  it('treats an unauthenticated session as anonymous rather than an error', async () => {
    const client = fakeClient({
      getSession: vi.fn(async () => {
        throw apiError(401);
      }),
    });

    renderWithSession(<SessionProbe />, client);

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('anonymous'),
    );
  });

  it('reports an unavailable session and recovers when retried', async () => {
    const getSession = vi
      .fn<() => Promise<SessionSnapshot>>()
      .mockRejectedValueOnce(apiError(503))
      .mockResolvedValueOnce(snapshot('Member'));
    const client = fakeClient({ getSession });
    const user = userEvent.setup();

    renderWithSession(<SessionProbe />, client);

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
});

describe('session guards', () => {
  it('sends anonymous visitors to sign in and remembers the destination', async () => {
    const client = fakeClient({
      getSession: vi.fn(async () => {
        throw apiError(401);
      }),
    });

    renderWithSession(
      <RequireSession>
        <h1>Settings</h1>
      </RequireSession>,
      client,
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
    const client = fakeClient({
      getSession: vi.fn(() => new Promise<SessionSnapshot>(() => {})),
    });

    renderWithSession(
      <RequireSession>
        <h1>Settings</h1>
      </RequireSession>,
      client,
    );

    expect(screen.getByRole('status')).toHaveTextContent(
      'Checking your session',
    );
  });

  it('offers a retry when the session cannot be checked', async () => {
    const getSession = vi
      .fn<() => Promise<SessionSnapshot>>()
      .mockRejectedValueOnce(apiError(500))
      .mockResolvedValueOnce(snapshot('Member'));
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

  it('admits tenant administrators and platform administrators', async () => {
    renderWithSession(
      <RequireAdministration>
        <h1>People</h1>
      </RequireAdministration>,
      fakeClient({ getSession: vi.fn(async () => snapshot('TenantAdmin')) }),
      ['/admin/users'],
    );

    expect(
      await screen.findByRole('heading', { name: 'People' }),
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
