import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { CurrentUser, ProvisionedOwner } from '../../api/platform';
import { SessionProvider } from './SessionProvider';
import { SetupPage } from './SetupPage';
import { currentUser } from './sessionTestData';

const owner: ProvisionedOwner = {
  tenantId: 'tenant-a',
  tenantSlug: 'studio',
  tenantName: 'Studio',
  userId: 'user-1',
  email: 'ada@example.test',
  displayName: 'Ada Lovelace',
  role: 'TenantOwner',
};

function apiError(status: number, code: string, errors = {}) {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: code,
    status,
    code,
    errors,
  });
}

interface Options {
  readonly provision?: () => Promise<ProvisionedOwner>;
  readonly login?: () => Promise<{ user: CurrentUser; csrfToken: string }>;
  readonly getSession?: () => Promise<CurrentUser>;
}

const password = 'a very long owner password';

function renderSetup(options: Options = {}) {
  const client = {
    getSession:
      options.getSession ??
      vi.fn(async (): Promise<CurrentUser> => {
        throw apiError(401, 'auth.unauthenticated');
      }),
    login:
      options.login ??
      vi.fn(async () => ({ user: currentUser(), csrfToken: 'token-1' })),
    logout: vi.fn(async () => undefined),
    provisionFirstOwner: vi.fn(options.provision ?? (async () => owner)),
  };
  const router = createMemoryRouter(
    [
      { path: '/setup', element: <SetupPage client={client} /> },
      { path: '/login', element: <h1>Sign in</h1> },
      { path: '/library', element: <h1>Library</h1> },
    ],
    { initialEntries: ['/setup'] },
  );

  render(
    <SessionProvider client={client}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );

  return client;
}

async function completeForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Workspace name'), 'Studio');
  await user.type(screen.getByLabelText('Your name'), 'Ada Lovelace');
  await user.type(screen.getByLabelText('Email address'), 'ada@example.test');
  await user.type(screen.getByLabelText('Password'), password);
  await user.type(screen.getByLabelText('Confirm password'), password);
}

afterEach(() => {
  localStorage.clear();
  sessionStorage.clear();
});

