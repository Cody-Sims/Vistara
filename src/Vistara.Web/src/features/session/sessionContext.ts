import { createContext, useContext } from 'react';
import type {
  CurrentUser,
  LoginRequest,
  TenantMembership,
  TenantRole,
} from '../../api/platform';

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
  readonly canAdminister: boolean;
  readonly error?: unknown;
  signIn(request: LoginRequest): Promise<CurrentUser>;
  signOut(): Promise<void>;
  reload(): Promise<void>;
}

const anonymousSession: SessionContextValue = {
  status: 'anonymous',
  canAdminister: false,
  signIn: () => Promise.reject(new Error('No session provider is mounted.')),
  signOut: () => Promise.resolve(),
  reload: () => Promise.resolve(),
};

export const SessionContext =
  createContext<SessionContextValue>(anonymousSession);

export function useSession(): SessionContextValue {
  return useContext(SessionContext);
}
