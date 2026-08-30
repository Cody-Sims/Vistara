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
  LoginRequest,
  SessionSnapshot,
  UpdatePreferencesRequest,
} from '../../api/platform';
import { activeMembership, canAdminister } from './roles';
import {
  SessionContext,
  type SessionContextValue,
  type SessionStatus,
} from './sessionContext';

export interface SessionClient {
  getSession(): Promise<SessionSnapshot>;
  login(request: LoginRequest): Promise<SessionSnapshot>;
  logout(): Promise<void>;
  updatePreferences(
    request: UpdatePreferencesRequest,
  ): Promise<SessionSnapshot>;
}

interface SessionProviderProps {
  readonly client: SessionClient;
  /** `preview` skips every network call for the API-free static preview. */
  readonly mode?: 'live' | 'preview';
  readonly children: ReactNode;
}

interface SessionState {
  readonly status: SessionStatus;
  readonly session?: SessionSnapshot;
  readonly error?: unknown;
}

async function resolveSession(client: SessionClient): Promise<SessionState> {
  try {
    return { status: 'authenticated', session: await client.getSession() };
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
      setState({ status: 'authenticated', session });
      return session;
    },
    [client],
  );

  const signOut = useCallback(async () => {
    request.current += 1;
    try {
      await client.logout();
    } finally {
      setState({ status: 'anonymous' });
    }
  }, [client]);

  const savePreferences = useCallback(
    async (preferences: UpdatePreferencesRequest) => {
      const session = await client.updatePreferences(preferences);
      request.current += 1;
      setState({ status: 'authenticated', session });
    },
    [client],
  );

  const value = useMemo<SessionContextValue>(() => {
    const membership = activeMembership(state.session);

    return {
      status: state.status,
      session: state.session,
      user: state.session?.user,
      membership,
      role: membership?.role,
      canAdminister:
        state.status === 'preview' ? true : canAdminister(state.session),
      error: state.error,
      signIn,
      signOut,
      reload: () => applyResolved({ announcePending: true }),
      savePreferences,
    };
  }, [applyResolved, savePreferences, signIn, signOut, state]);

  return (
    <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
  );
}