describe('first-run setup', () => {
  it('asks for every missing field before contacting the API', async () => {
    const user = userEvent.setup();
    const client = renderSetup();

    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    expect(
      await screen.findByText('Enter a name for this workspace.'),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Workspace name')).toHaveFocus();
    expect(screen.getByLabelText('Workspace name')).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(client.provisionFirstOwner).not.toHaveBeenCalled();
  });

  it('derives an editable workspace address from the name', async () => {
    const user = userEvent.setup();
    renderSetup();

    await user.type(
      screen.getByLabelText('Workspace name'),
      'Harbour Lights Studio',
    );

    expect(screen.getByLabelText('Workspace address')).toHaveValue(
      'harbour-lights-studio',
    );

    await user.clear(screen.getByLabelText('Workspace address'));
    await user.type(screen.getByLabelText('Workspace address'), 'harbour');
    await user.type(screen.getByLabelText('Workspace name'), ' Two');

    expect(screen.getByLabelText('Workspace address')).toHaveValue('harbour');
  });

  it('refuses a password that is too short or mistyped', async () => {
    const user = userEvent.setup();
    const client = renderSetup();

    await user.type(screen.getByLabelText('Workspace name'), 'Studio');
    await user.type(screen.getByLabelText('Your name'), 'Ada');
    await user.type(screen.getByLabelText('Email address'), 'ada@example.test');
    await user.type(screen.getByLabelText('Password'), 'short');
    await user.type(screen.getByLabelText('Confirm password'), 'short');
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    expect(
      await screen.findByText(/at least 12 characters/),
    ).toBeInTheDocument();

    await user.clear(screen.getByLabelText('Password'));
    await user.type(screen.getByLabelText('Password'), password);
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    expect(
      await screen.findByText('Both passwords must match.'),
    ).toBeInTheDocument();
    expect(client.provisionFirstOwner).not.toHaveBeenCalled();
  });

  it('reveals and hides the password with an announced control', async () => {
    const user = userEvent.setup();
    renderSetup();

    const field = screen.getByLabelText('Password');
    const toggle = screen.getByRole('button', { name: 'Show password' });

    expect(field).toHaveAttribute('type', 'password');
    expect(toggle).toHaveAttribute('aria-pressed', 'false');

    await user.click(toggle);

    expect(field).toHaveAttribute('type', 'text');
    expect(
      screen.getByRole('button', { name: 'Hide password' }),
    ).toHaveAttribute('aria-pressed', 'true');
  });

  it('creates the workspace and signs the owner in', async () => {
    const user = userEvent.setup();
    const client = renderSetup();

    await completeForm(user);
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    await waitFor(() =>
      expect(client.provisionFirstOwner).toHaveBeenCalledWith({
        tenantSlug: 'studio',
        tenantName: 'Studio',
        displayName: 'Ada Lovelace',
        email: 'ada@example.test',
        password,
      }),
    );
    await waitFor(() =>
      expect(client.login).toHaveBeenCalledWith({
        login: 'ada@example.test',
        password,
      }),
    );
    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
  });

  it('never keeps the password after the attempt', async () => {
    const user = userEvent.setup();
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    renderSetup();

    await completeForm(user);
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    await waitFor(() =>
      expect(
        screen.queryByRole('heading', { name: 'Library' }),
      ).toBeInTheDocument(),
    );

    expect(document.body.innerHTML).not.toContain(password);
    for (const call of setItem.mock.calls) {
      expect(String(call[1])).not.toContain(password);
    }
    expect(JSON.stringify(sessionStorage)).not.toContain(password);
    expect(JSON.stringify(localStorage)).not.toContain(password);
  });

  it('offers sign-in when the workspace exists but the handoff fails', async () => {
    const user = userEvent.setup();
    renderSetup({
      login: vi.fn(async () => {
        throw apiError(503, 'auth.unavailable');
      }),
    });

    await completeForm(user);
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    expect(
      await screen.findByRole('heading', { name: 'Workspace ready' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to sign in' })).toHaveAttribute(
      'href',
      '/login',
    );
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
    expect(document.body.innerHTML).not.toContain(password);
  });

  it('explains a platform that already has an owner', async () => {
    const user = userEvent.setup();
    renderSetup({
      provision: async () => {
        throw apiError(409, 'setup.already_provisioned');
      },
    });

    await completeForm(user);
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('already has an owner');
    expect(screen.getByRole('link', { name: 'Go to sign in' })).toBeVisible();
    expect(screen.getByLabelText('Password')).toHaveValue('');
  });

  it('invites another attempt when setup is contended', async () => {
    const user = userEvent.setup();
    renderSetup({
      provision: async () => {
        throw apiError(409, 'setup.provisioning_contended');
      },
    });

    await completeForm(user);
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Another setup is already running',
    );
    expect(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    ).toBeEnabled();
  });

  it('shows the fields the API rejected', async () => {
    const user = userEvent.setup();
    renderSetup({
      provision: async () => {
        throw apiError(422, 'setup.invalid_request', {
          tenantSlug: ['The workspace address is already taken.'],
        });
      },
    });

    await completeForm(user);
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    expect(
      await screen.findByText('The workspace address is already taken.'),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByLabelText('Workspace address')).toHaveFocus(),
    );
  });

  it('reports a password the deployment considers too weak', async () => {
    const user = userEvent.setup();
    renderSetup({
      provision: async () => {
        throw apiError(422, 'setup.weak_password');
      },
    });

    await completeForm(user);
    await user.click(
      screen.getByRole('button', { name: 'Create workspace and owner' }),
    );

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'password this deployment accepts',
    );
    expect(screen.getByLabelText('Password')).toHaveValue('');
  });

  it('sends an already signed-in visitor to the library', async () => {
    renderSetup({ getSession: vi.fn(async () => currentUser()) });

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
  });
});
