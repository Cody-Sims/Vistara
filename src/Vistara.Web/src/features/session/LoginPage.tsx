import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { VistaraApiError } from '../../api/generated/client';
import type { PlatformApiClient, SignInProvider } from '../../api/platform';
import { describeRetryAfter, VistaraThrottledError } from '../../api/platform';
import { BrandMark } from '../../brand';
import { hostedStartUrl, rememberHostedProvider } from './hostedSignIn';
import { safeDestination } from './safeDestination';
import { useSession } from './sessionContext';
import styles from './LoginPage.module.css';

interface FieldErrors {
  readonly login?: string;
  readonly password?: string;
}

/**
 * The one failure the hosted sign-in redirect reports. The server answers the
 * same code for a cancelled consent, a replayed request, and a refused
 * directory alike, so this page has nothing more specific to say and must not
 * invent it.
 */
const hostedFailureCode = 'oidc_sign_in_failed';

const hostedFailureMessage =
  'Signing in with your identity provider did not finish. ' +
  'Try again, or sign in with your Vistara password.';

/** One provider control, with the path a browser navigation may start at. */
interface HostedProvider {
  readonly id: string;
  readonly displayName: string;
  readonly startUrl: string;
}

/**
 * Keeps the providers this page can actually navigate to. A published entry
 * that is missing a label, or whose start URL is not a same-origin path, is
 * dropped rather than rendered as a control that would leave this origin.
 */
function publishedProviders(
  providers: readonly SignInProvider[] | undefined,
  destination: string,
): readonly HostedProvider[] {
  if (!Array.isArray(providers)) {
    return [];
  }

  return providers.flatMap((provider) => {
    const startUrl =
      typeof provider?.startUrl === 'string'
        ? hostedStartUrl(provider.startUrl, destination)
        : undefined;
    const displayName =
      typeof provider?.displayName === 'string'
        ? provider.displayName.trim()
        : '';
    return startUrl && displayName && typeof provider.id === 'string'
      ? [{ id: provider.id, displayName, startUrl }]
      : [];
  });
}

export type LoginSetupClient = Pick<PlatformApiClient, 'getSetupState'>;

interface LoginPageProps {
  /** Reads whether first-run setup is still open; omitted when unavailable. */
  readonly setup?: LoginSetupClient;
}

