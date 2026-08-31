import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type { CurrentUser, TenantRole } from '../../api/platform';
import { SessionProvider } from '../../features/session';
import { currentUser } from '../../features/session/sessionTestData';
import { PrimaryNavigation } from './PrimaryNavigation';

function snapshot(role: TenantRole): CurrentUser {
  return currentUser({}, role);
}

function client(role: TenantRole) {
  return {
    getSession: vi.fn(async () => snapshot(role)),
    login: vi.fn(async () => ({
      user: snapshot(role),
      csrfToken: 'token-1',
    })),
    logout: vi.fn(async () => undefined),
  };
}

function renderNavigation(
  path = '/library',
  sessionClient?: ReturnType<typeof client>,
) {
  const router = createMemoryRouter(
    [{ path: '*', element: <PrimaryNavigation /> }],
    { initialEntries: [path] },
  );

  if (!sessionClient) {
    return render(<RouterProvider router={router} />);
  }

  return render(
    <SessionProvider client={sessionClient}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );
}

describe('primary navigation', () => {
  it('provides rail and mobile navigation variants with a primary upload action', () => {
    renderNavigation();

    const rail = screen.getByRole('navigation', {
      name: 'Primary navigation',
    });
    const mobile = screen.getByRole('navigation', {
      name: 'Mobile navigation',
    });

    expect(within(rail).getByRole('link', { name: 'Upload' })).toHaveAttribute(
      'href',
      '/uploads',
    );
    expect(
      within(mobile).getByRole('link', { name: 'Upload' }),
    ).toHaveAttribute('href', '/uploads');
    expect(rail).toHaveAttribute('data-navigation-variant', 'rail');
    expect(mobile).toHaveAttribute('data-navigation-variant', 'bottom');
  });

  it('keeps search and utility destinations URL-addressed without inventing data', () => {
    renderNavigation('/shared/links');

    const rail = screen.getByRole('navigation', {
      name: 'Primary navigation',
    });

    expect(within(rail).getByRole('link', { name: 'Search' })).toHaveAttribute(
      'href',
      '/search',
    );
    for (const [name, href] of [
      ['Tags', '/tags'],
      ['Shared links', '/shared/links'],
      ['Trash', '/trash'],
      ['Settings', '/settings'],
    ]) {
      expect(within(rail).getByRole('link', { name })).toHaveAttribute(
        'href',
        href,
      );
    }
    expect(
      within(rail).getByRole('link', { name: 'Shared links' }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('offers sign-in instead of an account menu without a session', () => {
    renderNavigation();

    const rail = screen.getByRole('navigation', { name: 'Primary navigation' });

    expect(within(rail).getByRole('link', { name: 'Sign in' })).toHaveAttribute(
      'href',
      '/login',
    );
    expect(
      within(rail).queryByRole('link', { name: 'People' }),
    ).not.toBeInTheDocument();
  });

  it('hides administration from members', async () => {
    renderNavigation('/library', client('Member'));

    expect(
      await screen.findAllByRole('button', { name: /Ada Lovelace/ }),
    ).not.toHaveLength(0);
    expect(
      screen.queryByRole('navigation', { name: 'Administration' }),
    ).not.toBeInTheDocument();
  });

  it('shows administration destinations to workspace administrators', async () => {
    renderNavigation('/admin/jobs', client('TenantAdmin'));

    const administration = await screen.findByRole('navigation', {
      name: 'Administration',
    });

    for (const [name, href] of [
      ['People', '/admin/users'],
      ['Storage', '/admin/storage'],
      ['Jobs', '/admin/jobs'],
      ['Policies', '/admin/policies'],
      ['Audit log', '/admin/audit'],
    ]) {
      expect(
        within(administration).getByRole('link', { name }),
      ).toHaveAttribute('href', href);
    }
    expect(
      within(administration).getByRole('link', { name: 'Jobs' }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('signs out from the account menu', async () => {
    const sessionClient = client('Member');
    const user = userEvent.setup();
    renderNavigation('/library', sessionClient);

    const [account] = await screen.findAllByRole('button', {
      name: /Ada Lovelace/,
    });
    await user.click(account!);
    await user.click(screen.getAllByRole('button', { name: 'Sign out' })[0]!);

    expect(sessionClient.logout).toHaveBeenCalledTimes(1);
    expect(
      await screen.findAllByRole('link', { name: 'Sign in' }),
    ).not.toHaveLength(0);
  });

  it('waits for the session before offering sign-in or an account menu', () => {
    const pending = {
      getSession: vi.fn(() => new Promise<CurrentUser>(() => {})),
      login: vi.fn(async () => ({
        user: snapshot('Member'),
        csrfToken: 'token-1',
      })),
      logout: vi.fn(async () => undefined),
    };
    renderNavigation('/library', pending);

    const rail = screen.getByRole('navigation', { name: 'Primary navigation' });

    expect(
      within(rail).getByRole('button', { name: 'Checking your session…' }),
    ).toBeDisabled();
    expect(
      within(rail).queryByRole('link', { name: 'Sign in' }),
    ).not.toBeInTheDocument();
  });

  it('returns focus to the account button when the menu is dismissed', async () => {
    const user = userEvent.setup();
    renderNavigation('/library', client('Member'));

    const [account] = await screen.findAllByRole('button', {
      name: /Ada Lovelace/,
    });
    await user.click(account!);
    await user.tab();
    await user.keyboard('{Escape}');

    expect(account).toHaveFocus();
    expect(account).toHaveAttribute('aria-expanded', 'false');
  });
});
