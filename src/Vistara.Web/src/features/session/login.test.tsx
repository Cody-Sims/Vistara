import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { CurrentUser, SetupState } from '../../api/platform';
import { PlatformApiClient, VistaraThrottledError } from '../../api/platform';
import { hostedProviderKey } from './hostedSignIn';
import { LoginPage } from './LoginPage';
import { safeDestination } from './safeDestination';
import { SessionProvider } from './SessionProvider';
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

interface Options {
  readonly login?: () => Promise<{ user: CurrentUser; csrfToken: string }>;
  readonly getSession?: () => Promise<CurrentUser>;
  readonly getSetupState?: () => Promise<SetupState>;
  readonly entry?: string;
}

function renderLogin(options: Options = {}) {
  const client = {
    getSession:
      options.getSession ??
      vi.fn(async () => {
        throw apiError(401);
      }),
    login:
      options.login ??
      vi.fn(async () => ({ user: currentUser(), csrfToken: 'token-1' })),
    logout: vi.fn(async () => undefined),
  };
  const setup = options.getSetupState
    ? { getSetupState: vi.fn(options.getSetupState) }
    : undefined;
  const router = createMemoryRouter(
    [
      { path: '/login', element: <LoginPage setup={setup} /> },
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

  return Object.assign(client, { router });
}

describe('sign-in page', () => {
  it('asks for the missing fields before contacting the API', async () => {
    const user = userEvent.setup();
    const client = renderLogin();

    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(
      await screen.findByText(
        'Enter the email address or user name for your account.',
      ),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Email address or user name')).toHaveFocus();
    expect(client.login).not.toHaveBeenCalled();
  });

  it('signs in with the login field the API accepts', async () => {
    const user = userEvent.setup();
    const login = vi.fn(async () => ({
      user: currentUser(),
      csrfToken: 'token-1',
    }));
    renderLogin({ login, entry: '/login?returnTo=%2Fsettings' });

    await user.type(
      screen.getByLabelText('Email address or user name'),
      'ada@example.test',
    );
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(
      await screen.findByRole('heading', { name: 'Settings' }),
    ).toBeInTheDocument();
    expect(login).toHaveBeenCalledWith({
      login: 'ada@example.test',
      password: 'correct horse',
    });
  });

  it('refuses to forward an off-site destination', async () => {
    const user = userEvent.setup();
    renderLogin({ entry: '/login?returnTo=https%3A%2F%2Fexample.invalid' });

    await user.type(
      screen.getByLabelText('Email address or user name'),
      'ada@example.test',
    );
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
  });

  it('reports rejected credentials and clears the password field', async () => {
    const user = userEvent.setup();
    renderLogin({
      login: vi.fn(async () => {
        throw apiError(401);
      }),
    });

    await user.type(
      screen.getByLabelText('Email address or user name'),
      'ada@example.test',
    );
    await user.type(screen.getByLabelText('Password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Check your email address and password.');
    expect(screen.getByLabelText('Email address or user name')).toHaveValue(
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

    await user.type(
      screen.getByLabelText('Email address or user name'),
      'ada@example.test',
    );
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Too many sign-in attempts',
    );
  });

  it('sends an already signed-in visitor onward', async () => {
    renderLogin({ getSession: vi.fn(async () => currentUser()) });

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

describe('first-run discovery', () => {
  it('offers setup when the deployment reports no owner', async () => {
    renderLogin({ getSetupState: async () => ({ available: true }) });

    expect(
      await screen.findByRole('link', { name: 'Set up Vistara' }),
    ).toHaveAttribute('href', '/setup');
  });

  it('hides setup once the deployment has an owner', async () => {
    renderLogin({ getSetupState: async () => ({ available: false }) });

    await screen.findByLabelText('Password');
    expect(
      screen.queryByRole('link', { name: /set up vistara/i }),
    ).not.toBeInTheDocument();
  });

  it('keeps a way in when setup discovery is throttled', async () => {
    renderLogin({
      getSetupState: async () => {
        throw new VistaraThrottledError(
          {
            type: 'about:blank',
            title: 'setup.throttled',
            status: 429,
            code: 'setup.throttled',
            errors: {},
          },
          20,
        );
      },
    });

    expect(
      await screen.findByRole('link', { name: 'set up Vistara' }),
    ).toBeInTheDocument();
    expect(screen.getByText(/20 seconds/)).toBeInTheDocument();
  });

  it('keeps a way in when the deployment cannot say', async () => {
    renderLogin({
      getSetupState: async () => {
        throw apiError(404);
      },
    });

    expect(
      await screen.findByRole('link', { name: 'set up Vistara' }),
    ).toHaveAttribute('href', '/setup');
    expect(
      screen.getByText(/does not report whether it has an owner/),
    ).toBeInTheDocument();
  });

  it('says nothing about setup when no reader is provided', async () => {
    renderLogin();

    await screen.findByLabelText('Password');
    expect(
      screen.queryByRole('link', { name: /set up vistara/i }),
    ).not.toBeInTheDocument();
  });
});

/**
 * A published provider is one more way in, never a replacement for the local
 * one: a deployment that turns hosted sign-in on must still let an operator
 * sign in with a password and reach first-run setup.
 */
describe('hosted sign-in', () => {
  const entra = {
    id: 'entra',
    displayName: 'Microsoft Entra ID',
    startUrl: '/api/v1/auth/oidc/entra/start',
  };

  beforeEach(() => {
    sessionStorage.clear();
  });

  it('offers the published provider beside the password form', async () => {
    renderLogin({
      getSetupState: async () => ({
        available: false,
        signInProviders: [entra],
      }),
      entry: '/login?returnTo=%2Falbums',
    });

    const control = await screen.findByRole('link', {
      name: 'Sign in with Microsoft Entra ID',
    });
    expect(control).toHaveAttribute(
      'href',
      '/api/v1/auth/oidc/entra/start?returnTo=%2Falbums',
    );
    expect(screen.getByLabelText('Password')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('offers nothing hosted when the deployment publishes no provider', async () => {
    renderLogin({ getSetupState: async () => ({ available: false }) });

    await screen.findByLabelText('Password');
    expect(screen.queryByRole('link', { name: /sign in with/i })).toBeNull();
  });

  /**
   * The provider list arrives over the network, so a start URL that would
   * leave this origin is dropped rather than rendered as a control.
   */
  it('never renders a start URL that leaves this origin', async () => {
    renderLogin({
      getSetupState: async () => ({
        available: false,
        signInProviders: [
          { ...entra, startUrl: 'https://attacker.example/start' },
        ],
      }),
    });

    await screen.findByLabelText('Password');
    expect(screen.queryByRole('link', { name: /sign in with/i })).toBeNull();
  });

  it('records the provider and reports the wait before leaving', async () => {
    const user = userEvent.setup();
    renderLogin({
      getSetupState: async () => ({
        available: false,
        signInProviders: [entra],
      }),
    });

    const control = await screen.findByRole('link', {
      name: 'Sign in with Microsoft Entra ID',
    });
    // jsdom cannot navigate, and the assertions are about what this page did
    // before the browser would have left.
    document.addEventListener('click', preventNavigation);
    try {
      await user.click(control);
    } finally {
      document.removeEventListener('click', preventNavigation);
    }

    expect(sessionStorage.getItem(hostedProviderKey)).toBe('entra');
    expect(control).toHaveAttribute('aria-busy', 'true');
    expect(
      screen.getByText('Taking you to Microsoft Entra ID…'),
    ).toBeInTheDocument();
    expect(JSON.stringify(sessionStorage)).not.toContain('token');
  });

  /**
   * The redirect carries one opaque code and no detail. It is announced,
   * focused for a keyboard visitor, and then taken out of the address bar so a
   * reload does not repeat a failure that already happened.
   */
  it('announces a failed hosted sign-in once and clears the code', async () => {
    const client = renderLogin({
      getSetupState: async () => ({
        available: false,
        signInProviders: [entra],
      }),
      entry: '/login?returnTo=%2Falbums&error=oidc_sign_in_failed',
    });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(
      'Signing in with your identity provider did not finish.',
    );
    await waitFor(() => expect(alert).toHaveFocus());
    await waitFor(() =>
      expect(client.router.state.location.search).toBe('?returnTo=%2Falbums'),
    );
    expect(screen.getByLabelText('Password')).toBeInTheDocument();
  });
});

function preventNavigation(event: Event) {
  event.preventDefault();
}

describe('credential handling', () => {
  it('sends the password once and never again on later requests', async () => {
    const password = 'correct horse battery staple';
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({ user: currentUser(), csrfToken: 'token-1' }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      )
      .mockResolvedValue(new Response(null, { status: 204 }));
    const client = new PlatformApiClient({ fetch });

    await client.login({ login: 'ada@example.test', password });
    await client.logout();
    await client.getCapabilities().catch(() => undefined);

    const bodies = fetch.mock.calls.map((call) => String(call[1]?.body ?? ''));
    expect(bodies.filter((body) => body.includes(password))).toHaveLength(1);
    expect(bodies[0]).toContain(password);
    expect(bodies.slice(1).join('|')).not.toContain(password);

    const headers = fetch.mock.calls.flatMap((call) => [
      ...new Headers(call[1]?.headers).entries(),
    ]);
    expect(headers.map(([, value]) => value).join('|')).not.toContain(password);
    expect(fetch.mock.calls.map((call) => String(call[0])).join('|')).not.toContain(
      password,
    );
    expect(JSON.stringify(localStorage)).not.toContain(password);
    expect(JSON.stringify(sessionStorage)).not.toContain(password);
  });

  it('removes the password from the form as soon as it is submitted', async () => {
    const user = userEvent.setup();
    const password = 'correct horse battery staple';
    let release: ((value: { user: CurrentUser; csrfToken: string }) => void) | undefined;
    renderLogin({
      login: vi.fn(
        () =>
          new Promise<{ user: CurrentUser; csrfToken: string }>((resolve) => {
            release = resolve;
          }),
      ),
    });

    await user.type(
      screen.getByLabelText('Email address or user name'),
      'ada@example.test',
    );
    await user.type(screen.getByLabelText('Password'), password);
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    release?.({ user: currentUser(), csrfToken: 'token-1' });

    await waitFor(() =>
      expect(screen.queryByLabelText('Password')).toSatisfy(
        (field: HTMLInputElement | null) => field === null || field.value === '',
      ),
    );
    expect(document.body.innerHTML).not.toContain(password);
  });
});
