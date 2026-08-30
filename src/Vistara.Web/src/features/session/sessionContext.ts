import { createContext, useContext } from 'react';
import type {
  LoginRequest,
  SessionSnapshot,
  SessionUser,
  TenantMembership,
  TenantRole,
  UpdatePreferencesRequest,
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
  readonly session?: SessionSnapshot;
  readonly user?: SessionUser;
  readonly membership?: TenantMembership;
  readonly role?: TenantRole;
  readonly canAdminister: boolean;
  readonly error?: unknown;
  signIn(request: LoginRequest): Promise<SessionSnapshot>;
  signOut(): Promise<void>;
  reload(): Promise<void>;
  savePreferences(request: UpdatePreferencesRequest): Promise<void>;
}

const anonymousSession: SessionContextValue = {
  status: 'anonymous',
  canAdminister: false,
  signIn: () => Promise.reject(new Error('No session provider is mounted.')),
  signOut: () => Promise.resolve(),
  reload: () => Promise.resolve(),
  savePreferences: () =>
    Promise.reject(new Error('No session provider is mounted.')),
};

export const SessionContext =
  createContext<SessionContextValue>(anonymousSession);

export function useSession(): SessionContextValue {
  return useContext(SessionContext);
}
