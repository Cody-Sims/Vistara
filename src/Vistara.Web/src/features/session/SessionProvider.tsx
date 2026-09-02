import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { sessionCredentials } from '../../api/credentials';
import { VistaraApiError } from '../../api/generated/client';
import { onSessionExpired } from '../../api/sessionExpiry';
import type {
  CurrentUser,
  HostedSignOut,
  LoginRequest,
  LoginResponse,
} from '../../api/platform';
import { clearAccountScopedData } from './accountData';
import {
  forgetHostedProvider,
  navigateToProviderSignOut,
  providerSignOutUrl,
  readHostedProvider,
} from './hostedSignIn';
import {
  activeMembership,
  canAdminister,
  credentialKind,
  previewScopes,
  sessionScopes,
} from './roles';
import {
  SessionContext,
  type SessionContextValue,
  type SessionStatus,
} from './sessionContext';

export interface SessionClient {
  getSession(): Promise<CurrentUser>;
  login(request: LoginRequest): Promise<LoginResponse>;
  logout(): Promise<void>;
  /**
   * Revokes the session of a browser that signed in with a hosted provider and
   * answers where the provider session may be ended. Optional so a deployment
   * or a preview without hosted sign-in simply signs out locally.
   */
  signOutFromProvider?(providerId: string): Promise<HostedSignOut>;
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
  /**
   * Sends the browser to the provider's end-session URL once the local session
   * is already gone. It is injectable so the handoff can be observed in a test
   * without a real navigation.
   */
  readonly onLeaveForProvider?: (url: string) => void;
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

/**
 * Publishes the credential the resolved session carries, so every client that
 * mutates for this session sends the antiforgery header the API named, and no
 * client holds one once the session is gone.
 */
function publishCredential(state: SessionState): void {
  if (state.user) {
    sessionCredentials.adopt(state.user);
    return;
  }

  if (state.status === 'anonymous') {
    sessionCredentials.clear();
  }
}

export function SessionProvider({
  children,
  client,
  mode = 'live',
  onLeaveForProvider = navigateToProviderSignOut,
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
        // The antiforgery token belonged to the account that just left; it is
        // dropped before the next one is published anything.
        sessionCredentials.clear();
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
      publishCredential(next);
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
      publishCredential(next);
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
    if (mode === 'preview') {
      return;
    }

    const expire = () => {
      if (cachedIdentity.current === undefined) {
        return;
      }

      request.current += 1;
      cachedIdentity.current = undefined;
      forgetHostedProvider();
      sessionCredentials.clear();
      setState({ status: 'anonymous' });
      void endAccount();
    };

    const unsubscribe = [
      client.onUnauthorized?.(expire),
      onSessionExpired(expire),
    ];

    return () => {
      for (const stop of unsubscribe) {
        stop?.();
      }
    };
  }, [client, endAccount, mode]);

  const signIn = useCallback(
    async (credentials: LoginRequest) => {
      const session = await client.login(credentials);
      // Login answers the antiforgery token beside the user rather than on
      // it, and opens a cookie session by definition; both are carried onto
      // the session exactly as a later `GET /api/v1/me` would publish them.
      const user: CurrentUser = {
        ...session.user,
        authenticationKind: session.user.authenticationKind ?? 'cookie',
        csrfToken: session.csrfToken ?? session.user.csrfToken,
      };
      setSignOutIncomplete(false);
      request.current += 1;
      // A password sign-in is not a provider session, so a marker left by an
      // earlier hosted sign-in in this tab must not outlive it.
      forgetHostedProvider();
      await adoptIdentity(user.userId);
      sessionCredentials.adopt(user);
      setState({ status: 'authenticated', user });
      return user;
    },
    [adoptIdentity, client],
  );

  /**
   * Ends the session everywhere it exists, in that order: the Vistara session
   * is revoked by the server first, this device forgets everything the account
   * left behind, and only then is the browser sent to the provider to end the
   * provider session. A tab that never signed in with a provider, or a
   * deployment that publishes none, signs out locally and stays here.
   */
  const signOut = useCallback(async () => {
    request.current += 1;
    const hostedProvider = client.signOutFromProvider
      ? readHostedProvider()
      : undefined;
    let confirmed = true;
    let endSessionUrl: string | undefined;
    try {
      if (hostedProvider) {
        const hosted = await client.signOutFromProvider!(hostedProvider);
        endSessionUrl = providerSignOutUrl(hosted.endSessionUrl);
      } else {
        await client.logout();
      }
    } catch {
      // A provider sign-out that did not answer must not leave the Vistara
      // session behind it, so the local revocation is attempted on its own.
      // The browser session ends on this device either way, but the server may
      // still hold the cookie, so the visitor is told.
      endSessionUrl = undefined;
      try {
        await client.logout();
      } catch {
        confirmed = false;
      }
    }

    forgetHostedProvider();
    setSignOutIncomplete(!confirmed);
    cachedIdentity.current = undefined;
    // Whether or not the server confirmed it, nothing of this session is spent
    // from this device again.
    sessionCredentials.clear();
    setState({ status: 'anonymous' });
    await endAccount();
    if (endSessionUrl) {
      onLeaveForProvider(endSessionUrl);
    }
  }, [client, endAccount, onLeaveForProvider]);

  const value = useMemo<SessionContextValue>(() => {
    const membership = activeMembership(state.user);

    return {
      status: state.status,
      user: state.user,
      membership,
      role: membership?.role,
      credentialKind:
        state.status === 'preview' ? 'cookie' : credentialKind(state.user),
      scopes:
        state.status === 'preview' ? previewScopes : sessionScopes(state.user),
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
