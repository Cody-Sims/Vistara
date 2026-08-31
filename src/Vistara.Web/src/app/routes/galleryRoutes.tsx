import { lazy, Suspense, type ComponentType, type ReactNode } from 'react';
import { Navigate, type RouteObject } from 'react-router-dom';
import {
  LoginPage,
  RequireAdministration,
  RequireSession,
  type AdministrationGuardProps,
  type SessionScope,
} from '../../features/session';
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

/**
 * The operator screens are one chunk, fetched only when a route that is
 * already past its guard renders. Denied and signed-out accounts never ask
 * for it.
 */
const deferredScreen = (
  pick: (module: typeof import('./deferredScreens')) => ComponentType,
) =>
  lazy(async () => ({ default: pick(await import('./deferredScreens')) }));

const AdminUsersScreen = deferredScreen((module) => module.AdminUsersScreen);
const AdminStorageScreen = deferredScreen(
  (module) => module.AdminStorageScreen,
);
const AdminJobsScreen = deferredScreen((module) => module.AdminJobsScreen);
const AdminPoliciesScreen = deferredScreen(
  (module) => module.AdminPoliciesScreen,
);
const AdminAuditScreen = deferredScreen((module) => module.AdminAuditScreen);
const SettingsScreen = deferredScreen((module) => module.SettingsScreen);

export function galleryRoutes(
  staticPreview: boolean,
  additionalRoutes: RouteObject[] = [],
  liveFeatures = true,
): RouteObject[] {
  const preview = !liveFeatures || staticPreview;
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
  /**
   * A deferred screen is only imported once its guard has admitted the
   * account, so a member who opens an administration URL is told they cannot
   * and downloads nothing.
   */
  const behindGuard = (
    title: string,
    Guard: ComponentType<AdministrationGuardProps>,
    Screen: ComponentType,
    scope?: SessionScope,
  ) =>
    preview ? (
      <RoutePlaceholderPage title={title} staticPreview={staticPreview} />
    ) : (
      <Guard scope={scope}>
        <Suspense fallback={<InitialLoadingPage />}>
          <Screen />
        </Suspense>
      </Guard>
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
          element: behindGuard('Settings', RequireSession, SettingsScreen),
        },
        {
          path: 'admin',
          element: <Navigate replace to="/admin/users" />,
        },
        {
          path: 'admin/users',
          element: behindGuard(
            'People',
            RequireAdministration,
            AdminUsersScreen,
            'members.manage',
          ),
        },
        {
          path: 'admin/storage',
          element: behindGuard(
            'Storage',
            RequireAdministration,
            AdminStorageScreen,
            'quotas.manage',
          ),
        },
        {
          path: 'admin/jobs',
          element: behindGuard(
            'Jobs',
            RequireAdministration,
            AdminJobsScreen,
            'assets.read',
          ),
        },
        {
          path: 'admin/policies',
          element: behindGuard(
            'Policies',
            RequireAdministration,
            AdminPoliciesScreen,
            'quotas.manage',
          ),
        },
        {
          path: 'admin/audit',
          element: behindGuard(
            'Audit log',
            RequireAdministration,
            AdminAuditScreen,
            'members.manage',
          ),
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
      hydrateFallbackElement: <InitialLoadingPage />,
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
