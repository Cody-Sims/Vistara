import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { VistaraApiError } from '../../api/generated/client';
import type { PlatformApiClient } from '../../api/platform';
import { describeRetryAfter, VistaraThrottledError } from '../../api/platform';
import { BrandMark } from '../../brand';
import { safeDestination } from './safeDestination';
import { useSession } from './sessionContext';
import styles from './LoginPage.module.css';

interface FieldErrors {
  readonly login?: string;
  readonly password?: string;
}

export type LoginSetupClient = Pick<PlatformApiClient, 'getSetupState'>;

interface LoginPageProps {
  /** Reads whether first-run setup is still open; omitted when unavailable. */
  readonly setup?: LoginSetupClient;
}

export function LoginPage({ setup }: LoginPageProps = {}) {
  const session = useSession();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const destination = safeDestination(searchParams.get('returnTo'));

  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const loginField = useRef<HTMLInputElement>(null);
  const passwordField = useRef<HTMLInputElement>(null);
  const [setupState, setSetupState] = useState<'open' | 'closed' | 'unknown'>(
    'closed',
  );
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

        {failure ? (
          <p className={styles.failure} role="alert">
            {failure}
          </p>
        ) : null}

        {session.signOutIncomplete ? (
          <p className={styles.failure} role="alert">
            Signing out was not confirmed by the server. This device is signed
            out, but close the browser or sign in again to be sure the session
            ended.
          </p>
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
