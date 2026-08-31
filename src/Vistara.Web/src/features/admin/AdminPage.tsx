import type { ReactNode } from 'react';
import { Skeleton } from '../../components';
import styles from './admin.module.css';

interface AdminPageProps {
  readonly title: string;
  readonly description: string;
  readonly children: ReactNode;
  readonly toolbar?: ReactNode;
}

export function AdminPage({
  children,
  description,
  title,
  toolbar,
}: AdminPageProps) {
  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <p className={styles.eyebrow}>Administration</p>
        <h1>{title}</h1>
        <p className={styles.description}>{description}</p>
        {toolbar ? <div className={styles.toolbar}>{toolbar}</div> : null}
      </header>
      {children}
    </div>
  );
}

interface AdminLoadingProps {
  readonly label: string;
  readonly shape?: 'row' | 'card';
}

export function AdminLoading({ label, shape = 'row' }: AdminLoadingProps) {
  return (
    <div>
      <p className={styles.pending}>{label}</p>
      <Skeleton count={4} shape={shape} />
    </div>
  );
}

interface AdminFailureProps {
  readonly title: string;
  readonly description: string;
  readonly onRetry: () => void;
}

export function AdminFailure({
  description,
  onRetry,
  title,
}: AdminFailureProps) {
  return (
    <section className={styles.failure} aria-labelledby="admin-failure-heading">
      <h2 id="admin-failure-heading">{title}</h2>
      <p>{description}</p>
      <button className={styles.primaryButton} type="button" onClick={onRetry}>
        Try again
      </button>
    </section>
  );
}

export function AdminEmpty({ children }: { readonly children: ReactNode }) {
  return <p className={styles.empty}>{children}</p>;
}

interface PendingContractProps {
  readonly title: string;
  readonly description: string;
  /** The exact request the API must publish before this can be built. */
  readonly contract: string;
}

/**
 * States plainly that a specified screen has no route yet and names the
 * contract it will consume, so nothing here pretends to read data.
 */
export function AdminPendingContract({
  contract,
  description,
  title,
}: PendingContractProps) {
  return (
    <section className={styles.pending} aria-labelledby={`pending-${slug(title)}`}>
      <h2 className={styles.pendingTitle} id={`pending-${slug(title)}`}>
        {title}
      </h2>
      <p>{description}</p>
      <p className={styles.contract}>
        <span className={styles.contractLabel}>Required contract</span>
        <code>{contract}</code>
      </p>
    </section>
  );
}

function slug(value: string) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-');
}
