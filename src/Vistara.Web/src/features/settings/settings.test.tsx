import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { SessionSnapshot } from '../../api/platform';
import { SessionProvider } from '../session';
import { SettingsPage } from './SettingsPage';

const session: SessionSnapshot = {
  user: {
    id: 'user-1',
    displayName: 'Ada Lovelace',
    email: 'ada@example.test',
    platformAdmin: false,
  },
  memberships: [
    {
      tenantId: 'tenant-1',
      tenantName: 'Studio',
      role: 'TenantAdmin',
      status: 'active',
    },
  ],
  activeTenantId: 'tenant-1',
  preferences: { theme: 'system', density: 'comfortable', locale: 'en-US' },
};

function apiError(status: number) {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: 'Failed',
    status,
    code: 'failed',
    errors: {},
  });
}

afterEach(() => {
  localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
});

function renderSettings(
  updatePreferences: () => Promise<SessionSnapshot> = async () => session,
) {
  const client = {
    getSession: vi.fn(async () => session),
    login: vi.fn(async () => session),
    logout: vi.fn(async () => undefined),
    updatePreferences: vi.fn(updatePreferences),
  };
  const router = createMemoryRouter([{ path: '*', element: <SettingsPage /> }], {
    initialEntries: ['/settings'],
  });

  render(
    <SessionProvider client={client}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );

  return client;
}

describe('settings', () => {
  it('describes the signed-in account and workspace role', async () => {
    renderSettings();

    expect(
      await screen.findByRole('heading', { name: 'Settings' }),
    ).toBeInTheDocument();
    expect(screen.getByText('ada@example.test')).toBeInTheDocument();
    expect(screen.getByText('Studio')).toBeInTheDocument();
    expect(screen.getByText('Administrator')).toBeInTheDocument();
  });

  it('applies and remembers the chosen appearance', async () => {
    const user = userEvent.setup();
    renderSettings();

    await user.click(await screen.findByRole('radio', { name: 'Light' }));

    expect(document.documentElement).toHaveAttribute('data-theme', 'light');
    expect(localStorage.getItem('vistara:theme')).toBe('light');
    expect(screen.getByRole('radio', { name: 'Light' })).toBeChecked();
  });

  it('saves reading preferences and confirms the result', async () => {
    const user = userEvent.setup();
    const client = renderSettings();

    await user.click(await screen.findByRole('radio', { name: 'Compact' }));
    await user.click(screen.getByLabelText('Reduce motion'));
    await user.click(screen.getByRole('button', { name: 'Save preferences' }));

    await waitFor(() =>
      expect(client.updatePreferences).toHaveBeenCalledWith({
        density: 'compact',
        locale: 'en-US',
        reducedMotion: true,
        screenReaderPagedMode: false,
      }),
    );
    expect(
      await screen.findByText('Preferences saved.'),
    ).toBeInTheDocument();
  });

  it('keeps the edits and offers another attempt when saving fails', async () => {
    const user = userEvent.setup();
    renderSettings(async () => {
      throw apiError(503);
    });

    await user.click(await screen.findByRole('radio', { name: 'Compact' }));
    await user.click(screen.getByRole('button', { name: 'Save preferences' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Preferences could not be saved',
    );
    expect(screen.getByRole('radio', { name: 'Compact' })).toBeChecked();
    expect(
      screen.getByRole('button', { name: 'Save preferences' }),
    ).toBeEnabled();
  });

  it('explains a conflicting update from another device', async () => {
    const user = userEvent.setup();
    renderSettings(async () => {
      throw apiError(409);
    });

    await user.click(await screen.findByRole('radio', { name: 'Compact' }));
    await user.click(screen.getByRole('button', { name: 'Save preferences' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'changed somewhere else',
    );
    expect(
      screen.getByRole('button', { name: 'Reload preferences' }),
    ).toBeInTheDocument();
  });

  it('signs out from the account section', async () => {
    const user = userEvent.setup();
    const client = renderSettings();

    await user.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(client.logout).toHaveBeenCalledTimes(1);
  });
});
