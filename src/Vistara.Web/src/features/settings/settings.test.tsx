import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type {
  ApiKeyCollection,
  CreatedApiKey,
  TenantCollection,
  UpdateUserPreferencesRequest,
  UserPreferences,
} from '../../api/platform';
import { resetPreferences } from '../../app/preferences';
import { SessionProvider } from '../session';
import { currentUser } from '../session/sessionTestData';
import { SettingsPage } from './SettingsPage';

function apiError(status: number) {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: 'Failed',
    status,
    code: 'failed',
    errors: {},
  });
}

const tenants: TenantCollection = {
  items: [
    {
      id: 'tenant-a',
      slug: 'studio',
      name: 'Studio',
      status: 'Active',
      role: 'TenantAdmin',
      membershipStatus: 'Active',
      joinedAt: '2026-01-01T00:00:00Z',
    },
    {
      id: 'tenant-b',
      slug: 'annex',
      name: 'Annex',
      status: 'Active',
      role: 'Viewer',
      membershipStatus: 'Active',
    },
  ],
};

const keys: ApiKeyCollection = {
  items: [
    {
      id: 'key-1',
      prefix: 'vst_abc',
      ownerId: 'user-1',
      scopes: ['assets.read'],
      status: 'Active',
      createdAt: '2026-01-01T00:00:00Z',
    },
  ],
};

const created: CreatedApiKey = {
  key: {
    id: 'key-2',
    prefix: 'vst_def',
    ownerId: 'user-1',
    scopes: ['assets.read'],
    status: 'Active',
    createdAt: '2026-02-01T00:00:00Z',
  },
  secret: 'vst_def_secret-value',
};

afterEach(() => {
  resetPreferences();
  localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
  for (const attribute of ['density', 'reducedMotion', 'pagedMode']) {
    delete document.documentElement.dataset[attribute];
  }
});

const preferences: UserPreferences = {
  density: 'comfortable',
  reducedMotion: false,
  screenReaderPagedMode: false,
  version: 3,
};

type VersionedPreferences = { data: UserPreferences; etag?: string };

/**
 * Answers `PATCH` the way the API does: the accepted patch is part of the
 * document that comes back, and the version moves on.
 */
function preferenceDocument(initial: UserPreferences = preferences) {
  let document = initial;

  return {
    read: async (): Promise<VersionedPreferences> => ({
      data: document,
      etag: `"v${document.version}"`,
    }),
    write: async (
      patch: UpdateUserPreferencesRequest,
    ): Promise<VersionedPreferences> => {
      document = {
        ...document,
        ...(patch.density ? { density: patch.density } : {}),
        ...(patch.reducedMotion === undefined
          ? {}
          : { reducedMotion: patch.reducedMotion }),
        ...(patch.screenReaderPagedMode === undefined
          ? {}
          : { screenReaderPagedMode: patch.screenReaderPagedMode }),
        version: document.version + 1,
      };
      return { data: document, etag: `"v${document.version}"` };
    },
  };
}

function renderSettings(
  overrides: {
    listTenants?: () => Promise<TenantCollection>;
    listApiKeys?: () => Promise<ApiKeyCollection>;
    createApiKey?: () => Promise<CreatedApiKey>;
    revokeApiKey?: () => Promise<void>;
    getPreferences?: () => Promise<VersionedPreferences>;
    updatePreferences?: (
      patch: UpdateUserPreferencesRequest,
      options: { ifMatch: string },
    ) => Promise<VersionedPreferences>;
    role?: 'Member' | 'TenantAdmin';
  } = {},
) {
  const account = preferenceDocument();
  const client = {
    getSession: vi.fn(async () =>
      currentUser({}, overrides.role ?? 'TenantAdmin'),
    ),
    login: vi.fn(),
    logout: vi.fn(async () => undefined),
    listTenants: vi.fn(overrides.listTenants ?? (async () => tenants)),
    listApiKeys: vi.fn(overrides.listApiKeys ?? (async () => keys)),
    createApiKey: vi.fn(overrides.createApiKey ?? (async () => created)),
    revokeApiKey: vi.fn(overrides.revokeApiKey ?? (async () => undefined)),
    getPreferences: vi.fn(overrides.getPreferences ?? account.read),
    updatePreferences: vi.fn(overrides.updatePreferences ?? account.write),
  };
  const router = createMemoryRouter(
    [{ path: '*', element: <SettingsPage client={client} /> }],
    { initialEntries: ['/settings'] },
  );

  render(
    <SessionProvider client={client}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );

  return client;
}

