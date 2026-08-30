import type { RouteObject } from 'react-router-dom';
import { createBrowserRouter, createMemoryRouter } from 'react-router-dom';
import { galleryRoutes } from './routes/galleryRoutes';

export interface AppRouterOptions {
  initialEntries?: string[];
  additionalRoutes?: RouteObject[];
  staticPreview?: boolean;
}

export function createAppRouter({
  initialEntries,
  additionalRoutes = [],
  staticPreview: staticPreviewOverride,
}: AppRouterOptions = {}) {
  const basename = import.meta.env.BASE_URL;
  const staticPreview =
    staticPreviewOverride ??
    (import.meta.env.MODE === 'pages' || import.meta.env.MODE === 'test');
  const routes = galleryRoutes(
    staticPreview,
    additionalRoutes,
    initialEntries === undefined,
  );

  if (initialEntries) {
    return createMemoryRouter(routes, { basename, initialEntries });
  }

  return createBrowserRouter(routes, { basename });
}
