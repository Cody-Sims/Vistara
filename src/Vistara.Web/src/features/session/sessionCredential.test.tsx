import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { sessionCredentials } from '../../api/credentials';
import type { CurrentUser } from '../../api/platform';
import { reportSessionExpired } from '../../api/sessionExpiry';
import { ApplicationProviders } from '../../app/ApplicationProviders';
import { useSession } from './index';
import { currentUser } from './sessionTestData';

function SessionProbe() {
  const session = useSession();
  return (
    <div>
      <p data-testid="status">{session.status}</p>
      <button type="button" onClick={() => void session.signOut()}>
        Sign out
      </button>
      <button
        type="button"
        onClick={() =>
          void session.signIn({ login: 'grace@example.test', password: 'pw' })
        }
      >
        Sign in
      </button>
    </div>
  );
}

function renderSession(client: {
  getSession: () => Promise<CurrentUser>;
  login: () => Promise<{ user: CurrentUser; csrfToken: string }>;
  logout: () => Promise<void>;
}) {
  const router = createMemoryRouter([{ path: '*', element: <SessionProbe /> }], {
    initialEntries: ['/library'],
  });

  render(
    <ApplicationProviders
      queryClient={new QueryClient()}
      router={router}
      sessionClient={client}
    />,
  );
}

function stubClient(user: CurrentUser, loginAs = user, csrfToken = 'token-2') {
  return {
    getSession: vi.fn(async () => user),
    login: vi.fn(async () => ({ user: loginAs, csrfToken })),
    logout: vi.fn(async () => undefined),
  };
}

async function waitForStatus(status: string) {
  await waitFor(() =>
    expect(screen.getByTestId('status')).toHaveTextContent(status),
  );
}

describe('the credential a browser session spends', () => {
  it('is published when the session is read on arrival', async () => {
    renderSession(
      stubClient(
        currentUser({
          csrfHeaderName: 'X-Deployment-CSRF',
          csrfToken: 'token-1',
        }),
      ),
    );

    await waitForStatus('authenticated');

    const headers = new Headers();
    expect(sessionCredentials.applyTo(headers)).toBe('token-1');
    expect(headers.get('X-Deployment-CSRF')).toBe('token-1');
  });

  it('is dropped when the visitor signs out', async () => {
    const user = userEvent.setup();
    renderSession(stubClient(currentUser()));
    await waitForStatus('authenticated');
    expect(sessionCredentials.carriesToken).toBe(true);

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    await waitForStatus('anonymous');
    expect(sessionCredentials.carriesToken).toBe(false);
  });

  it('is dropped when a request is refused for want of a session', async () => {
    renderSession(stubClient(currentUser()));
    await waitForStatus('authenticated');
    expect(sessionCredentials.carriesToken).toBe(true);

    reportSessionExpired();

    await waitForStatus('anonymous');
    expect(sessionCredentials.carriesToken).toBe(false);
  });

  it('never survives into the account that signs in next', async () => {
    const user = userEvent.setup();
    renderSession(
      stubClient(
        currentUser({ csrfToken: 'first-token' }),
        currentUser({
          userId: 'user-2',
          email: 'grace@example.test',
          csrfToken: undefined,
        }),
        'second-token',
      ),
    );
    await waitForStatus('authenticated');

    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() =>
      expect(sessionCredentials.applyTo(new Headers())).toBe('second-token'),
    );
  });

  it('holds nothing for a visitor with no session at all', async () => {
    renderSession({
      getSession: vi.fn(async () => {
        throw Object.assign(new Error('unauthenticated'), { status: 401 });
      }),
      login: vi.fn(async () => ({ user: currentUser(), csrfToken: 'token-1' })),
      logout: vi.fn(async () => undefined),
    });

    await waitForStatus('error');
    expect(sessionCredentials.carriesToken).toBe(false);
  });
});
