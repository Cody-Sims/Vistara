import {
  type ChangeEvent,
  type DragEvent,
  useEffect,
  useRef,
  useSyncExternalStore,
} from 'react';
import type { UploadQueueItem } from './uploadQueue';
import { UploadQueue } from './uploadQueue';
import styles from './UploadQueueView.module.css';

interface UploadQueueViewProps {
  readonly queue: UploadQueue;
}

export function UploadQueueView({ queue }: UploadQueueViewProps) {
  const items = useSyncExternalStore(
    queue.subscribe,
    queue.getItems,
    queue.getItems,
  );
  const input = useRef<HTMLInputElement>(null);
  const active = items.some((item) =>
    ['queued', 'hashing', 'uploading', 'paused', 'processing'].includes(item.phase),
  );

  useEffect(() => {
    if (!active) {
      return;
    }
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [active]);

  const choose = () => input.current?.click();
  const add = (files: FileList | readonly File[]) => {
    queue.addFiles(Array.from(files));
    if (input.current) {
      input.current.value = '';
    }
  };
  const onChange = (event: ChangeEvent<HTMLInputElement>) => {
    if (event.target.files) {
      add(event.target.files);
    }
  };
  const onDrop = (event: DragEvent<HTMLButtonElement>) => {
    event.preventDefault();
    add(event.dataTransfer.files);
  };

  const announcement = items.at(-1);

  return (
    <section className={styles.uploads} aria-labelledby="uploads-heading">
      <div className={styles.heading}>
        <div>
          <p className={styles.eyebrow}>Add to your library</p>
          <h1 id="uploads-heading">Upload images</h1>
          <p className={styles.description}>
            JPEG, PNG, and WebP files upload securely in the background.
          </p>
        </div>
        {active ? (
          <span className={styles.activeBadge}>
            {
              items.filter((item) =>
                ['queued', 'hashing', 'uploading', 'paused', 'processing'].includes(
                  item.phase,
                ),
              ).length
            } active
          </span>
        ) : null}
      </div>

      <input
        ref={input}
        className={styles.visuallyHidden}
        type="file"
        accept="image/jpeg,image/png,image/webp"
        multiple
        aria-label="Choose images to upload"
        onChange={onChange}
      />
      <button
        className={styles.dropZone}
        type="button"
        onClick={choose}
        onDragOver={(event) => event.preventDefault()}
        onDrop={onDrop}
      >
        <span className={styles.dropIcon} aria-hidden="true">
          ↑
        </span>
        <strong>Choose or drop images here</strong>
        <span>Tap to browse on mobile</span>
      </button>

      <p className={styles.liveRegion} role="status" aria-live="polite">
        {announcement
          ? `${announcement.fileName}: ${phaseLabel(announcement)}`
          : 'No uploads queued.'}
      </p>

      {items.length > 0 ? (
        <ul className={styles.queue} aria-label="Upload queue">
          {items.map((item) => (
            <UploadRow key={item.id} item={item} queue={queue} />
          ))}
        </ul>
      ) : (
        <p className={styles.empty}>Your upload queue is empty.</p>
      )}
    </section>
  );
}

function UploadRow({
  item,
  queue,
}: {
  readonly item: UploadQueueItem;
  readonly queue: UploadQueue;
}) {
  return (
    <li className={styles.queueItem}>
      <div className={styles.fileDetails}>
        <div>
          <h2 className={styles.fileName}>{item.fileName}</h2>
          <p className={styles.meta}>
            {formatBytes(item.sizeBytes)}
            {item.strategy ? ` · ${item.strategy}` : ''}
          </p>
        </div>
        <span className={styles.phase} data-phase={item.phase}>
          {phaseLabel(item)}
        </span>
      </div>

      <progress
        className={styles.progress}
        max="100"
        value={item.progress}
        aria-label={`Upload progress for ${item.fileName}`}
      >
        {item.progress}%
      </progress>
      {item.message ? <p className={styles.message}>{item.message}</p> : null}

      <div className={styles.actions}>
        {item.canPause ? (
          <button
            className={styles.queueAction}
            type="button"
            onClick={() => void queue.pause(item.id)}
            aria-label={`Pause ${item.fileName}`}
          >
            Pause
          </button>
        ) : null}
        {item.canResume ? (
          <button
            className={styles.queueAction}
            type="button"
            onClick={() => queue.resume(item.id)}
            aria-label={`Resume ${item.fileName}`}
          >
            Resume
          </button>
        ) : null}
        {item.needsFile ? (
          <label className={styles.queueAction}>
            Choose file
            <input
              className={styles.visuallyHidden}
              type="file"
              accept={item.fileName.toLowerCase().endsWith('.png')
                ? 'image/png'
                : item.fileName.toLowerCase().endsWith('.webp')
                  ? 'image/webp'
                  : 'image/jpeg'}
              aria-label={`Choose file to resume ${item.fileName}`}
              onChange={(event) => {
                const selected = event.target.files?.[0];
                if (selected) {
                  queue.resumeWithFile(item.id, selected);
                }
              }}
            />
          </label>
        ) : null}
        {item.canRetry ? (
          <button
            className={styles.queueAction}
            type="button"
            onClick={() => queue.retry(item.id)}
            aria-label={`Retry ${item.fileName}`}
          >
            Retry
          </button>
        ) : null}
        {item.canCancel ? (
          <button
            className={styles.queueAction}
            type="button"
            onClick={() => void queue.cancel(item.id)}
            aria-label={`Cancel ${item.fileName}`}
          >
            Cancel
          </button>
        ) : null}
      </div>
    </li>
  );
}

function phaseLabel(item: UploadQueueItem) {
  const labels: Record<UploadQueueItem['phase'], string> = {
    queued: 'Queued',
    hashing: 'Preparing',
    uploading: `${item.progress}%`,
    paused: 'Paused',
    processing: 'Processing',
    ready: 'Ready',
    duplicate: 'Already uploaded',
    cancelled: 'Cancelled',
    error: 'Error',
  };
  return labels[item.phase];
}

function formatBytes(bytes: number) {
  if (bytes < 1_024 * 1_024) {
    return `${Math.max(1, Math.round(bytes / 1_024))} KB`;
  }
  return `${(bytes / (1_024 * 1_024)).toFixed(1)} MB`;
}
