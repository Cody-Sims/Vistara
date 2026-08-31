import { VistaraApiClient } from '../api/generated';
import { credentialedFetch } from '../api/credentialedFetch';
import { PlatformApiClient } from '../api/platform';
import { reportingFetch } from '../api/sessionExpiry';

/**
 * Same-origin clients shared by every route so one session bootstrap is
 * reused. Both send through a wrapped fetch, so a refused request ends the
 * session once and every unsafe request made for a cookie session carries the
 * antiforgery header the API published. The generated gallery client is not
 * edited; it is constructed with the wrapped fetch.
 */
export const galleryClient = new VistaraApiClient({
  fetch: credentialedFetch(reportingFetch()),
});
export const platformClient = new PlatformApiClient({ fetch: reportingFetch() });
