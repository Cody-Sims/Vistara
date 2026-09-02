import { readFileSync } from 'node:fs';
import { statePath } from './paths.js';

export interface BrowserState {
  readonly tenantId: string;
  readonly userId: string;
  readonly primaryAssetId: string;
  readonly trashAssetId: string;
  readonly apiKey: string;
  /** Sign-in for the cookie session this suite opens in a real browser. */
  readonly login: string;
  readonly password: string;
}

/**
 * The hosted sign-in environment: a second API instance with its own database,
 * and the stub identity provider it federates with. Nothing here is a secret;
 * both are started for one run and torn down with it.
 */
export interface HostedSignInState {
  readonly baseUrl: string;
  readonly identityProviderUrl: string;
  readonly apiPid: number;
  readonly identityProviderPid: number;
  readonly providerId: string;
  readonly providerDisplayName: string;
  readonly directoryTenantId: string;
  readonly foreignDirectoryTenantId: string;
  /** The directory identity already linked to the seeded member. */
  readonly memberObjectId: string;
  /** The only directory identity allowed to claim the first workspace. */
  readonly allowedOwnerObjectId: string;
  /** A directory identity that is neither a member nor allowlisted. */
  readonly strangerObjectId: string;
  readonly tenantSlug: string;
  readonly bootstrapTenantSlug: string;
  readonly login: string;
  readonly password: string;
}

export interface RuntimeState {
  readonly baseUrl: string;
  readonly apiPid: number;
  readonly workerPid: number;
  readonly browsers: Readonly<Record<string, BrowserState>>;
  readonly oidc: HostedSignInState;
}

export function readRuntimeState(): RuntimeState {
  return JSON.parse(readFileSync(statePath, 'utf8')) as RuntimeState;
}
