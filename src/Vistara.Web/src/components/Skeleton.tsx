import styles from './components.module.css';

interface SkeletonProps {
  /** Number of placeholder blocks to draw. */
  readonly count?: number;
  readonly shape?: 'text' | 'card' | 'row' | 'tile';
  /** Accessible description announced once while content loads. */
  readonly label?: string;
}

/**
 * Decorative loading placeholder. The shimmer is suppressed for reduced-motion
 * visitors, and the placeholder is hidden from assistive technology because the
 * owning view announces its own pending status.
 */
export function Skeleton({ count = 3, label, shape = 'text' }: SkeletonProps) {
  return (
    <div
      className={styles.skeleton}
      data-shape={shape}
      {...(label
        ? { role: 'status', 'aria-live': 'polite', 'aria-label': label }
        : { 'aria-hidden': 'true' })}
    >
      {Array.from({ length: count }, (_, index) => (
        <span key={index} className={styles.skeletonBlock} />
      ))}
    </div>
  );
}
