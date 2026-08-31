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
  /** Optional notification that a request needing a session was refused. */
  onUnauthorized?(listener: () => void): () => void;
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
  const [signOutIncomplete, setSignOutIncomplete] = useState(false);
  // The identity the caches on this device belong to.
  const cachedIdentity = useRef<string | undefined>(undefined);

  const endAccount = useCallback(
    () => Promise.resolve(onSessionEnd?.() ?? clearAccountScopedData()),
    [onSessionEnd],
  );

  /**
   * Account-scoped caches belong to exactly one signed-in identity. Whenever
   * the resolved identity changes — a different user signs in, or the session
   * is gone — everything the previous account left behind is dropped before
   * the new state is published.
   */
  const adoptIdentity = useCallback(
    async (identity: string | undefined) => {
      if (cachedIdentity.current === identity) {
        return;
      }

      const hadAccount = cachedIdentity.current !== undefined;
      cachedIdentity.current = identity;
      if (hadAccount) {
        await endAccount();
      }
    },
    [endAccount],
  );

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
      if (request.current !== id) {
        return;
      }

      await adoptIdentity(next.user?.userId);
      setState(next);
    },
    [adoptIdentity, client, mode],
  );

  useEffect(() => {
    if (mode === 'preview') {
      return;
    }

    const id = ++request.current;
    void resolveSession(client).then(async (next) => {
      if (request.current !== id) {
        return;
      }

      await adoptIdentity(next.user?.userId);
      setState(next);
    });

    return () => {
      request.current += 1;
    };
  }, [adoptIdentity, client, mode]);

  // Any refused request ends the session once: the state moves to anonymous,
  // the account's data is dropped, and the guards send private routes to sign
  // in with the destination they were on.
  useEffect(() => {
    if (mode === 'preview' || !client.onUnauthorized) {
      return;
    }

    return client.onUnauthorized(() => {
      if (cachedIdentity.current === undefined) {
        return;
      }

      request.current += 1;
      cachedIdentity.current = undefined;
      setState({ status: 'anonymous' });
      void endAccount();
    });
  }, [client, endAccount, mode]);

  const signIn = useCallback(
    async (credentials: LoginRequest) => {
      const session = await client.login(credentials);
      setSignOutIncomplete(false);
      request.current += 1;
      await adoptIdentity(session.user.userId);
      setState({ status: 'authenticated', user: session.user });
      return session.user;
    },
    [adoptIdentity, client],
  );

  const signOut = useCallback(async () => {
    request.current += 1;
    let confirmed = true;
    try {
      await client.logout();
    } catch {
      // The browser session ends locally either way, but the server may still
      // hold the cookie, so the visitor is told.
      confirmed = false;
    }

    setSignOutIncomplete(!confirmed);
    cachedIdentity.current = undefined;
    setState({ status: 'anonymous' });
    await endAccount();
  }, [client, endAccount]);

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
      signOutIncomplete,
      signIn,
      signOut,
      reload: () => applyResolved({ announcePending: true }),
    };
  }, [applyResolved, signIn, signOut, signOutIncomplete, state]);

  return (
    <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
  );
}
