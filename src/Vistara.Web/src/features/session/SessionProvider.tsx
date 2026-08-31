import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { VistaraApiError } from '../../api/generated/client';
import type {
  CurrentUser,
  LoginRequest,
  LoginResponse,
} from '../../api/platform';
import { clearAccountScopedData } from './accountData';
import { activeMembership, canAdminister } from './roles';
import {
  SessionContext,
  type SessionContextValue,
  type SessionStatus,
} from './sessionContext';

export interface SessionClient {
  getSession(): Promise<CurrentUser>;
  login(request: LoginRequest): Promise<LoginResponse>;
  logout(): Promise<void>;
}

interface SessionProviderProps {
  readonly client: SessionClient;
  /** `preview` skips every network call for the API-free static preview. */
  readonly mode?: 'live' | 'preview';
  /**
   * Clears caches and stores that belong to the account that signed out.
   * Defaults to the shared account-scoped cleanup.
   */
  readonly onSessionEnd?: () => Promise<void> | void;
  readonly children: ReactNode;
}

interface SessionState {
  readonly status: SessionStatus;
  readonly user?: CurrentUser;
  readonly error?: unknown;
}

async function resolveSession(client: SessionClient): Promise<SessionState> {
  try {
    return { status: 'authenticated', user: await client.getSession() };
  } catch (error) {
    return error instanceof VistaraApiError && error.status === 401
      ? { status: 'anonymous' }
      : { status: 'error', error };
  }
}

export function SessionProvider({
  children,
  client,
  mode = 'live',
  onSessionEnd,
}: SessionProviderProps) {
  const [state, setState] = useState<SessionState>(() => ({
    status: mode === 'preview' ? 'preview' : 'loading',
  }));
  const request = useRef(0);

  const applyResolved = useCallback(
    async (options: { announcePending?: boolean } = {}) => {
      if (mode === 'preview') {
        return;
      }

      const id = ++request.current;
      if (options.announcePending) {
        setState({ status: 'loading' });
      }

      const next = await resolveSession(client);
      if (request.current === id) {
        setState(next);
      }
    },
    [client, mode],
  );

  useEffect(() => {
    if (mode === 'preview') {
      return;
    }

    const id = ++request.current;
    void resolveSession(client).then((next) => {
      if (request.current === id) {
        setState(next);
      }
    });

    return () => {
      request.current += 1;
    };
  }, [client, mode]);

  const signIn = useCallback(
    async (credentials: LoginRequest) => {
      const session = await client.login(credentials);
      request.current += 1;
      setState({ status: 'authenticated', user: session.user });
      return session.user;
    },
    [client],
  );

  const signOut = useCallback(async () => {
    request.current += 1;
    try {
      await client.logout();
    } catch {
      // The browser session ends locally either way; the next session read
      // decides whether the server cookie is still valid.
    }

    setState({ status: 'anonymous' });
    await (onSessionEnd?.() ?? clearAccountScopedData());
  }, [client, onSessionEnd]);

  const value = useMemo<SessionContextValue>(() => {
    const membership = activeMembership(state.user);

    return {
      status: state.status,
      user: state.user,
      membership,
      role: membership?.role,
      canAdminister:
        state.status === 'preview' ? true : canAdminister(state.user),
      error: state.error,
      signIn,
      signOut,
      reload: () => applyResolved({ announcePending: true }),
    };
  }, [applyResolved, signIn, signOut, state]);

  return (
    <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
  );
}
