import { useEffect, useId, useRef, useState } from 'react';
import {
  VistaraApiError,
  type VistaraApiClient,
} from '../../api/generated';
import type {
  CreateShareRequest,
  ShareSummary,
  VersionedAssetReference,
} from '../../api/generated/models';
import styles from './Shares.module.css';
import { versionTag } from '../../api/versionTag';

export type ShareCreationTarget =
  | { readonly kind: 'album'; readonly albumId: string }
  | {
      readonly kind: 'snapshot';
      readonly assets: readonly VersionedAssetReference[];
    };

export interface ShareManagerProps {
  readonly client: Pick<
    VistaraApiClient,
    'listShares' | 'createShare' | 'revokeShare'
  >;
  readonly target: ShareCreationTarget;
  readonly now?: () => Date;
  readonly createIdempotencyKey?: () => string;
  readonly buildPublicUrl?: (token: string) => string;
}

const defaultNow = () => new Date();
const defaultIdempotencyKey = () => crypto.randomUUID();
const defaultPublicUrl = (token: string) =>
  new URL(`/s/${encodeURIComponent(token)}`, window.location.origin).toString();

export function ShareManager({
  client,
  target,
  now = defaultNow,
  createIdempotencyKey = defaultIdempotencyKey,
  buildPublicUrl = defaultPublicUrl,
}: ShareManagerProps) {
  const [shares, setShares] = useState<readonly ShareSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<string>();
  const [formOpen, setFormOpen] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [name, setName] = useState('');
  const [downloadRenditions, setDownloadRenditions] = useState(true);
  const [downloadOriginal, setDownloadOriginal] = useState(false);
  const [metadataExposure, setMetadataExposure] = useState<'none' | 'basic'>(
    'none',
  );
  const [expiresAt, setExpiresAt] = useState('');
  const [password, setPassword] = useState('');
  const [oneTimeUrl, setOneTimeUrl] = useState<string>();
  const [revokeTarget, setRevokeTarget] = useState<ShareSummary>();
  const createButtonRef = useRef<HTMLButtonElement>(null);
  const revokeButtonRef = useRef<HTMLButtonElement>(null);
  const revokeDialogRef = useRef<HTMLDivElement>(null);
  const headingId = useId();

  async function loadShares() {
    setLoading(true);
    try {
      const response = await client.listShares();
      setShares(response.data.items);
      setError(undefined);
    } catch (caught) {
      setError(messageFor(caught, 'Share links could not be loaded.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    let active = true;
    void client
      .listShares()
      .then((response) => {
        if (active) {
          setShares(response.data.items);
          setError(undefined);
        }
      })
      .catch((caught: unknown) => {
        if (active) {
          setError(messageFor(caught, 'Share links could not be loaded.'));
        }
      })
      .finally(() => {
        if (active) {
          setLoading(false);
        }
      });
    return () => {
      active = false;
    };
  }, [client]);

  async function submitShare(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(undefined);
    const request: CreateShareRequest = {
      name: name.trim(),
      targetKind: target.kind,
      ...(target.kind === 'album'
        ? { albumId: target.albumId }
        : { snapshotAssets: target.assets }),
      permissions: {
        view: true,
        downloadRenditions,
        downloadOriginal,
      },
      metadataExposure,
      ...(expiresAt
        ? { expiresAt: new Date(expiresAt).toISOString() }
        : undefined),
      ...(password ? { password } : undefined),
    };

    try {
      const response = await client.createShare(request, {
        idempotencyKey: createIdempotencyKey(),
      });
      setShares((current) => [response.data.share.share, ...current]);
      setOneTimeUrl(buildPublicUrl(response.data.publicToken));
      setPassword('');
      setStatus(
        'Link created. Copy it now; the bearer token will not be shown again.',
      );
    } catch (caught) {
      setError(messageFor(caught, 'The share link could not be created.'));
    } finally {
      setSubmitting(false);
    }
  }

  async function copyOneTimeLink() {
    if (!oneTimeUrl) {
      return;
    }
    try {
      await navigator.clipboard.writeText(oneTimeUrl);
      setOneTimeUrl(undefined);
      setStatus('Link copied. Vistara no longer displays this token.');
    } catch {
      setError(
        'The link was not copied. It remains visible so you can copy it manually.',
      );
    }
  }

  function discardOneTimeLink() {
    setOneTimeUrl(undefined);
    setStatus('One-time link display closed. The token cannot be shown again.');
  }

  async function confirmRevocation() {
    if (!revokeTarget) {
      return;
    }
    const targetToRevoke = revokeTarget;
    setSubmitting(true);
    setError(undefined);
    try {
      await client.revokeShare(targetToRevoke.id, {
        idempotencyKey: createIdempotencyKey(),
        ifMatch: versionTag(targetToRevoke.version),
      });
      setShares((current) =>
        current.map((item) =>
          item.id === targetToRevoke.id
            ? {
                ...item,
                status: 'revoked',
                revokedAt: now().toISOString(),
              }
            : item,
        ),
      );
      setStatus(`“${targetToRevoke.name}” was revoked.`);
      setRevokeTarget(undefined);
      queueMicrotask(() => revokeButtonRef.current?.focus());
    } catch (caught) {
      if (caught instanceof VistaraApiError && caught.status === 412) {
        setRevokeTarget(undefined);
        await loadShares();
        setError(
          'This link changed elsewhere. The latest version has been loaded.',
        );
      } else {
        setError(messageFor(caught, 'The share link could not be revoked.'));
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className={styles.feature} aria-labelledby={headingId}>
      <header className={styles.header}>
        <div>
          <p className={styles.eyebrow}>Sharing</p>
          <h1 id={headingId}>Share links</h1>
          <p>
            Create revocable links with explicit download, metadata, password,
            and expiry policies.
          </p>
        </div>
        <button
          className={styles.primaryButton}
          type="button"
          ref={createButtonRef}
          onClick={() => setFormOpen(true)}
        >
          Create share link
        </button>
      </header>

      {error ? <p role="alert" className={styles.error}>{error}</p> : null}
      {status ? <p role="status" className={styles.status}>{status}</p> : null}

      {formOpen ? (
        <form className={styles.panel} onSubmit={submitShare}>
          <div className={styles.panelHeading}>
            <div>
              <h2>Create a share link</h2>
              <p>The link bearer can access only the permissions shown below.</p>
            </div>
            <button
              type="button"
              className={styles.secondaryButton}
              onClick={() => {
                setFormOpen(false);
                createButtonRef.current?.focus();
              }}
            >
              Cancel
            </button>
          </div>

          <label className={styles.field}>
            <span>Link name</span>
            <input
              required
              maxLength={120}
              autoFocus
              value={name}
              onChange={(event) => setName(event.target.value)}
            />
          </label>

          <fieldset className={styles.fieldset}>
            <legend>Downloads</legend>
            <label className={styles.check}>
              <input
                type="checkbox"
                checked={downloadRenditions}
                onChange={(event) =>
                  setDownloadRenditions(event.target.checked)
                }
              />
              Allow display-size downloads
            </label>
            <label className={styles.check}>
              <input
                type="checkbox"
                checked={downloadOriginal}
                onChange={(event) => {
                  setDownloadOriginal(event.target.checked);
                  if (event.target.checked) {
                    setDownloadRenditions(true);
                  }
                }}
              />
              Allow original downloads
            </label>
          </fieldset>

          <label className={styles.check}>
            <input
              type="checkbox"
              checked={metadataExposure === 'basic'}
              onChange={(event) =>
                setMetadataExposure(event.target.checked ? 'basic' : 'none')
              }
            />
            Expose basic metadata
          </label>
          <p className={styles.help}>
            Basic metadata can include title, description, capture date, and
            dimensions. Restricted metadata is never included.
          </p>

          <div className={styles.twoColumns}>
            <label className={styles.field}>
              <span>Expires (optional)</span>
              <input
                type="datetime-local"
                min={toLocalDateTime(now())}
                value={expiresAt}
                onChange={(event) => setExpiresAt(event.target.value)}
              />
            </label>
            <label className={styles.field}>
              <span>Password (optional)</span>
              <input
                type="password"
                autoComplete="new-password"
                minLength={12}
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
            </label>
          </div>

          <aside
            role="note"
            aria-label="Share policy summary"
            className={styles.policy}
          >
            <strong>Policy shown to recipients</strong>
            <span>
              Display downloads: {downloadRenditions ? 'allowed' : 'blocked'}
            </span>
            <span>
              Original downloads: {downloadOriginal ? 'allowed' : 'blocked'}
            </span>
            <span>
              Basic metadata:{' '}
              {metadataExposure === 'basic' ? 'exposed' : 'hidden'}
            </span>
            <span>Password: {password ? 'required' : 'not required'}</span>
            <span>
              Expiry:{' '}
              {expiresAt
                ? formatDate(new Date(expiresAt))
                : 'no automatic expiry'}
            </span>
          </aside>

          <button
            type="submit"
            className={styles.primaryButton}
            disabled={submitting || !name.trim()}
          >
            {submitting ? 'Creating…' : 'Create link'}
          </button>
        </form>
      ) : null}

      {oneTimeUrl ? (
        <section className={styles.secretPanel} aria-labelledby="copy-link-title">
          <h2 id="copy-link-title">Copy this link now</h2>
          <p>
            This bearer link is displayed once. Do not send it through an
            untrusted channel.
          </p>
          <input
            className={styles.secretInput}
            readOnly
            aria-label="New share link"
            value={oneTimeUrl}
            onFocus={(event) => event.currentTarget.select()}
          />
          <div className={styles.actions}>
            <button
              type="button"
              className={styles.primaryButton}
              onClick={copyOneTimeLink}
            >
              Copy share link
            </button>
            <button
              type="button"
              className={styles.secondaryButton}
              onClick={discardOneTimeLink}
            >
              Close without copying
            </button>
          </div>
        </section>
      ) : null}

      <section aria-labelledby="existing-links-heading">
        <div className={styles.listHeading}>
          <h2 id="existing-links-heading">Managed links</h2>
          <button
            type="button"
            className={styles.secondaryButton}
            onClick={loadShares}
            disabled={loading}
          >
            Refresh
          </button>
        </div>
        {loading ? <p role="status">Loading share links…</p> : null}
        {!loading && shares.length === 0 ? <p>No share links yet.</p> : null}
        <div className={styles.cardList}>
          {shares.map((item) => (
            <article
              key={item.id}
              className={styles.card}
              aria-label={item.name}
            >
              <div className={styles.cardHeading}>
                <h3>{item.name}</h3>
                <span className={styles.badge} data-status={item.status}>
                  {titleCase(item.status)}
                </span>
              </div>
              <dl className={styles.details}>
                <div>
                  <dt>Content</dt>
                  <dd>
                    {item.target.assetCount}{' '}
                    {item.target.assetCount === 1 ? 'asset' : 'assets'}
                  </dd>
                </div>
                <div>
                  <dt>Downloads</dt>
                  <dd>{downloadLabel(item)}</dd>
                </div>
                <div>
                  <dt>Metadata</dt>
                  <dd>
                    {item.metadataExposure === 'basic' ? 'Basic' : 'Hidden'}
                  </dd>
                </div>
                <div>
                  <dt>Password</dt>
                  <dd>{item.passwordProtected ? 'Required' : 'Not required'}</dd>
                </div>
                <div>
                  <dt>Expiry</dt>
                  <dd>
                    {item.expiresAt
                      ? formatDate(new Date(item.expiresAt))
                      : 'No expiry'}
                  </dd>
                </div>
              </dl>
              {item.status === 'active' ? (
                <button
                  type="button"
                  ref={revokeButtonRef}
                  className={styles.dangerButton}
                  onClick={() => setRevokeTarget(item)}
                >
                  Revoke
                </button>
              ) : null}
            </article>
          ))}
        </div>
      </section>

      {revokeTarget ? (
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="revoke-title"
          className={styles.dialogBackdrop}
          onKeyDown={(event) => {
            if (event.key === 'Escape') {
              setRevokeTarget(undefined);
              revokeButtonRef.current?.focus();
            } else if (event.key === 'Tab') {
              trapFocus(event, revokeDialogRef.current);
            }
          }}
        >
          <div className={styles.dialog} ref={revokeDialogRef}>
            <h2 id="revoke-title">Revoke this link?</h2>
            <p>
              Recipients will immediately lose access to “{revokeTarget.name}”.
              Revocation cannot be undone, but a new link can be created.
            </p>
            <div className={styles.actions}>
              <button
                type="button"
                className={styles.dangerButton}
                disabled={submitting}
                autoFocus
                onClick={confirmRevocation}
              >
                {submitting ? 'Revoking…' : 'Confirm revocation'}
              </button>
              <button
                type="button"
                className={styles.secondaryButton}
                onClick={() => {
                  setRevokeTarget(undefined);
                  revokeButtonRef.current?.focus();
                }}
              >
                Keep link
              </button>
            </div>
          </div>
        </div>
      ) : null}
    </section>
  );
}

function messageFor(caught: unknown, fallback: string) {
  return caught instanceof Error && caught.message ? caught.message : fallback;
}

function formatDate(date: Date) {
  return new Intl.DateTimeFormat('en-US', {
    dateStyle: 'medium',
    timeZone: 'UTC',
  }).format(date);
}

function toLocalDateTime(date: Date) {
  const offset = date.getTimezoneOffset() * 60_000;
  return new Date(date.getTime() - offset).toISOString().slice(0, 16);
}

function titleCase(value: string) {
  return `${value.charAt(0).toUpperCase()}${value.slice(1)}`;
}

function downloadLabel(item: ShareSummary) {
  if (item.permissions.downloadOriginal) {
    return 'Original and display';
  }
  return item.permissions.downloadRenditions ? 'Display only' : 'Disabled';
}

function trapFocus(event: React.KeyboardEvent, container: HTMLElement | null) {
  if (!container) {
    return;
  }
  const focusable = Array.from(
    container.querySelectorAll<HTMLElement>(
      'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href]',
    ),
  );
  const first = focusable[0];
  const last = focusable.at(-1);
  if (!first || !last) {
    return;
  }
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
}
