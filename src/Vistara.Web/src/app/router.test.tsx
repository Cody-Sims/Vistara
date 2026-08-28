import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RouterProvider } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { createAppRouter } from './router';

describe('application shell', () => {
  it('redirects home to an accessible library frame', async () => {
    const user = userEvent.setup();
    const router = createAppRouter({ initialEntries: ['/'] });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main-content');

    await user.tab();
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveFocus();
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveAttribute(
      'href',
      '#main-content',
    );
  });

  it('recovers from an unknown route through visible navigation', async () => {
    const user = userEvent.setup();
    const router = createAppRouter({ initialEntries: ['/missing'] });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Page not found' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: 'Return to library' }));

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
  });

  it('announces pending route navigation', async () => {
    let finishLoading: (() => void) | undefined;
    const loader = new Promise<void>((resolve) => {
      finishLoading = resolve;
    });
    const router = createAppRouter({
      initialEntries: ['/library'],
      additionalRoutes: [
        {
          path: 'slow',
          loader: () => loader,
          element: <h1>Loaded route</h1>,
        },
      ],
    });

    render(<RouterProvider router={router} />);
    await screen.findByRole('heading', { name: 'Library' });

    const navigation = router.navigate('/slow');

    expect(await screen.findByRole('status')).toHaveTextContent('Loading page');

    finishLoading?.();
    await navigation;

    expect(
      await screen.findByRole('heading', { name: 'Loaded route' }),
    ).toBeInTheDocument();
  });

  it('renders a useful boundary when a route loader fails', async () => {
    const router = createAppRouter({
      initialEntries: ['/broken'],
      additionalRoutes: [
        {
          path: 'broken',
          loader: () => {
            throw new Response('Unavailable', {
              status: 503,
              statusText: 'Service Unavailable',
            });
          },
          element: <h1>Never rendered</h1>,
        },
      ],
    });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Something went wrong' }),
    ).toBeInTheDocument();
    expect(screen.getByText(/503 Service Unavailable/)).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Return to library' }),
    ).toBeInTheDocument();
  });
});
