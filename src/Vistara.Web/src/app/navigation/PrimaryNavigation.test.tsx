import { render, screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { PrimaryNavigation } from './PrimaryNavigation';

describe('primary navigation', () => {
  it('provides rail and mobile navigation variants with a primary upload action', () => {
    const router = createMemoryRouter(
      [{ path: '*', element: <PrimaryNavigation /> }],
      { initialEntries: ['/library'] },
    );

    render(<RouterProvider router={router} />);

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
    const router = createMemoryRouter(
      [{ path: '*', element: <PrimaryNavigation /> }],
      { initialEntries: ['/shared/links'] },
    );

    render(<RouterProvider router={router} />);

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
    expect(
      within(rail).getByRole('button', {
        name: 'Account controls (not connected)',
      }),
    ).toBeDisabled();
  });
});
