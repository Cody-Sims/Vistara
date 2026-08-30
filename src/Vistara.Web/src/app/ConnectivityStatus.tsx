import { useSyncExternalStore } from 'react';
import styles from './ConnectivityStatus.module.css';

const listeners = new Set<() => void>();
let online = true;

function publish(next: boolean) {
  if (online === next) {
    return;
  }

  online = next;
  for (const listener of listeners) {
    listener();
  }
}

const goOnline = () => publish(true);
const goOffline = () => publish(false);

function subscribe(onStoreChange: () => void) {
  if (listeners.size === 0) {
    online = navigator.onLine !== false;
    window.addEventListener('online', goOnline);
    window.addEventListener('offline', goOffline);
  }

  listeners.add(onStoreChange);
  return () => {
    listeners.delete(onStoreChange);
    if (listeners.size === 0) {
      window.removeEventListener('online', goOnline);
      window.removeEventListener('offline', goOffline);
    }
  };
}

export function ConnectivityStatus() {
  const isOnline = useSyncExternalStore(
    subscribe,
    () => online,
    () => true,
  );

  return (
    <span
      className={`${styles.status} ${isOnline ? styles.online : styles.offline}`}
      role="status"
      aria-label="Connection status"
      aria-live="polite"
    >
      <span className={styles.indicator} aria-hidden="true" />
      {isOnline ? 'Online' : 'Offline'}
    </span>
  );
}
