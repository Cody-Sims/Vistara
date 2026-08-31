import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CurrentUser } from '../api/platform';
import { useSession } from '../features/session';
import { currentUser } from '../features/session/sessionTestData';
import { ApplicationProviders } from './ApplicationProviders';

function SignOutProbe() {
  const session = useSession();
  return (
    <div>
      <p data-testid="status">{session.status}</p>
      <button type="button" onClick={() => void session.signOut()}>
        Sign out
      </button>
    </div>
  );
}

afterEach(() => {
  sessionStorage.clear();
});

describe('application sign-out', () => {
  it('empties the shared query cache and gallery session storage', async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient();
    queryClient.setQueryData(['assets'], { owner: 'first-account' });
    sessionStorage.setItem(
      'vistara:route-restoration:/library',
      '{"scrollTop":420}',
    );
    sessionStorage.setItem('vistara:theme', 'dark');

    const client = {
      getSession: vi.fn(async (): Promise<CurrentUser> => currentUser()),
      login: vi.fn(async () => ({ user: currentUser(), csrfToken: 'token-1' })),
      logout: vi.fn(async () => undefined),
    };
    const router = createMemoryRouter(
      [{ path: '*', element: <SignOutProbe /> }],
      { initialEntries: ['/library'] },
    );

    render(
      <ApplicationProviders
        queryClient={queryClient}
        router={router}
        sessionClient={client}
      />,
    );

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated'),
    );

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    await waitFor(() =>
      expect(screen.getByTestId('status')).toHaveTextContent('anonymous'),
    );
    await waitFor(() =>
      expect(queryClient.getQueryData(['assets'])).toBeUndefined(),
    );
    expect(
      sessionStorage.getItem('vistara:route-restoration:/library'),
    ).toBeNull();
    expect(sessionStorage.getItem('vistara:theme')).toBe('dark');
  });
});
