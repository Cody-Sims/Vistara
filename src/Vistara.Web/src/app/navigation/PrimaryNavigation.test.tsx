import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { PrimaryNavigation } from './PrimaryNavigation';

describe('primary navigation', () => {
  it('links to the implemented gallery workflows', () => {
    const router = createMemoryRouter(
      [{ path: '*', element: <PrimaryNavigation /> }],
      { initialEntries: ['/library'] },
    );

    render(<RouterProvider router={router} />);

    for (const name of [
      'Library',
      'Uploads',
      'Albums',
      'Tags',
      'Favorites',
      'Shares',
      'Trash',
    ]) {
      expect(screen.getByRole('link', { name })).toBeInTheDocument();
    }
  });
});