export function LoginPage({ setup }: LoginPageProps = {}) {
  const session = useSession();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const destination = safeDestination(searchParams.get('returnTo'));

  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState('');
  /**
   * Whether this page was reached by a hosted sign-in that failed. It is read
   * once, on the render that arrived with the code, so the message survives
   * the code being taken out of the address bar.
   */
  const [hostedFailure, setHostedFailure] = useState(
    () => searchParams.get('error') === hostedFailureCode,
  );
  const [submitting, setSubmitting] = useState(false);
  const [starting, setStarting] = useState<string>();
  const loginField = useRef<HTMLInputElement>(null);
  const passwordField = useRef<HTMLInputElement>(null);
  const failureMessage = useRef<HTMLParagraphElement>(null);
  const [setupState, setSetupState] = useState<'open' | 'closed' | 'unknown'>(
    'closed',
  );
  const [providers, setProviders] = useState<readonly SignInProvider[]>([]);
  const [setupThrottled, setSetupThrottled] = useState<string>();

  useEffect(() => {
    if (!setup) {
      return;
    }

    let active = true;
    void setup.getSetupState().then(
      (state) => {
        if (active) {
          setSetupState(state.available === true ? 'open' : 'closed');
          setProviders(state.signInProviders ?? []);
        }
      },
      (error: unknown) => {
        // A deployment that does not answer cannot say whether it has an owner,
        // so the way to first-run setup stays visible with that caveat.
        if (!active) {
          return;
        }

        setSetupState('unknown');
        if (error instanceof VistaraThrottledError) {
          setSetupThrottled(describeRetryAfter(error.retryAfterSeconds));
        }
      },
    );

    return () => {
      active = false;
    };
  }, [setup]);

  /**
   * The failure is announced once and the code is then taken out of the
   * address bar, so a reload, a shared link, or a screen reader revisiting the
   * page does not repeat a failure that already happened. The return target is
   * left alone.
   */
  useEffect(() => {
    if (!hostedFailure || !searchParams.has('error')) {
      return;
    }

    const remaining = new URLSearchParams(searchParams);
    remaining.delete('error');
    setSearchParams(remaining, { replace: true });
  }, [hostedFailure, searchParams, setSearchParams]);

  useEffect(() => {
    if (hostedFailure) {
      failureMessage.current?.focus();
    }
  }, [hostedFailure]);

  const announced = failure || (hostedFailure ? hostedFailureMessage : '');

  const hostedProviders = publishedProviders(providers, destination);

  if (session.status === 'authenticated') {
    return <Navigate replace to={destination} />;
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const trimmedLogin = login.trim();
    const errors: FieldErrors = {
      ...(trimmedLogin
        ? {}
        : { login: 'Enter the email address or user name for your account.' }),
      ...(password ? {} : { password: 'Enter your password.' }),
    };

    setFieldErrors(errors);
    setFailure('');
    setHostedFailure(false);
    if (errors.login || errors.password) {
      (errors.login ? loginField : passwordField).current?.focus();
      return;
    }

    setSubmitting(true);
    try {
      await session.signIn({ login: trimmedLogin, password });
      setPassword('');
      await navigate(destination, { replace: true });
    } catch (error) {
      setPassword('');
      setFailure(describeFailure(error));
      passwordField.current?.focus();
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className={styles.page} id="main-content">
      <section className={styles.card} aria-labelledby="login-heading">
        <BrandMark className={styles.mark} title="Vistara" />
        <p className={styles.eyebrow}>Vistara</p>
        <h1 id="login-heading">Sign in</h1>
        <p className={styles.description}>
          Your library, originals, and share links stay on the server you
          control.
        </p>

        {announced ? (
          <p
            className={styles.failure}
            ref={failureMessage}
            role="alert"
            tabIndex={hostedFailure ? -1 : undefined}
          >
            {announced}
          </p>
        ) : null}

        {session.signOutIncomplete ? (
          <p className={styles.failure} role="alert">
            Signing out was not confirmed by the server. This device is signed
            out, but close the browser or sign in again to be sure the session
            ended.
          </p>
        ) : null}

        {hostedProviders.length > 0 ? (
          <div className={styles.providers}>
            {hostedProviders.map((provider) => (
              <a
                className={styles.federated}
                href={provider.startUrl}
                key={provider.id}
                aria-busy={starting === provider.id || undefined}
                onClick={() => {
                  // The provider is recorded before the browser leaves, so
                  // signing out later can end the provider session too. It is
                  // a published route segment and nothing else.
                  rememberHostedProvider(provider.id);
                  setStarting(provider.id);
                }}
              >
                <ProviderMark providerId={provider.id} />
                Sign in with {provider.displayName}
              </a>
            ))}
            <p className={styles.progress} role="status" aria-live="polite">
              {starting
                ? `Taking you to ${
                    hostedProviders.find((provider) => provider.id === starting)
                      ?.displayName ?? 'your identity provider'
                  }…`
                : ''}
            </p>
            <p className={styles.divider}>
              <span>or sign in with your Vistara password</span>
            </p>
          </div>
        ) : null}

        <form
          className={styles.form}
          noValidate
          onSubmit={(event) => void submit(event)}
        >
          <div className={styles.field}>
            <label htmlFor="login-name">Email address or user name</label>
            <input
              autoComplete="username"
              aria-describedby={
                fieldErrors.login ? 'login-name-error' : undefined
              }
              aria-invalid={fieldErrors.login ? true : undefined}
              id="login-name"
              name="login"
              ref={loginField}
              type="text"
              value={login}
              onChange={(event) => setLogin(event.target.value)}
            />
            {fieldErrors.login ? (
              <p className={styles.fieldError} id="login-name-error">
                {fieldErrors.login}
              </p>
            ) : null}
          </div>

          <div className={styles.field}>
            <label htmlFor="login-password">Password</label>
            <input
              autoComplete="current-password"
              aria-describedby={
                fieldErrors.password ? 'login-password-error' : undefined
              }
              aria-invalid={fieldErrors.password ? true : undefined}
              id="login-password"
              name="password"
              ref={passwordField}
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
            {fieldErrors.password ? (
              <p className={styles.fieldError} id="login-password-error">
                {fieldErrors.password}
              </p>
            ) : null}
          </div>

          <button className={styles.submit} disabled={submitting} type="submit">
            {submitting ? 'Signing in…' : 'Sign in'}
          </button>
          <p className={styles.progress} role="status" aria-live="polite">
            {submitting ? 'Checking your credentials…' : ''}
          </p>
        </form>

        {setupState === 'open' ? (
          <p className={styles.hint}>
            This deployment has no owner yet.{' '}
            <Link className={styles.inlineLink} to="/setup">
              Set up Vistara
            </Link>{' '}
            to create the first workspace.
          </p>
        ) : null}

        {setupState === 'unknown' ? (
          <p className={styles.hint}>
            {setupThrottled
              ? `This deployment is answering setup checks slowly. ${setupThrottled} `
              : 'This deployment does not report whether it has an owner. '}
            If it is new,{' '}
            <Link className={styles.inlineLink} to="/setup">
              set up Vistara
            </Link>
            ; an existing workspace will say so.
          </p>
        ) : null}

        <p className={styles.hint}>
          The session lives in a secure cookie on this server. Nothing about
          your account is stored in the browser.
        </p>

      </section>
    </main>
  );
}

/**
 * The mark on a provider control. Only the provider Vistara actually hosts is
 * drawn; anything else is a plain label, because a deployment-supplied key
 * must never select a logo that claims an identity it did not prove.
 */
function ProviderMark({ providerId }: { readonly providerId: string }) {
  if (providerId !== 'entra') {
    return null;
  }

  return (
    <svg
      className={styles.providerMark}
      viewBox="0 0 20 20"
      aria-hidden="true"
      focusable="false"
    >
      <rect x="1" y="1" width="8" height="8" fill="#f25022" />
      <rect x="11" y="1" width="8" height="8" fill="#7fba00" />
      <rect x="1" y="11" width="8" height="8" fill="#00a4ef" />
      <rect x="11" y="11" width="8" height="8" fill="#ffb900" />
    </svg>
  );
}

function describeFailure(error: unknown): string {
  if (error instanceof VistaraApiError) {
    if (error.status === 401 || error.status === 400) {
      return 'Check your email address and password.';
    }

    if (error.status === 423 || error.status === 403) {
      return 'This account cannot sign in right now. Contact an administrator.';
    }

    if (error.status === 429) {
      return 'Too many sign-in attempts. Wait a moment and try again.';
    }
  }

  return 'Sign-in is unavailable right now. Check your connection and try again.';
}
