import { render, screen } from '@testing-library/react';
import { RouterProvider, matchRoutes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { createAppRouter } from '../router';
import { galleryRoutes } from './galleryRoutes';

describe('gallery application routes', () => {
  it('registers every implemented gallery destination', () => {
    const paths = [
      '/library',
      '/library/recent',
      '/assets/01990a2a-bc00-7000-8000-000000000001',
      '/uploads',
      '/albums',
      '/albums/new',
      '/albums/01990a2a-bc00-7000-8000-000000000002',
      '/tags',
      '/tags/01990a2a-bc00-7000-8000-000000000003',
      '/favorites',
      '/shared/with-me',
      '/shared/links',
      '/trash',
    ];

    for (const path of paths) {
      expect(matchRoutes(galleryRoutes(false), path), path).not.toBeNull();
    }
  });

  it('keeps the Pages preview API-free and explicit', async () => {
    const router = createAppRouter({
      initialEntries: ['/uploads'],
      staticPreview: true,
    });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Upload images' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('note')).toHaveTextContent(
      'Static preview only. This page does not connect to an API.',
    );
  });
});
