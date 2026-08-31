import type { ComponentType, ReactNode } from 'react';
import { Navigate, type RouteObject } from 'react-router-dom';
import { LoginPage, RequireSession } from '../../features/session';
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
  SearchRoute,
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
  /**
   * Screens that only a signed-in operator reaches are loaded on demand, so
   * sign-in, first-run setup, and a public share never pay for them.
   */
  const deferred = (
    title: string,
    load: () => Promise<{ Component: ComponentType }>,
  ): RouteObject =>
    preview
      ? {
          element: (
            <RoutePlaceholderPage title={title} staticPreview={staticPreview} />
          ),
        }
      : { lazy: load };
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
          element: guarded('Library', <LibraryRoute />),
        },
        {
          path: 'library/recent',
          element: guarded('Recent uploads', <LibraryRoute />),
        },
        {
          path: 'search',
          element: guarded('Search', <SearchRoute />),
        },
        {
          path: 'assets/:assetId',
          element: guarded('Asset viewer', <ViewerRoute />),
        },
        {
          path: 'uploads',
          element: guarded('Upload images', <UploadsRoute />),
        },
        {
          path: 'albums',
          element: guarded('Albums', <AlbumsRoute />),
        },
        {
          path: 'albums/new',
          element: guarded('New album', <AlbumsRoute />),
        },
        {
          path: 'albums/:albumId',
          element: guarded('Album', <AlbumRoute />),
        },
        {
          path: 'tags',
          element: guarded('Tags', <TagsRoute />),
        },
        {
          path: 'tags/:tagId',
          element: guarded('Tag', <TagsRoute />),
        },
        {
          path: 'favorites',
          element: guarded('Favorites', <FavoritesRoute />),
        },
        {
          path: 'shared/with-me',
          element: <Navigate replace to="/shared/links" />,
        },
        {
          path: 'shared/links',
          element: guarded('Share links', <SharesRoute />),
        },
        {
          path: 'trash',
          element: guarded('Trash', <TrashRoute />),
        },
        {
          path: 'settings',
          ...deferred('Settings', async () => ({
            Component: (await import('./deferredScreens')).SettingsScreen,
          })),
        },
        {
          path: 'admin',
          element: <Navigate replace to="/admin/users" />,
        },
        {
          path: 'admin/users',
          ...deferred('People', async () => ({
            Component: (await import('./deferredScreens')).AdminUsersScreen,
          })),
        },
        {
          path: 'admin/storage',
          ...deferred('Storage', async () => ({
            Component: (await import('./deferredScreens')).AdminStorageScreen,
          })),
        },
        {
          path: 'admin/jobs',
          ...deferred('Jobs', async () => ({
            Component: (await import('./deferredScreens')).AdminJobsScreen,
          })),
        },
        {
          path: 'admin/policies',
          ...deferred('Policies', async () => ({
            Component: (await import('./deferredScreens')).AdminPoliciesScreen,
          })),
        },
        {
          path: 'admin/audit',
          ...deferred('Audit log', async () => ({
            Component: (await import('./deferredScreens')).AdminAuditScreen,
          })),
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
          <LoginPage setup={platformClient} />
        ),
    },
    {
      path: '/setup',
      errorElement: <RouteErrorBoundary />,
      ...deferred('Set up Vistara', async () => ({
        Component: (await import('./deferredScreens')).SetupScreen,
      })),
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
