import { VistaraApiClient } from '../api/generated';
import { PlatformApiClient } from '../api/platform';
import { reportingFetch } from '../api/sessionExpiry';

/** Same-origin clients shared by every route so one session bootstrap is reused. */
export const galleryClient = new VistaraApiClient({ fetch: reportingFetch() });
export const platformClient = new PlatformApiClient();
