import type { HTMLAttributes } from 'react';
import styles from './VisuallyHidden.module.css';

export function VisuallyHidden({
  className,
  ...props
}: HTMLAttributes<HTMLSpanElement>) {
  const classes = className ? `${styles.root} ${className}` : styles.root;
  return <span className={classes} {...props} />;
}
