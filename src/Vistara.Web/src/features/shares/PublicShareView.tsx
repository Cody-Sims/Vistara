import { useEffect, useState } from 'react';
import {
  VistaraApiError,
  type VistaraApiClient,
} from '../../api/generated';
import type { PublicShare } from '../../api/generated/models';
import { buildResponsiveImage } from '../viewer/responsiveImage';
import styles from './Shares.module.css';

export interface PublicShareViewProps {
  readonly client: Pick<
    VistaraApiClient,
    'getPublicShare' | 'challengePublicShare'
  >;
  readonly token: string;
  readonly now?: () => Date;
}

export function PublicShareView({
  client,
  token,
  now = () => new Date(),
}: PublicShareViewProps) {
  const [share, setShare] = useState<PublicShare>();
  const [loading, setLoading] = useState(true);
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string>();
  const [unavailable, setUnavailable] = useState<'expired' | 'revoked'>();

  async function loadShare() {
    setLoading(true);
    try {
      const response = await client.getPublicShare(token);
      setShare(response.data);
      setUnavailable(
        response.data.status === 'active' ? undefined : response.data.status,
      );
      setError(undefined);
    } catch (caught) {
      if (caught instanceof VistaraApiError && caught.status === 410) {
        setUnavailable('revoked');
      } else {
        setError('This shared gallery could not be loaded.');
      }
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    let active = true;
    void client
      .getPublicShare(token)
      .then((response) => {
        if (active) {
          setShare(response.data);
          setUnavailable(
            response.data.status === 'active'
              ? undefined
              : response.data.status,
          );
          setError(undefined);
        }
      })
      .catch((caught: unknown) => {
        if (!active) {
          return;
        }
        if (caught instanceof VistaraApiError && caught.status === 410) {
          setUnavailable('revoked');
        } else {
          setError('This shared gallery could not be loaded.');
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
  }, [client, token]);

  async function unlock(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(undefined);
    try {
      const response = await client.challengePublicShare(token, { password });
      if (!response.data.authenticated) {
        setError('That password was not accepted.');
        return;
      }
      setPassword('');
      await loadShare();
    } catch (caught) {
      if (caught instanceof VistaraApiError && caught.status === 429) {
        setError('Too many attempts. Wait before trying again.');
      } else {
        setError('That password was not accepted.');
      }
    } finally {
      setSubmitting(false);
    }
  }

  if (loading) {
    return (
      <main className={styles.publicPage}>
        <p role="status">Loading shared gallery…</p>
      </main>
    );
  }

  if (unavailable) {
    return (
      <main className={styles.publicPage}>
        <section className={styles.publicNotice}>
          <p className={styles.eyebrow}>Shared gallery</p>
          <h1>Share unavailable</h1>
          <p>
            This link has {unavailable}. Ask its owner to create a new link if
            access is still intended.
          </p>
        </section>
      </main>
    );
  }

  if (!share) {
    return (
      <main className={styles.publicPage}>
        <section className={styles.publicNotice}>
          <h1>Shared gallery unavailable</h1>
          <p role="alert">{error}</p>
          <button type="button" className={styles.primaryButton} onClick={loadShare}>
            Try again
          </button>
        </section>
      </main>
    );
  }

  return (
    <main className={styles.publicPage}>
      <header className={styles.publicHeader}>
        <p className={styles.eyebrow}>Shared gallery</p>
        <h1>{share.name}</h1>
        {share.expiresAt ? (
          <p>
            Available until {formatDate(new Date(share.expiresAt))}
            {new Date(share.expiresAt) <= now() ? ' (expiry pending)' : ''}
          </p>
        ) : (
          <p>The owner has not set an automatic expiry.</p>
        )}
        <div className={styles.policyRow} aria-label="Share permissions">
          <span>
            {share.permissions.downloadRenditions
              ? 'Display downloads are allowed.'
              : 'Display downloads are disabled.'}
          </span>
          <span>
            {share.permissions.downloadOriginal
              ? 'Original downloads are allowed.'
              : 'Original downloads are disabled.'}
          </span>
          <span>
            {share.metadataExposure === 'basic'
              ? 'Basic metadata is visible.'
              : 'Metadata is hidden.'}
          </span>
        </div>
      </header>

      {error ? <p role="alert" className={styles.error}>{error}</p> : null}

      {share.passwordRequired ? (
        <form className={styles.passwordPanel} onSubmit={unlock}>
          <h2>Password required</h2>
          <p>The owner protected this gallery with a password.</p>
          <label className={styles.field}>
            <span>Share password</span>
            <input
              type="password"
              required
              autoComplete="current-password"
              autoFocus
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          </label>
          <button
            type="submit"
            className={styles.primaryButton}
            disabled={submitting}
          >
            {submitting ? 'Unlocking…' : 'Unlock gallery'}
          </button>
        </form>
      ) : (
        <section aria-labelledby="shared-assets-heading">
          <h2 id="shared-assets-heading">Shared images</h2>
          {!share.assets || share.assets.items.length === 0 ? (
            <p>No images are currently available in this share.</p>
          ) : (
            <ul className={styles.publicGrid}>
              {share.assets.items.map((asset, index) => {
                const image = buildResponsiveImage(asset, 'share', index === 0);

                return (
                  <li key={asset.id} className={styles.publicCard}>
                    <h3>{asset.title}</h3>
                    {image ? (
                      <img
                        {...image}
                        alt={
                          share.metadataExposure === 'basic' &&
                          asset.description
                            ? asset.description
                            : asset.title
                        }
                      />
                    ) : null}
                    {share.metadataExposure === 'basic' && asset.description ? (
                      <p>{asset.description}</p>
                    ) : null}
                    {share.metadataExposure === 'basic' ? (
                      <dl className={styles.assetMetadata}>
                        <div>
                          <dt>Dimensions</dt>
                          <dd>
                            {asset.width} × {asset.height}
                          </dd>
                        </div>
                        {asset.capturedAt ? (
                          <div>
                            <dt>Captured</dt>
                            <dd>{formatDate(new Date(asset.capturedAt))}</dd>
                          </div>
                        ) : null}
                      </dl>
                    ) : null}
                    {share.permissions.downloadRenditions &&
                    asset.renditions[0] ? (
                      <a
                        className={styles.secondaryButton}
                        href={asset.renditions[0].path}
                        download
                      >
                        Download display image
                      </a>
                    ) : null}
                  </li>
                );
              })}
            </ul>
          )}
        </section>
      )}
    </main>
  );
}

function formatDate(date: Date) {
  return new Intl.DateTimeFormat('en-US', {
    dateStyle: 'medium',
    timeZone: 'UTC',
  }).format(date);
}
