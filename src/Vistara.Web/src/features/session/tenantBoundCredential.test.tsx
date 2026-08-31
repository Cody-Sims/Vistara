import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CurrentUser } from '../../api/platform';
import { createAppQueryClient } from '../../api/queryClient';
import { ApplicationProviders } from '../../app/ApplicationProviders';
import { PrimaryNavigation } from '../../app/navigation/PrimaryNavigation';
import { createAppRouter } from '../../app/router';
import { SessionProvider } from './SessionProvider';
import { useSession } from './sessionContext';
import { currentUser, tenantBoundUser } from './sessionTestData';

const imported = vi.hoisted(() => ({ count: 0 }));

vi.mock('../../app/routes/deferredScreens', async () => {
  imported.count += 1;
  return {
    AdminUsersScreen: () => <h1>People</h1>,
    AdminStorageScreen: () => <h1>Storage</h1>,
    AdminJobsScreen: () => <h1>Jobs</h1>,
    AdminPoliciesScreen: () => <h1>Policies</h1>,
    AdminAuditScreen: () => <h1>Audit log</h1>,
    SettingsScreen: () => <h1>Settings</h1>,
    SetupScreen: () => <h1>Set up Vistara</h1>,
  };
});

afterEach(() => {
  imported.count = 0;
});

function sessionClient(user: CurrentUser) {
  return {
    getSession: vi.fn(async () => user),
    login: vi.fn(),
    logout: vi.fn(async () => undefined),
  };
}

/** Renders the rail with a control that rereads the session on demand. */
function renderRefreshable(client: ReturnType<typeof sessionClient>) {
  function Screen() {
    const session = useSession();
    return (
      <>
        <button type="button" onClick={() => void session.reload()}>
          Reread session
        </button>
        <PrimaryNavigation variant="rail" />
      </>
    );
  }

  const router = createMemoryRouter([{ path: '*', element: <Screen /> }], {
    initialEntries: ['/library'],
  });

  render(
    <SessionProvider client={client}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );
}

function renderNavigation(user: CurrentUser) {
  const router = createMemoryRouter(
    [{ path: '*', element: <PrimaryNavigation /> }],
    { initialEntries: ['/library'] },
  );

  render(
    <SessionProvider client={sessionClient(user)}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );
}

function renderAt(entry: string, user: CurrentUser) {
  const router = createAppRouter({
    initialEntries: [entry],
    liveFeatures: true,
    staticPreview: false,
  });

  render(
    <ApplicationProviders
      queryClient={createAppQueryClient()}
      router={router}
      sessionClient={sessionClient(user) as never}
    />,
  );
}

