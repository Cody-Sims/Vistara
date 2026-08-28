import type { RouteObject } from 'react-router-dom';
import {
  createBrowserRouter,
  createMemoryRouter,
  redirect,
} from 'react-router-dom';
import { ApplicationFrame } from './ApplicationFrame';
import {
  InitialLoadingPage,
  LibraryPage,
  NotFoundPage,
  RouteErrorBoundary,
} from './ShellPages';

export interface AppRouterOptions {
  initialEntries?: string[];
  additionalRoutes?: RouteObject[];
}

export function createAppRouter({
  initialEntries,
  additionalRoutes = [],
}: AppRouterOptions = {}) {
  const basename = import.meta.env.BASE_URL;
  const staticPreview = import.meta.env.MODE === 'pages';
  const routes: RouteObject[] = [
    {
      path: '/',
      element: <ApplicationFrame staticPreview={staticPreview} />,
      errorElement: <RouteErrorBoundary />,
      hydrateFallbackElement: <InitialLoadingPage />,
      children: [
        {
          index: true,
          loader: () => redirect('/library'),
          element: <InitialLoadingPage />,
        },
        {
          path: 'library',
          element: <LibraryPage />,
        },
        ...additionalRoutes,
        {
          path: '*',
          element: <NotFoundPage />,
        },
      ],
    },
  ];

  if (initialEntries) {
    return createMemoryRouter(routes, { basename, initialEntries });
  }

  return createBrowserRouter(routes, { basename });
}
