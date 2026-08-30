import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { Capabilities, SessionSnapshot } from '../../api/platform';
import { LoginPage } from './LoginPage';
import { safeDestination } from './safeDestination';
import { SessionProvider } from './SessionProvider';

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
      role: 'Member',
      status: 'active',
    },
  ],
  activeTenantId: 'tenant-1',
  preferences: {},
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

interface Options {
  readonly login?: () => Promise<SessionSnapshot>;
  readonly getSession?: () => Promise<SessionSnapshot>;
  readonly capabilities?: Capabilities;
  readonly entry?: string;
}

function renderLogin(options: Options = {}) {
  const client = {
    getSession:
      options.getSession ??
      vi.fn(async () => {
        throw apiError(401);
      }),
    login: options.login ?? vi.fn(async () => session),
    logout: vi.fn(async () => undefined),
    updatePreferences: vi.fn(async () => session),
  };
  const capabilitiesClient = {
    getCapabilities: vi.fn(async () => options.capabilities ?? {}),
  };
  const router = createMemoryRouter(
    [
      {
        path: '/login',
        element: <LoginPage capabilities={capabilitiesClient} />,
      },
      { path: '/library', element: <h1>Library</h1> },
      { path: '/settings', element: <h1>Settings</h1> },
    ],
    { initialEntries: [options.entry ?? '/login'] },
  );

  render(
    <SessionProvider client={client}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );

  return { client, capabilitiesClient };
}

describe('sign-in page', () => {
  it('asks for the missing fields before contacting the API', async () => {
    const user = userEvent.setup();
    const { client } = renderLogin();

    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(
      await screen.findByText('Enter the email address for your account.'),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Email address')).toHaveFocus();
    expect(screen.getByLabelText('Email address')).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(client.login).not.toHaveBeenCalled();
  });

  it('signs in and continues to the remembered destination', async () => {
    const user = userEvent.setup();
    const login = vi.fn(async () => session);
    renderLogin({ login, entry: '/login?returnTo=%2Fsettings' });

    await user.type(screen.getByLabelText('Email address'), 'ada@example.test');
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.click(screen.getByLabelText('Keep me signed in'));
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(
      await screen.findByRole('heading', { name: 'Settings' }),
    ).toBeInTheDocument();
    expect(login).toHaveBeenCalledWith({
      email: 'ada@example.test',
      password: 'correct horse',
      rememberMe: true,
    });
  });

  it('refuses to forward an off-site destination', async () => {
    const user = userEvent.setup();
    renderLogin({ entry: '/login?returnTo=https%3A%2F%2Fexample.invalid' });

    await user.type(screen.getByLabelText('Email address'), 'ada@example.test');
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
  });

  it('reports rejected credentials without keeping the password', async () => {
    const user = userEvent.setup();
    renderLogin({
      login: vi.fn(async () => {
        throw apiError(401);
      }),
    });

    await user.type(screen.getByLabelText('Email address'), 'ada@example.test');
    await user.type(screen.getByLabelText('Password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Check your email address and password.');
    expect(screen.getByLabelText('Email address')).toHaveValue(
      'ada@example.test',
    );
    expect(screen.getByLabelText('Password')).toHaveValue('');
    await waitFor(() =>
      expect(screen.getByLabelText('Password')).toHaveFocus(),
    );
  });

  it('separates a throttled attempt from an unavailable service', async () => {
    const user = userEvent.setup();
    renderLogin({
      login: vi.fn(async () => {
        throw apiError(429);
      }),
    });

    await user.type(screen.getByLabelText('Email address'), 'ada@example.test');
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Too many sign-in attempts',
    );
  });

  it('offers the configured single sign-on provider', async () => {
    renderLogin({
      capabilities: {
        authentication: {
          localAccounts: true,
          oidc: { displayName: 'Corp SSO', startPath: '/api/v1/auth/oidc' },
        },
      },
    });

    const link = await screen.findByRole('link', {
      name: 'Continue with Corp SSO',
    });
    expect(link).toHaveAttribute('href', '/api/v1/auth/oidc');
  });

  it('explains when local accounts are disabled', async () => {
    renderLogin({
      capabilities: {
        authentication: {
          localAccounts: false,
          oidc: { displayName: 'Corp SSO', startPath: '/api/v1/auth/oidc' },
        },
      },
    });

    expect(
      await screen.findByRole('link', { name: 'Continue with Corp SSO' }),
    ).toBeInTheDocument();
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
  });

  it('sends an already signed-in visitor onward', async () => {
    renderLogin({ getSession: vi.fn(async () => session) });

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
  });

  it('only follows same-origin destinations', () => {
    expect(safeDestination('/settings?tab=account')).toBe(
      '/settings?tab=account',
    );
    expect(safeDestination('/library#top')).toBe('/library#top');
    expect(safeDestination(null)).toBe('/library');
    expect(safeDestination('')).toBe('/library');
    expect(safeDestination('https://example.invalid/library')).toBe('/library');
    expect(safeDestination('//example.invalid/library')).toBe('/library');
    expect(safeDestination('/\\example.invalid/library')).toBe('/library');
    expect(safeDestination('  /library')).toBe('/library');
  });
});