describe('tenant-bound credential', () => {
  it('offers no administration navigation to an owner reported by an API key', async () => {
    renderNavigation(tenantBoundUser());

    expect(
      await screen.findAllByRole('button', { name: /Ada Lovelace/ }),
    ).not.toHaveLength(0);
    expect(
      screen.queryByRole('navigation', { name: 'Administration' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('link', { name: 'People' }),
    ).not.toBeInTheDocument();
  });

  it('offers no administration navigation to an owner holding a bearer token', async () => {
    renderNavigation(tenantBoundUser({ authenticationKind: 'bearer' }));

    expect(
      await screen.findAllByRole('button', { name: /Ada Lovelace/ }),
    ).not.toHaveLength(0);
    expect(
      screen.queryByRole('navigation', { name: 'Administration' }),
    ).not.toBeInTheDocument();
  });

  it('names the credential so the read-only context is not read as owner rights', async () => {
    renderNavigation(tenantBoundUser());

    const [account] = await screen.findAllByRole('button', {
      name: /Ada Lovelace/,
    });
    expect(account).toHaveTextContent(/API key/i);
  });

  it('refuses a session whose credential the deployment does not publish', async () => {
    renderNavigation(currentUser({ authenticationKind: undefined }, 'TenantOwner'));

    expect(
      await screen.findAllByRole('button', { name: /Ada Lovelace/ }),
    ).not.toHaveLength(0);
    expect(
      screen.queryByRole('navigation', { name: 'Administration' }),
    ).not.toBeInTheDocument();
  });

  it('explains the credential instead of opening the storage assistant', async () => {
    renderAt('/admin/storage', tenantBoundUser());

    expect(
      await screen.findByRole('heading', {
        name: 'Administration needs a signed-in session',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Test connection' }),
    ).not.toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(imported.count).toBe(0);
  });

  it('keeps the same owner administering from an interactive session', async () => {
    renderAt('/admin/storage', currentUser({}, 'TenantOwner'));

    expect(
      await screen.findByRole('heading', { name: 'Storage' }),
    ).toBeInTheDocument();
  });

  it('keeps a cookie owner administering while no antiforgery token is held', async () => {
    renderNavigation(currentUser({ csrfToken: undefined }, 'TenantOwner'));

    const administration = await screen.findByRole('navigation', {
      name: 'Administration',
    });
    expect(
      within(administration).getByRole('link', { name: 'Storage' }),
    ).toBeInTheDocument();
  });

  it('opens administration for a cookie owner with no antiforgery token yet', async () => {
    renderAt('/admin/storage', currentUser({ csrfToken: undefined }, 'TenantOwner'));

    expect(
      await screen.findByRole('heading', { name: 'Storage' }),
    ).toBeInTheDocument();
  });
});

describe('refreshed session state', () => {
  it('adopts the credential the session is reread with', async () => {
    const user = userEvent.setup();
    const client = sessionClient(tenantBoundUser());
    client.getSession
      .mockResolvedValueOnce(tenantBoundUser())
      .mockResolvedValue(currentUser({}, 'TenantOwner'));
    renderRefreshable(client);

    await screen.findAllByRole('button', { name: /Ada Lovelace/ });
    expect(
      screen.queryByRole('navigation', { name: 'Administration' }),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Reread session' }));

    expect(
      await screen.findByRole('navigation', { name: 'Administration' }),
    ).toBeInTheDocument();
  });

  it('withdraws administration when the session is reread as a key', async () => {
    const user = userEvent.setup();
    const client = sessionClient(currentUser({}, 'TenantOwner'));
    client.getSession
      .mockResolvedValueOnce(currentUser({}, 'TenantOwner'))
      .mockResolvedValue(tenantBoundUser());
    renderRefreshable(client);

    await screen.findByRole('navigation', { name: 'Administration' });

    await user.click(screen.getByRole('button', { name: 'Reread session' }));

    await waitFor(() =>
      expect(
        screen.queryByRole('navigation', { name: 'Administration' }),
      ).not.toBeInTheDocument(),
    );
  });
});

describe('administration scopes', () => {
  it('offers an administrator only the screens their scopes authorize', async () => {
    renderNavigation(currentUser({}, 'TenantAdmin'));

    const administration = await screen.findByRole('navigation', {
      name: 'Administration',
    });

    for (const name of ['People', 'Jobs', 'Audit log']) {
      expect(
        within(administration).getByRole('link', { name }),
      ).toBeInTheDocument();
    }
    for (const name of ['Storage', 'Policies']) {
      expect(
        within(administration).queryByRole('link', { name }),
      ).not.toBeInTheDocument();
    }
  });

  it('offers an owner every administration screen', async () => {
    renderNavigation(currentUser({}, 'TenantOwner'));

    const administration = await screen.findByRole('navigation', {
      name: 'Administration',
    });

    for (const name of ['People', 'Storage', 'Jobs', 'Policies', 'Audit log']) {
      expect(
        within(administration).getByRole('link', { name }),
      ).toBeInTheDocument();
    }
  });

  it('refuses an administrator the owner-only policies screen', async () => {
    renderAt('/admin/policies', currentUser({}, 'TenantAdmin'));

    expect(
      await screen.findByRole('heading', {
        name: 'Administration unavailable',
      }),
    ).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(imported.count).toBe(0);
  });
});
