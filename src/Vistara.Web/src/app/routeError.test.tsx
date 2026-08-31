import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  createMemoryRouter,
  MemoryRouter,
  RouterProvider,
} from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { RouteErrorBoundary, RouteErrorPage } from './ShellPages';

function renderFailedRoute(error: () => never) {
  const router = createMemoryRouter(
    [
      {
        path: '/settings',
        errorElement: <RouteErrorBoundary />,
        lazy: async () => error(),
      },
    ],
    { initialEntries: ['/settings'] },
  );

  return render(<RouterProvider router={router} />);
}

describe('route error page', () => {
  it('offers a reload when a deferred screen fails to arrive', async () => {
    const reload = vi.fn();
    const user = userEvent.setup();
    render(
      <MemoryRouter>
        <RouteErrorPage
          detail="Failed to fetch dynamically imported module"
          onReload={reload}
        />
      </MemoryRouter>,
    );

    const button = screen.getByRole('button', { name: 'Reload this page' });
    expect(button).toBeVisible();
    await user.click(button);
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('explains that a stale page can be reloaded after a failed import', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    renderFailedRoute(() => {
      throw new TypeError('Failed to fetch dynamically imported module: /assets/x.js');
    });

    expect(
      await screen.findByRole('heading', { name: 'Something went wrong' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Reload this page' }),
    ).toBeVisible();
    expect(
      screen.getByRole('link', { name: 'Return to library' }),
    ).toBeVisible();
  });
});
