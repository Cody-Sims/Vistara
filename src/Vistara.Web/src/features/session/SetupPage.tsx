import { useRef, useState, type FormEvent } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { VistaraApiError } from '../../api/generated/client';
import type {
  PlatformApiClient,
  ProvisionedOwner,
} from '../../api/platform';
import { BrandMark } from '../../brand';
import { useSession } from './sessionContext';
import { isValidSlug, slugify } from './workspaceSlug';
import styles from './LoginPage.module.css';

export type SetupClient = Pick<PlatformApiClient, 'provisionFirstOwner'>;

interface SetupPageProps {
  readonly client: SetupClient;
}

interface FieldErrors {
  tenantName?: string;
  tenantSlug?: string;
  displayName?: string;
  email?: string;
  password?: string;
  confirmation?: string;
}

type Outcome =
  | { readonly kind: 'editing' }
  | { readonly kind: 'submitting' }
  | { readonly kind: 'provisioned'; readonly owner: ProvisionedOwner };

const minimumPasswordLength = 12;
const fieldOrder = [
  'tenantName',
  'tenantSlug',
  'displayName',
  'email',
  'password',
  'confirmation',
] as const;

export function SetupPage({ client }: SetupPageProps) {
  const session = useSession();
  const navigate = useNavigate();

  const [tenantName, setTenantName] = useState('');
  const [tenantSlug, setTenantSlug] = useState('');
  const [slugEdited, setSlugEdited] = useState(false);
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [revealed, setRevealed] = useState(false);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState('');
  const [signedIn, setSignedIn] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>({ kind: 'editing' });

  const tenantNameField = useRef<HTMLInputElement>(null);
  const tenantSlugField = useRef<HTMLInputElement>(null);
  const displayNameField = useRef<HTMLInputElement>(null);
  const emailField = useRef<HTMLInputElement>(null);
  const passwordField = useRef<HTMLInputElement>(null);
  const confirmationField = useRef<HTMLInputElement>(null);

  if (session.status === 'authenticated' && !signedIn) {
    return <Navigate replace to="/library" />;
  }

  function focusFirst(problems: FieldErrors) {
    const first = fieldOrder.find((name) => problems[name]);
    if (!first) {
      return;
    }

    const targets = {
      tenantName: tenantNameField,
      tenantSlug: tenantSlugField,
      displayName: displayNameField,
      email: emailField,
      password: passwordField,
      confirmation: confirmationField,
    };
    targets[first].current?.focus();
  }

  /** Clears both password fields; they are never read again after submit. */
  function forgetPasswords() {
    setPassword('');
    setConfirmation('');
    setRevealed(false);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const slug = (slugEdited ? tenantSlug : slugify(tenantName)).trim();
    const problems: FieldErrors = {
      ...(tenantName.trim() ? {} : { tenantName: 'Enter a name for this workspace.' }),
      ...(slug
        ? isValidSlug(slug)
          ? {}
          : {
              tenantSlug:
                'Use lowercase letters, numbers, and hyphens between them.',
            }
        : { tenantSlug: 'Enter an address for this workspace.' }),
      ...(displayName.trim() ? {} : { displayName: 'Enter your name.' }),
      ...(email.trim() && email.includes('@')
        ? {}
        : { email: 'Enter the email address for the owner account.' }),
      ...(password.length >= minimumPasswordLength
        ? {}
        : {
            password: `Use a password of at least ${minimumPasswordLength} characters.`,
          }),
      ...(password && password === confirmation
        ? {}
        : password.length >= minimumPasswordLength
          ? { confirmation: 'Both passwords must match.' }
          : {}),
    };

    setErrors(problems);
    setFailure('');
    if (Object.keys(problems).length > 0) {
      focusFirst(problems);
      return;
    }

    setOutcome({ kind: 'submitting' });
    const submitted = password;
    try {
      const owner = await client.provisionFirstOwner({
        tenantSlug: slug,
        tenantName: tenantName.trim(),
        displayName: displayName.trim(),
        email: email.trim(),
        password: submitted,
      });

      setOutcome({ kind: 'provisioned', owner });
      try {
        await session.signIn({ login: owner.email, password: submitted });
        forgetPasswords();
        setSignedIn(true);
        await navigate('/library', { replace: true });
      } catch {
        forgetPasswords();
      }
    } catch (error) {
      forgetPasswords();
      setOutcome({ kind: 'editing' });
      const problem =
        error instanceof VistaraApiError ? error.problem : undefined;

      if (problem?.code === 'setup.already_provisioned') {
        setFailure(
          'This deployment already has an owner, so it cannot be set up again. Sign in with the owner account instead.',
        );
        return;
      }

      if (problem?.code === 'setup.provisioning_contended') {
        setFailure(
          'Another setup is already running on this deployment. Wait a moment and try again.',
        );
        return;
      }

      if (problem?.code === 'setup.weak_password') {
        setFailure(
          problem.detail ??
            'Choose a longer password this deployment accepts, then try again.',
        );
        setErrors({ password: 'Choose a longer password and re-enter it.' });
        passwordField.current?.focus();
        return;
      }

      const fieldProblems = readFieldErrors(problem?.errors);
      if (Object.keys(fieldProblems).length > 0) {
        setErrors(fieldProblems);
        focusFirst(fieldProblems);
        return;
      }

      setFailure(
        'The workspace could not be created. Nothing was changed; check the deployment and try again.',
      );
    }
  }

  if (outcome.kind === 'provisioned' && !signedIn) {
    return (
      <main className={styles.page} id="main-content">
        <section className={styles.card} aria-labelledby="setup-ready">
          <BrandMark className={styles.mark} title="Vistara" />
          <p className={styles.eyebrow}>Vistara</p>
          <h1 id="setup-ready">Workspace ready</h1>
          <p className={styles.description}>
            {outcome.owner.tenantName} was created and {outcome.owner.email} owns
            it. Signing in from here did not complete, so sign in to continue.
          </p>
          <Link className={styles.federated} to="/login">
            Go to sign in
          </Link>
        </section>
      </main>
    );
  }

  const busy = outcome.kind === 'submitting';

  return (
    <main className={styles.page} id="main-content">
      <section className={styles.card} aria-labelledby="setup-heading">
        <BrandMark className={styles.mark} title="Vistara" />
        <p className={styles.eyebrow}>First run</p>
        <h1 id="setup-heading">Set up Vistara</h1>
        <p className={styles.description}>
          Create the first workspace and its owner. Everything stays on this
          server; nothing is sent anywhere else.
        </p>

        {failure ? (
          <div className={styles.failure} role="alert">
            <p>{failure}</p>
            <Link className={styles.inlineLink} to="/login">
              Go to sign in
            </Link>
          </div>
        ) : null}

        <form
          className={styles.form}
          noValidate
          onSubmit={(event) => void submit(event)}
        >
          <Field
            error={errors.tenantName}
            id="setup-tenant-name"
            label="Workspace name"
            inputRef={tenantNameField}
            hint="Shown across the gallery, for example “Harbour Lights Studio”."
            value={tenantName}
            onChange={(value) => {
              setTenantName(value);
              if (!slugEdited) {
                setTenantSlug(slugify(value));
              }
            }}
          />

          <Field
            error={errors.tenantSlug}
            id="setup-tenant-slug"
            label="Workspace address"
            inputRef={tenantSlugField}
            hint="Lowercase letters, numbers, and hyphens. Used in links and API paths."
            value={tenantSlug}
            onChange={(value) => {
              setSlugEdited(true);
              setTenantSlug(value);
            }}
          />

          <Field
            error={errors.displayName}
            id="setup-display-name"
            label="Your name"
            inputRef={displayNameField}
            autoComplete="name"
            value={displayName}
            onChange={setDisplayName}
          />

          <Field
            error={errors.email}
            id="setup-email"
            label="Email address"
            inputRef={emailField}
            autoComplete="username"
            type="email"
            value={email}
            onChange={setEmail}
          />

          <Field
            error={errors.password}
            id="setup-password"
            label="Password"
            inputRef={passwordField}
            autoComplete="new-password"
            hint={`At least ${minimumPasswordLength} characters. Use a phrase you can remember; it is never stored in this browser.`}
            type={revealed ? 'text' : 'password'}
            value={password}
            onChange={setPassword}
            action={
              <button
                aria-pressed={revealed}
                className={styles.reveal}
                type="button"
                onClick={() => setRevealed((current) => !current)}
              >
                {revealed ? 'Hide password' : 'Show password'}
              </button>
            }
          />

          <Field
            error={errors.confirmation}
            id="setup-confirmation"
            label="Confirm password"
            inputRef={confirmationField}
            autoComplete="new-password"
            type={revealed ? 'text' : 'password'}
            value={confirmation}
            onChange={setConfirmation}
          />

          <button className={styles.submit} disabled={busy} type="submit">
            {busy ? 'Creating…' : 'Create workspace and owner'}
          </button>
          <p className={styles.progress} role="status" aria-live="polite">
            {busy ? 'Creating the workspace and owner…' : ''}
          </p>
        </form>

        <p className={styles.hint}>
          Already set up? <Link to="/login">Sign in</Link> instead.
        </p>
      </section>
    </main>
  );
}

