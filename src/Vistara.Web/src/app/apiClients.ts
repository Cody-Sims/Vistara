import { VistaraApiClient } from '../api/generated';
import { PlatformApiClient } from '../api/platform';

/** Same-origin clients shared by every route so one session bootstrap is reused. */
export const galleryClient = new VistaraApiClient();
export const platformClient = new PlatformApiClient();
