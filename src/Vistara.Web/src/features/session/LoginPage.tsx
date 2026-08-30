import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { VistaraApiError } from '../../api/generated/client';
import type { Capabilities } from '../../api/platform';
import { BrandMark } from '../../brand';
import { Skeleton } from '../../components';
import { useSession } from './sessionContext';
import styles from './LoginPage.module.css';

export interface CapabilitiesClient {
  getCapabilities(): Promise<Capabilities>;
}

interface LoginPageProps {
  readonly capabilities: CapabilitiesClient;
}

interface FieldErrors {
  readonly email?: string;
  readonly password?: string;
}

export function LoginPage({ capabilities }: LoginPageProps) {
  const session = useSession();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const destination = safeDestination(searchParams.get('returnTo'));

  const [deployment, setDeployment] = useState<Capabilities>();
  const [capabilitiesFailed, setCapabilitiesFailed] = useState(false);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const emailField = useRef<HTMLInputElement>(null);
  const passwordField = useRef<HTMLInputElement>(null);

  useEffect(() => {
    let active = true;
    void capabilities.getCapabilities().then(
      (value) => {
        if (active) {
          setDeployment(value);
        }
      },
      () => {
        if (active) {
          setCapabilitiesFailed(true);
        }
      },
    );

    return () => {
      active = false;
    };
  }, [capabilities]);

  if (session.status === 'authenticated') {
    return <Navigate replace to={destination} />;
  }

  const authentication = deployment?.authentication;
  const localAccounts = authentication?.localAccounts !== false;
  const oidc = authentication?.oidc;
  const pending = deployment === undefined && !capabilitiesFailed;

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const trimmedEmail = email.trim();
    const errors: FieldErrors = {
      ...(trimmedEmail
        ? {}
        : { email: 'Enter the email address for your account.' }),
      ...(password ? {} : { password: 'Enter your password.' }),
    };

    setFieldErrors(errors);
    setFailure('');
    if (errors.email || errors.password) {
      (errors.email ? emailField : passwordField).current?.focus();
      return;
    }

    setSubmitting(true);
    try {
      await session.signIn({ email: trimmedEmail, password, rememberMe });
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

        {localAccounts ? (
          <form className={styles.form} noValidate onSubmit={(event) => void submit(event)}>
            <div className={styles.field}>
              <label htmlFor="login-email">Email address</label>
              <input
                autoComplete="username"
                aria-describedby={
                  fieldErrors.email ? 'login-email-error' : undefined
                }
                aria-invalid={fieldErrors.email ? true : undefined}
                id="login-email"
                inputMode="email"
                name="email"
                ref={emailField}
                type="email"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
              />
              {fieldErrors.email ? (
                <p className={styles.fieldError} id="login-email-error">
                  {fieldErrors.email}
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

            <label className={styles.remember}>
              <input
                checked={rememberMe}
                name="rememberMe"
                type="checkbox"
                onChange={(event) => setRememberMe(event.target.checked)}
              />
              Keep me signed in
            </label>

            <button
              className={styles.submit}
              disabled={submitting}
              type="submit"
            >
              {submitting ? 'Signing in…' : 'Sign in'}
            </button>
            <p className={styles.progress} role="status" aria-live="polite">
              {submitting ? 'Checking your credentials…' : ''}
            </p>
          </form>
        ) : null}

        {pending ? (
          <Skeleton count={1} shape="row" />
        ) : null}

        {oidc ? (
          <a className={styles.federated} href={oidc.startPath} rel="nofollow">
            Continue with {oidc.displayName}
          </a>
        ) : null}

        {!pending && !localAccounts && !oidc ? (
          <p className={styles.description}>
            No sign-in method is configured for this deployment. Ask the
            administrator to enable local accounts or a single sign-on provider.
          </p>
        ) : null}

        {capabilitiesFailed ? (
          <p className={styles.hint}>
            Sign-in options could not be loaded, so only local accounts are
            shown.
          </p>
        ) : null}
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

/** Only same-origin absolute paths are followed after sign-in. */
function safeDestination(value: string | null): string {
  if (!value || !value.startsWith('/') || value.startsWith('//')) {
    return '/library';
  }

  return value;
}
