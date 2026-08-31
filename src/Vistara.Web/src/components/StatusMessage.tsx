import type { ReactNode } from 'react';
import styles from './components.module.css';

export type StatusTone = 'info' | 'pending' | 'empty' | 'danger' | 'success';

interface StatusMessageProps {
  readonly tone?: StatusTone;
  readonly title: string;
  readonly description?: ReactNode;
  readonly action?: ReactNode;
  readonly headingLevel?: 1 | 2 | 3;
  readonly live?: boolean;
}

/**
 * Shared loading, empty, and failure presentation so every route reports the
 * same states with the same semantics.
 */
export function StatusMessage({
  action,
  description,
  headingLevel = 2,
  live = false,
  title,
  tone = 'info',
}: StatusMessageProps) {
  const Heading = `h${headingLevel}` as 'h1' | 'h2' | 'h3';

  return (
    <div
      className={styles.status}
      data-tone={tone}
      {...(live ? { role: 'status', 'aria-live': 'polite' } : {})}
    >
      {tone === 'pending' ? (
        <span className={styles.statusSpinner} aria-hidden="true" />
      ) : null}
      <Heading className={styles.statusTitle}>{title}</Heading>
      {description ? (
        <p className={styles.statusDescription}>{description}</p>
      ) : null}
      {action ? <div className={styles.statusAction}>{action}</div> : null}
    </div>
  );
}
