import type { RouteObject } from 'react-router-dom';
import { createBrowserRouter, createMemoryRouter } from 'react-router-dom';
import { galleryRoutes } from './routes/galleryRoutes';

export interface AppRouterOptions {
  initialEntries?: string[];
  additionalRoutes?: RouteObject[];
  staticPreview?: boolean;
  /**
   * Renders the live routes, including their session guards, instead of the
   * API-free placeholders. Memory routers opt in for tests.
   */
  liveFeatures?: boolean;
}

export function createAppRouter({
  initialEntries,
  additionalRoutes = [],
  staticPreview: staticPreviewOverride,
  liveFeatures,
}: AppRouterOptions = {}) {
  const basename = import.meta.env.BASE_URL;
  const staticPreview =
    staticPreviewOverride ??
    (import.meta.env.MODE === 'pages' || import.meta.env.MODE === 'test');
  const routes = galleryRoutes(
    staticPreview,
    additionalRoutes,
    liveFeatures ?? initialEntries === undefined,
  );

  if (initialEntries) {
    return createMemoryRouter(routes, { basename, initialEntries });
  }

  return createBrowserRouter(routes, { basename });
}
