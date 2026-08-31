import type { ReactNode } from 'react';
import { Navigate, type RouteObject } from 'react-router-dom';
import {
  LoginPage,
  RequireAdministration,
  RequireSession,
} from '../../features/session';
import { ApplicationFrame } from '../ApplicationFrame';
import {
  InitialLoadingPage,
  RouteErrorBoundary,
} from '../ShellPages';
import {
  AccessibleNotFoundRoute,
  AdminAuditRoute,
  AdminJobsRoute,
  AdminPoliciesRoute,
  AdminStorageRoute,
  AdminUsersRoute,
  AlbumRoute,
  AlbumsRoute,
  FavoritesRoute,
  LibraryRoute,
  PublicShareRoute,
  RoutePlaceholderPage,
  SearchRoute,
  SettingsRoute,
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
  const administrative = (title: string, element: ReactNode) =>
    preview ? (
      <RoutePlaceholderPage title={title} staticPreview={staticPreview} />
    ) : (
      <RequireAdministration>{element}</RequireAdministration>
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
          element: guarded('Settings', <SettingsRoute />),
        },
        {
          path: 'admin',
          element: <Navigate replace to="/admin/users" />,
        },
        {
          path: 'admin/users',
          element: administrative('People', <AdminUsersRoute />),
        },
        {
          path: 'admin/storage',
          element: administrative('Storage', <AdminStorageRoute />),
        },
        {
          path: 'admin/jobs',
          element: administrative('Jobs', <AdminJobsRoute />),
        },
        {
          path: 'admin/policies',
          element: administrative('Policies', <AdminPoliciesRoute />),
        },
        {
          path: 'admin/audit',
          element: administrative('Audit log', <AdminAuditRoute />),
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
          <LoginPage />
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
