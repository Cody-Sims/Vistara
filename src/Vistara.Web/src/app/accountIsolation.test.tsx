import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../api/generated/client';
import type { CurrentUser } from '../api/platform';
import {
  getStorageDraft,
  updateProviderDraft,
} from '../features/admin';
import { useSession } from '../features/session';
import { currentUser } from '../features/session/sessionTestData';
import { ApplicationProviders } from './ApplicationProviders';

function unauthorized() {
  return new VistaraApiError(401, {
    type: 'about:blank',
    title: 'auth.unauthenticated',
    status: 401,
    code: 'auth.unauthenticated',
    errors: {},
  });
}

function SessionProbe() {
  const session = useSession();
  return (
    <div>
      <p data-testid="status">{session.status}</p>
      <p data-testid="user">{session.user?.userId ?? 'none'}</p>
      <button
        type="button"
        onClick={() =>
          void session.signIn({ login: 'grace@example.test', password: 'pw' })
        }
      >
        Sign in
      </button>
      <button type="button" onClick={() => void session.reload()}>
        Reload session
      </button>
    </div>
  );
}

function seedAccountData(queryClient: QueryClient) {
  queryClient.setQueryData(['assets'], { owner: 'first-account' });
  queryClient.setQueryData(['uploads'], [{ id: 'upload-1' }]);
  sessionStorage.setItem(
    'vistara:route-restoration:/library',
    '{"scrollTop":420,"focusedAssetId":"asset-1"}',
  );
  sessionStorage.setItem('vistara:theme', 'dark');
  updateProviderDraft('s3', { secretAccessKey: 'first-account-secret' });
}

function renderApplication(client: {
  getSession: () => Promise<CurrentUser>;
  login: (request: {
    login: string;
    password: string;
  }) => Promise<{ user: CurrentUser; csrfToken: string }>;
  logout: () => Promise<void>;
}) {
  const queryClient = new QueryClient();
  const router = createMemoryRouter([{ path: '*', element: <SessionProbe /> }], {
    initialEntries: ['/library'],
  });

  render(
    <ApplicationProviders
      queryClient={queryClient}
      router={router}
      sessionClient={client}
    />,
  );

  return queryClient;
}

afterEach(() => {
  sessionStorage.clear();
  updateProviderDraft('s3', { secretAccessKey: '' });
});

describe('account isolation', () => {
  it('drops the previous account when a different user signs in directly', async () => {
    const user = userEvent.setup();
    const second = currentUser({
      userId: 'user-2',
      email: 'grace@example.test',
      displayName: 'Grace Hopper',
    });
    const client = {
      getSession: vi.fn(async () => currentUser()),
      login: vi.fn(async () => ({ user: second, csrfToken: 'token-2' })),
      logout: vi.fn(async () => undefined),
    };

    const queryClient = renderApplication(client);
    await waitFor(() =>
      expect(screen.getByTestId('user')).toHaveTextContent('user-1'),
    );
    seedAccountData(queryClient);

    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() =>
      expect(screen.getByTestId('user')).toHaveTextContent('user-2'),
    );
    await waitFor(() =>
      expect(queryClient.getQueryData(['assets'])).toBeUndefined(),
    );
    expect(queryClient.getQueryData(['uploads'])).toBeUndefined();
    expect(
      sessionStorage.getItem('vistara:route-restoration:/library'),
    ).toBeNull();
    expect(getStorageDraft().s3.secretAccessKey).toBe('');
    expect(sessionStorage.getItem('vistara:theme')).toBe('dark');
  });

  it('keeps the cache when the same account signs in again', async () => {
    const user = userEvent.setup();
    const client = {
      getSession: vi.fn(async () => currentUser()),
      login: vi.fn(async () => ({ user: currentUser(), csrfToken: 'token-1' })),
      logout: vi.fn(async () => undefined),
    };

    const queryClient = renderApplication(client);
    await waitFor(() =>
      expect(screen.getByTestId('user')).toHaveTextContent('user-1'),
    );
    queryClient.setQueryData(['assets'], { owner: 'same-account' });

    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => expect(client.login).toHaveBeenCalledTimes(1));
    expect(queryClient.getQueryData(['assets'])).toEqual({
      owner: 'same-account',
    });
  });

  it('clears account data when a resolved session turns anonymous', async () => {
    const user = userEvent.setup();
    const getSession = vi
      .fn<() => Promise<CurrentUser>>()
      .mockResolvedValueOnce(currentUser())
      .mockRejectedValue(unauthorized());
    const client = {
      getSession,
      login: vi.fn(async () => ({ user: currentUser(), csrfToken: 'token-1' })),
      logout: vi.fn(async () => undefined),
    };

    const queryClient = renderApplication(client);
    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated'),
    );
    seedAccountData(queryClient);

    // A later read finds the session gone, which is the end of that account on
    // this device even though nobody pressed sign out.
    await user.click(screen.getByRole('button', { name: 'Reload session' }));

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('anonymous'),
    );
    await waitFor(() =>
      expect(queryClient.getQueryData(['assets'])).toBeUndefined(),
    );
    expect(
      sessionStorage.getItem('vistara:route-restoration:/library'),
    ).toBeNull();
    expect(getStorageDraft().s3.secretAccessKey).toBe('');
  });
});