interface FieldProps {
  readonly action?: React.ReactNode;
  readonly autoComplete?: string;
  readonly error?: string;
  readonly hint?: string;
  readonly id: string;
  readonly inputRef: React.RefObject<HTMLInputElement | null>;
  readonly label: string;
  readonly type?: string;
  readonly value: string;
  onChange(value: string): void;
}

function Field({
  action,
  autoComplete,
  error,
  hint,
  id,
  inputRef,
  label,
  onChange,
  type = 'text',
  value,
}: FieldProps) {
  const described = [hint ? `${id}-hint` : '', error ? `${id}-error` : '']
    .filter(Boolean)
    .join(' ');

  return (
    <div className={styles.field}>
      <label htmlFor={id}>{label}</label>
      <div className={action ? styles.fieldRow : undefined}>
        <input
          {...(autoComplete ? { autoComplete } : {})}
          aria-describedby={described || undefined}
          aria-invalid={error ? true : undefined}
          id={id}
          name={id}
          ref={inputRef}
          type={type}
          value={value}
          onChange={(event) => onChange(event.target.value)}
        />
        {action}
      </div>
      {hint ? (
        <p className={styles.fieldHint} id={`${id}-hint`}>
          {hint}
        </p>
      ) : null}
      {error ? (
        <p className={styles.fieldError} id={`${id}-error`}>
          {error}
        </p>
      ) : null}
    </div>
  );
}

function readFieldErrors(
  errors: Readonly<Record<string, readonly string[]>> | undefined,
): FieldErrors {
  const problems: FieldErrors = {};
  for (const [field, messages] of Object.entries(errors ?? {})) {
    const message = messages[0];
    if (!message) {
      continue;
    }

    if (fieldOrder.includes(field as (typeof fieldOrder)[number])) {
      problems[field as keyof FieldErrors] = message;
    }
  }

  return problems;
}
