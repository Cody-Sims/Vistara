import type { ReactNode } from 'react';
import { Navigate, type RouteObject } from 'react-router-dom';
import { LoginPage, RequireSession } from '../../features/session';
import { SettingsPage } from '../../features/settings';
import { platformClient } from '../apiClients';
import { ApplicationFrame } from '../ApplicationFrame';
import {
  InitialLoadingPage,
  RouteErrorBoundary,
} from '../ShellPages';
import {
  AccessibleNotFoundRoute,
  AlbumRoute,
  AlbumsRoute,
  FavoritesRoute,
  LibraryRoute,
  PublicShareRoute,
  RoutePlaceholderPage,
  SharesRoute,
  TagsRoute,
  TrashRoute,
  UploadsRoute,
  ViewerRoute,
} from './GalleryRoutePages';

export function galleryRoutes(
  staticPreview: boolean,
  additionalRoutes: RouteObject[] = [],
  liveFeatures = true,
): RouteObject[] {
  const preview = !liveFeatures || staticPreview;
  const feature = (title: string, element: ReactNode) =>
    preview ? (
      <RoutePlaceholderPage title={title} staticPreview={staticPreview} />
    ) : (
      element
    );
  const guarded = (title: string, element: ReactNode) =>
    preview ? (
      <RoutePlaceholderPage title={title} staticPreview={staticPreview} />
    ) : (
      <RequireSession>{element}</RequireSession>
    );

  return [
    {
      path: '/',
      element: <ApplicationFrame staticPreview={staticPreview} />,
      errorElement: <RouteErrorBoundary />,
      hydrateFallbackElement: <InitialLoadingPage />,
      children: [
        {
          index: true,
          element: <Navigate replace to="/library" />,
        },
        {
          path: 'library',
          element: feature('Library', <LibraryRoute />),
        },
        {
          path: 'library/recent',
          element: feature('Recent uploads', <LibraryRoute />),
        },
        {
          path: 'search',
          element: feature('Search', <LibraryRoute />),
        },
        {
          path: 'assets/:assetId',
          element: feature('Asset viewer', <ViewerRoute />),
        },
        {
          path: 'uploads',
          element: feature('Upload images', <UploadsRoute />),
        },
        {
          path: 'albums',
          element: feature('Albums', <AlbumsRoute />),
        },
        {
          path: 'albums/new',
          element: feature('New album', <AlbumsRoute />),
        },
        {
          path: 'albums/:albumId',
          element: feature('Album', <AlbumRoute />),
        },
        {
          path: 'tags',
          element: feature('Tags', <TagsRoute />),
        },
        {
          path: 'tags/:tagId',
          element: feature('Tag', <TagsRoute />),
        },
        {
          path: 'favorites',
          element: feature('Favorites', <FavoritesRoute />),
        },
        {
          path: 'shared/with-me',
          element: <Navigate replace to="/shared/links" />,
        },
        {
          path: 'shared/links',
          element: feature('Share links', <SharesRoute />),
        },
        {
          path: 'trash',
          element: feature('Trash', <TrashRoute />),
        },
        {
          path: 'settings',
          element: guarded('Settings', <SettingsPage />),
        },
        ...additionalRoutes,
        {
          path: '*',
          element: <AccessibleNotFoundRoute />,
        },
      ],
    },
    {
      path: '/login',
      errorElement: <RouteErrorBoundary />,
      element:
        staticPreview || !liveFeatures ? (
          <RoutePlaceholderPage title="Sign in" staticPreview={staticPreview} />
        ) : (
          <LoginPage capabilities={platformClient} />
        ),
    },
    {
      path: '/s/:token',
      errorElement: <RouteErrorBoundary />,
      element: staticPreview ? (
        <RoutePlaceholderPage title="Shared gallery" staticPreview />
      ) : (
        <PublicShareRoute />
      ),
    },
  ];
}