describe('settings: account', () => {
  it('describes the signed-in account and its workspace role', async () => {
    renderSettings();

    expect(
      await screen.findByRole('heading', { name: 'Settings' }),
    ).toBeInTheDocument();
    expect(screen.getByText('ada@example.test')).toBeInTheDocument();
    expect(screen.getByText('Administrator')).toBeInTheDocument();
  });

  it('signs out from the account section', async () => {
    const user = userEvent.setup();
    const client = renderSettings();

    await user.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(client.logout).toHaveBeenCalledTimes(1);
  });

  it('lists the workspaces the account belongs to', async () => {
    renderSettings();

    const workspaces = await screen.findByRole('list', { name: 'Workspaces' });
    expect(within(workspaces).getByText('Studio')).toBeInTheDocument();
    expect(within(workspaces).getByText('Annex')).toBeInTheDocument();
    expect(within(workspaces).getByText('Signed in here')).toBeInTheDocument();
  });
});

describe('settings: device preferences', () => {
  it('applies and remembers the chosen appearance', async () => {
    const user = userEvent.setup();
    renderSettings();

    await user.click(await screen.findByRole('radio', { name: 'Light' }));

    expect(document.documentElement).toHaveAttribute('data-theme', 'light');
    expect(localStorage.getItem('vistara:theme')).toBe('light');
  });

  it('applies density to the document immediately', async () => {
    const user = userEvent.setup();
    renderSettings();

    await user.click(await screen.findByRole('radio', { name: 'Compact' }));

    expect(document.documentElement.dataset.density).toBe('compact');
    expect(localStorage.getItem('vistara:preferences')).toContain('compact');
  });

  it('applies reduced motion and paged reading mode to the document', async () => {
    const user = userEvent.setup();
    renderSettings();

    await user.click(await screen.findByLabelText('Reduce motion'));
    await user.click(screen.getByLabelText('Paged library and search'));

    expect(document.documentElement.dataset.reducedMotion).toBe('true');
    expect(document.documentElement.dataset.pagedMode).toBe('true');
  });

  it('saves a reading preference to the account with its version', async () => {
    const user = userEvent.setup();
    const client = renderSettings();

    await user.click(await screen.findByRole('radio', { name: 'Compact' }));

    await waitFor(() =>
      expect(client.updatePreferences).toHaveBeenCalledWith(
        { density: 'compact' },
        { ifMatch: '"v3"' },
      ),
    );
    expect(
      await screen.findByText('Preferences saved to your account.'),
    ).toBeInTheDocument();
    expect(document.documentElement.dataset.density).toBe('compact');
  });

  it('keeps the device applying a preference the account could not store', async () => {
    const user = userEvent.setup();
    renderSettings({
      updatePreferences: async () => {
        throw apiError(412);
      },
    });

    await user.click(await screen.findByRole('radio', { name: 'Compact' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'changed on another device',
    );
    expect(document.documentElement.dataset.density).toBe('compact');
  });

  it('saves both of two rapid changes without losing one to a conflict', async () => {
    const user = userEvent.setup();
    const account = preferenceDocument();
    let refused = 0;
    const client = renderSettings({
      updatePreferences: async (patch, options) => {
        const current = await account.read();
        if (options.ifMatch !== current.etag) {
          refused += 1;
          throw apiError(412);
        }

        return account.write(patch);
      },
    });

    await screen.findByRole('radio', { name: 'Compact' });
    await user.click(screen.getByRole('radio', { name: 'Compact' }));
    await user.click(screen.getByLabelText('Reduce motion'));

    await waitFor(() =>
      expect(
        screen.getByText('Preferences saved to your account.'),
      ).toBeInTheDocument(),
    );
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(document.documentElement.dataset.density).toBe('compact');
    expect(document.documentElement.dataset.reducedMotion).toBe('true');

    const patches = client.updatePreferences.mock.calls.map(
      (call) => call[0] as UpdateUserPreferencesRequest,
    );
    expect(patches).toContainEqual({ density: 'compact' });
    expect(patches).toContainEqual({ reducedMotion: true });
    expect(refused).toBe(0);
    await expect(account.read()).resolves.toMatchObject({
      data: { density: 'compact', reducedMotion: true, version: 5 },
    });
  });

  it('reapplies a change the account refused with a stale tag', async () => {
    const user = userEvent.setup();
    let attempts = 0;
    const client = renderSettings({
      updatePreferences: async () => {
        attempts += 1;
        if (attempts === 1) {
          throw apiError(412);
        }

        return {
          data: { ...preferences, density: 'compact' as const, version: 10 },
          etag: '"v10"',
        };
      },
      getPreferences: async () => ({
        data: { ...preferences, version: 9 },
        etag: '"v9"',
      }),
    });

    await user.click(await screen.findByRole('radio', { name: 'Compact' }));

    expect(
      await screen.findByText('Preferences saved to your account.'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(document.documentElement.dataset.density).toBe('compact');
    expect(client.updatePreferences.mock.calls[1]?.[1]).toEqual({
      ifMatch: '"v9"',
    });
  });

  it('keeps a change on the device when the account cannot be reached', async () => {
    const user = userEvent.setup();
    renderSettings({
      updatePreferences: async () => {
        throw new TypeError('Failed to fetch');
      },
    });

    await user.click(await screen.findByLabelText('Reduce motion'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'still apply on this device',
    );
    expect(document.documentElement.dataset.reducedMotion).toBe('true');
  });

  it('applies the preferences stored for the account on arrival', async () => {
    renderSettings({
      getPreferences: async () => ({
        data: {
          density: 'compact',
          reducedMotion: true,
          screenReaderPagedMode: true,
          version: 9,
        },
        etag: '"v9"',
      }),
    });

    await waitFor(() =>
      expect(document.documentElement.dataset.pagedMode).toBe('true'),
    );
    expect(document.documentElement.dataset.reducedMotion).toBe('true');
    expect(document.documentElement.dataset.density).toBe('compact');
  });
});

describe('settings: API keys', () => {
  it('lists existing keys without ever showing a secret', async () => {
    renderSettings();

    const list = await screen.findByRole('list', { name: 'API keys' });
    expect(within(list).getByText('vst_abc')).toBeInTheDocument();
    expect(within(list).queryByText(/secret/i)).not.toBeInTheDocument();
  });

  it('creates a key and reveals the secret exactly once', async () => {
    const user = userEvent.setup();
    const client = renderSettings();

    await screen.findByRole('list', { name: 'API keys' });
    await user.type(screen.getByLabelText('Scopes'), 'assets.read');
    await user.click(screen.getByRole('button', { name: 'Create API key' }));

    expect(await screen.findByText('vst_def_secret-value')).toBeInTheDocument();
    expect(client.createApiKey).toHaveBeenCalledWith({
      scopes: ['assets.read'],
    });

    await user.click(screen.getByRole('button', { name: 'I saved the secret' }));

    expect(screen.queryByText('vst_def_secret-value')).not.toBeInTheDocument();
  });

  it('revokes a key after the change is confirmed', async () => {
    const user = userEvent.setup();
    const client = renderSettings();

    await screen.findByRole('list', { name: 'API keys' });
    await user.click(screen.getByRole('button', { name: 'Revoke vst_abc' }));
    await user.click(screen.getByRole('button', { name: 'Revoke key' }));

    await waitFor(() => expect(client.revokeApiKey).toHaveBeenCalledWith('key-1'));
    expect(client.listApiKeys).toHaveBeenCalledTimes(2);
  });

  it('asks for a scope before contacting the API', async () => {
    const user = userEvent.setup();
    const client = renderSettings();

    await screen.findByRole('list', { name: 'API keys' });
    await user.click(screen.getByRole('button', { name: 'Create API key' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'at least one scope',
    );
    expect(client.createApiKey).not.toHaveBeenCalled();
  });

  it('keeps the list when a key cannot be created', async () => {
    const user = userEvent.setup();
    renderSettings({
      createApiKey: async () => {
        throw apiError(403);
      },
    });

    await screen.findByRole('list', { name: 'API keys' });
    await user.type(screen.getByLabelText('Scopes'), 'assets.read');
    await user.click(screen.getByRole('button', { name: 'Create API key' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'could not be created',
    );
    expect(screen.getByText('vst_abc')).toBeInTheDocument();
  });

  it('explains when keys cannot be read', async () => {
    renderSettings({
      listApiKeys: async () => {
        throw apiError(403);
      },
    });

    expect(
      await screen.findByText(/API keys could not be read/i),
    ).toBeInTheDocument();
  });
});
