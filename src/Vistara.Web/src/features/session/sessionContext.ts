import { createContext, useContext } from 'react';
import type {
  CurrentUser,
  LoginRequest,
  TenantMembership,
  TenantRole,
} from '../../api/platform';
import type { CredentialKind, SessionScope } from './roles';

export type SessionStatus =
  | 'loading'
  | 'authenticated'
  | 'anonymous'
  | 'error'
  /** The static preview build has no API, so no session is fetched. */
  | 'preview';

export interface SessionContextValue {
  readonly status: SessionStatus;
  readonly user?: CurrentUser;
  readonly membership?: TenantMembership;
  readonly role?: TenantRole;
  /** How the API authenticated this session. */
  readonly credentialKind: CredentialKind;
  /** Scopes this session may spend; empty for a tenant-bound credential. */
  readonly scopes: readonly SessionScope[];
  readonly canAdminister: boolean;
  readonly error?: unknown;
  /**
   * True when the last sign-out could not be confirmed by the server, so the
   * cookie session may still be open elsewhere.
   */
  readonly signOutIncomplete: boolean;
  signIn(request: LoginRequest): Promise<CurrentUser>;
  signOut(): Promise<void>;
  reload(): Promise<void>;
}

const anonymousSession: SessionContextValue = {
  status: 'anonymous',
  credentialKind: 'tenantBound',
  scopes: [],
  canAdminister: false,
  signOutIncomplete: false,
  signIn: () => Promise.reject(new Error('No session provider is mounted.')),
  signOut: () => Promise.resolve(),
  reload: () => Promise.resolve(),
};

export const SessionContext =
  createContext<SessionContextValue>(anonymousSession);

export function useSession(): SessionContextValue {
  return useContext(SessionContext);
}
