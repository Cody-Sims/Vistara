import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { UploadQueueView } from './UploadQueueView';
import {
  UploadQueue,
  type UploadApi,
  type UploadStatus,
  type UploadTransfer,
} from './uploadQueue';

function createQueue(): { queue: UploadQueue; complete: () => void } {
  let completeTransfer: (() => void) | undefined;
  const client: UploadApi = {
    create: vi.fn(async (): Promise<UploadStatus> => ({
      uploadId: 'upload-1',
      fileName: 'photo.jpg',
      strategy: 'direct',
      state: 'uploadIssued',
      expectedSizeBytes: 4,
      declaredContentType: 'image/jpeg',
      sha256: 'a'.repeat(64),
      expiresAt: '2030-01-01T00:00:00.000Z',
      version: 1,
      parts: [],
      plan: {
        mode: 'direct',
        request: {
          method: 'PUT',
          url: 'https://uploads.invalid/signed',
          headers: {},
        },
      },
    })),
    status: vi.fn(),
    refreshParts: vi.fn(),
    commit: vi.fn(async (): Promise<UploadStatus> => ({
      uploadId: 'upload-1',
      fileName: 'photo.jpg',
      strategy: 'direct',
      state: 'accepted',
      expectedSizeBytes: 4,
      declaredContentType: 'image/jpeg',
      sha256: 'a'.repeat(64),
      expiresAt: '2030-01-01T00:00:00.000Z',
      version: 3,
      parts: [],
    })),
    abort: vi.fn(async () => undefined),
  };
  const boundary: UploadTransfer = {
    signed: vi.fn(
      (_request, _blob, onProgress, signal) =>
        new Promise<{ etag: string }>((resolve, reject) => {
          onProgress(2, 4);
          completeTransfer = () => resolve({ etag: '"part"' });
          signal.addEventListener('abort', () =>
            reject(new DOMException('Paused', 'AbortError')),
          );
        }),
    ),
    proxy: vi.fn(),
  };
  return {
    queue: new UploadQueue({
      client,
      transfer: boundary,
      hashFile: vi.fn(async () => 'a'.repeat(64)),
    }),
    complete: () => completeTransfer?.(),
  };
}

describe('uploads view', () => {
  it('supports a keyboard file picker and announces queued progress', async () => {
    const user = userEvent.setup();
    const { queue } = createQueue();
    render(<UploadQueueView queue={queue} />);

    const picker = screen.getByLabelText('Choose images to upload');
    await user.upload(
      picker,
      new File(['data'], 'photo.jpg', { type: 'image/jpeg' }),
    );

    expect(
      await screen.findByRole('heading', { name: 'photo.jpg' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('progressbar')).toHaveAttribute(
      'aria-label',
      'Upload progress for photo.jpg',
    );
    expect(screen.getByRole('status')).toHaveTextContent(/photo.jpg/);
    expect(
      screen.getByRole('button', { name: 'Pause photo.jpg' }),
    ).toBeInTheDocument();
  });

  it('accepts dropped files and exposes touch-sized queue controls', async () => {
    const { queue } = createQueue();
    render(<UploadQueueView queue={queue} />);

    fireEvent.drop(screen.getByRole('button', { name: /drop images/i }), {
      dataTransfer: {
        files: [new File(['data'], 'dropped.webp', { type: 'image/webp' })],
      },
    });

    expect(
      await screen.findByRole('heading', { name: 'dropped.webp' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Cancel dropped.webp' }),
    ).toHaveClass(/queueAction/);
  });

  it('warns before unloading while work is active and stops warning afterward', async () => {
    const { queue, complete } = createQueue();
    render(<UploadQueueView queue={queue} />);
    queue.addFiles([
      new File(['data'], 'photo.jpg', { type: 'image/jpeg' }),
    ]);
    await screen.findByRole('button', { name: 'Pause photo.jpg' });

    const activeEvent = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(activeEvent);
    expect(activeEvent.defaultPrevented).toBe(true);

    complete();
    await screen.findByText('Ready', { selector: 'span' });
    const completeEvent = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(completeEvent);
    expect(completeEvent.defaultPrevented).toBe(false);
  });
});
