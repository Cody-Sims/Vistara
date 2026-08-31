import { VistaraApiError } from './generated/client';
import type { EntityTag, ResourceVersion } from './generated/models';

/** Every Vistara route emits `"v{version}"` entity tags. */
export function versionTag(version: ResourceVersion): EntityTag {
  return `"v${version}"`;
}

/**
 * `412` answers a stale `If-Match`: the record moved on and the edit must be
 * reloaded before it can be reapplied.
 */
export function isStaleVersion(error: unknown): boolean {
  return error instanceof VistaraApiError && error.status === 412;
}

/**
 * `409` answers a state conflict: the request was understood but the resource
 * cannot be in the requested state, so retrying the same edit will not help.
 */
export function isStateConflict(error: unknown): boolean {
  return error instanceof VistaraApiError && error.status === 409;
}
