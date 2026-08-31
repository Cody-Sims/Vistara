import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createAppQueryClient } from '../api/queryClient';
import type { CurrentUser } from '../api/platform';
import { currentUser } from '../features/session/sessionTestData';
import { ApplicationProviders } from './ApplicationProviders';
import { createAppRouter } from './router';

const imported = vi.hoisted(() => ({ count: 0 }));

vi.mock('./routes/deferredScreens', async () => {
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

function renderAt(entry: string, user: CurrentUser) {
  const client = {
    getSession: vi.fn(async () => user),
    login: vi.fn(),
    logout: vi.fn(async () => undefined),
  };
  const router = createAppRouter({
    initialEntries: [entry],
    liveFeatures: true,
    staticPreview: false,
  });

  render(
    <ApplicationProviders
      queryClient={createAppQueryClient()}
      router={router}
      sessionClient={client as never}
    />,
  );

  return router;
}

afterEach(() => {
  imported.count = 0;
});

describe('operator bundle', () => {
  it('is never fetched for an account that does not administer', async () => {
    renderAt('/admin/users', currentUser({}, 'Member'));

    expect(
      await screen.findByRole('heading', {
        name: 'Administration unavailable',
      }),
    ).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(imported.count).toBe(0);
  });

  it('is fetched once an administrator opens a screen', async () => {
    renderAt('/admin/users', currentUser({}, 'TenantOwner'));

    expect(
      await screen.findByRole('heading', { name: 'People' }),
    ).toBeInTheDocument();
    await waitFor(() => expect(imported.count).toBe(1));
  });
});
