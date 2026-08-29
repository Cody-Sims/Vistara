import { describe, expect, it, vi } from 'vitest';
import {
  MemoryUploadPersistence,
  UploadQueue,
  type UploadApi,
  type UploadPlan,
  type UploadStatus,
  type UploadTransfer,
} from './uploadQueue';

const readyStatus = (uploadId: string, version = 3): UploadStatus => ({
  uploadId,
  fileName: `${uploadId}.jpg`,
  strategy: 'direct',
  state: 'accepted',
  expectedSizeBytes: 4,
  declaredContentType: 'image/jpeg',
  sha256: 'a'.repeat(64),
  expiresAt: '2030-01-01T00:00:00.000Z',
  version,
  parts: [],
});

function directPlan(uploadId: string): UploadStatus & { plan: UploadPlan } {
  return {
    ...readyStatus(uploadId, 1),
    state: 'uploadIssued',
    plan: {
      mode: 'direct',
      expiresAt: '2030-01-01T00:00:00.000Z',
      request: {
        method: 'PUT',
        url: `https://uploads.invalid/${uploadId}?secret=never-persist`,
        headers: { Authorization: 'signed secret' },
      },
    },
  };
}

function file(
  name = 'photo.jpg',
  type = 'image/jpeg',
  contents = 'data',
) {
  return new File([contents], name, { type, lastModified: 1 });
}

function api(overrides: Partial<UploadApi> = {}): UploadApi {
  return {
    create: vi.fn(async (_file, _sha256, key) =>
      directPlan(`upload-${key.slice(-1)}`)),
    status: vi.fn(async (uploadId) => readyStatus(uploadId)),
    refreshParts: vi.fn(),
    commit: vi.fn(async (uploadId) => readyStatus(uploadId)),
    abort: vi.fn(async () => undefined),
    ...overrides,
  };
}

function transfer(overrides: Partial<UploadTransfer> = {}): UploadTransfer {
  return {
    signed: vi.fn(async (_request, blob, onProgress) => {
      onProgress(blob.size, blob.size);
      return { etag: '"part"' };
    }),
    proxy: vi.fn(async (_url, _version, blob, onProgress) => {
      onProgress(blob.size, blob.size);
      return 2;
    }),
    ...overrides,
  };
}

describe('uploads queue', () => {
  it('rejects unsupported and empty files before calling the API', async () => {
    const client = api();
    const queue = new UploadQueue({
      client,
      transfer: transfer(),
      hashFile: vi.fn(),
    });

    queue.addFiles([
      file('vector.svg', 'image/svg+xml'),
      file('empty.png', 'image/png', ''),
    ]);

    expect(queue.getItems().map((item) => item.phase)).toEqual([
      'error',
      'error',
    ]);
    expect(queue.getItems()[0]?.message).toMatch(/JPEG, PNG, and WebP/);
    expect(queue.getItems()[1]?.message).toMatch(/empty/);
    expect(client.create).not.toHaveBeenCalled();
  });

  it('bounds active file uploads while reporting deterministic progress', async () => {
    let active = 0;
    let maximum = 0;
    const releases: Array<() => void> = [];
    const upload = vi.fn(
      (_request, blob: Blob, onProgress: (sent: number, total: number) => void) =>
        new Promise<{ etag: string }>((resolve) => {
          active += 1;
          maximum = Math.max(maximum, active);
          onProgress(blob.size / 2, blob.size);
          releases.push(() => {
            onProgress(blob.size, blob.size);
            active -= 1;
            resolve({ etag: '"part"' });
          });
        }),
    );
    const queue = new UploadQueue({
      client: api(),
      transfer: transfer({ signed: upload }),
      hashFile: vi.fn(async () => 'a'.repeat(64)),
      createId: (() => {
        let value = 0;
        return () => `queue-${++value}`;
      })(),
      maxConcurrent: 2,
    });

    queue.addFiles([file('1.jpg'), file('2.jpg'), file('3.jpg')]);
    await vi.waitFor(() => expect(upload).toHaveBeenCalledTimes(2));

    expect(maximum).toBe(2);
    expect(queue.getItems().filter((item) => item.progress === 50)).toHaveLength(
      2,
    );

    releases.shift()?.();
    await vi.waitFor(() => expect(upload).toHaveBeenCalledTimes(3));
    releases.splice(0).forEach((release) => release());
    await vi.waitFor(() =>
      expect(queue.getItems().every((item) => item.phase === 'ready')).toBe(true),
    );
  });

  it('uses proxy and multipart plans, refreshing an expired part plan', async () => {
    const proxy = file('proxy.jpg');
    const multipart = file('multipart.jpg', 'image/jpeg', 'abcdefgh');
    const refreshParts = vi.fn(async (_uploadId, partNumbers: readonly number[]) => ({
      parts: [
        {
          partNumber: partNumbers[0]!,
          minBytes: 1,
          maxBytes: 4,
          expiresAt: '2030-01-01T00:00:00.000Z',
          request: {
            method: 'PUT',
            url: 'https://uploads.invalid/refreshed',
            headers: {},
          },
        },
      ],
    }));
    const client = api({
      create: vi
        .fn()
        .mockResolvedValueOnce({
          ...readyStatus('proxy', 1),
          state: 'uploadIssued',
          strategy: 'proxy',
          plan: { mode: 'proxy', contentUrl: '/api/v1/uploads/proxy/content' },
        })
        .mockResolvedValueOnce({
          ...readyStatus('multipart', 1),
          expectedSizeBytes: 8,
          state: 'uploadIssued',
          strategy: 'multipart',
          plan: {
            mode: 'multipart',
            multipart: {
              maxParts: 2,
              minPartBytes: 1,
              maxPartBytes: 4,
              parts: [
                {
                  partNumber: 1,
                  minBytes: 1,
                  maxBytes: 4,
                  expiresAt: '2020-01-01T00:00:00.000Z',
                  request: {
                    method: 'PUT',
                    url: 'https://uploads.invalid/expired',
                    headers: {},
                  },
                },
              ],
            },
          },
        }),
      refreshParts,
    });
    const boundary = transfer();
    const queue = new UploadQueue({
      client,
      transfer: boundary,
      hashFile: vi.fn(async () => 'a'.repeat(64)),
      maxConcurrent: 1,
      now: () => new Date('2029-01-01T00:00:00.000Z'),
    });

    queue.addFiles([proxy, multipart]);

    await vi.waitFor(() =>
      expect(queue.getItems().every((item) => item.phase === 'ready')).toBe(true),
    );
    expect(boundary.proxy).toHaveBeenCalledWith(
      '/api/v1/uploads/proxy/content',
      1,
      proxy,
      expect.any(Function),
      expect.any(AbortSignal),
    );
    expect(refreshParts).toHaveBeenCalledWith('multipart', [1], 1);
    expect(boundary.signed).toHaveBeenCalledTimes(2);
    expect(client.commit).toHaveBeenLastCalledWith(
      'multipart',
      [
        expect.objectContaining({ partNumber: 1, sizeBytes: 4 }),
        expect.objectContaining({ partNumber: 2, sizeBytes: 4 }),
      ],
      expect.any(String),
      1,
    );
  });

  it('pauses by aborting the active transfer and resumes without a new intent', async () => {
    let firstSignal: AbortSignal | undefined;
    const signed = vi
      .fn()
      .mockImplementationOnce(
        (
          _request,
          _blob,
          _onProgress,
          signal: AbortSignal,
        ): Promise<{ etag: string }> => {
          firstSignal = signal;
          return new Promise((_resolve, reject) => {
            signal.addEventListener('abort', () =>
              reject(new DOMException('Paused', 'AbortError')),
            );
          });
        },
      )
      .mockResolvedValueOnce({ etag: '"part"' });
    const client = api();
    const queue = new UploadQueue({
      client,
      transfer: transfer({ signed }),
      hashFile: vi.fn(async () => 'a'.repeat(64)),
    });

    const [item] = queue.addFiles([file()]);
    expect(item).toBeDefined();
    if (!item) {
      throw new Error('Expected an upload item.');
    }
    await vi.waitFor(() => expect(signed).toHaveBeenCalledOnce());
    await queue.pause(item.id);

    expect(firstSignal?.aborted).toBe(true);
    expect(queue.getItem(item.id)?.phase).toBe('paused');

    queue.resume(item.id);
    await vi.waitFor(() => expect(queue.getItem(item.id)?.phase).toBe('ready'));
    expect(client.create).toHaveBeenCalledOnce();
    expect(signed).toHaveBeenCalledTimes(2);
  });

  it('cancels remotely, retries with stable idempotency keys, and marks replayed commits duplicate', async () => {
    const commit = vi
      .fn()
      .mockRejectedValueOnce(new Error('network unavailable'))
      .mockResolvedValue({
        status: readyStatus('upload-1'),
        duplicate: true,
      });
    const client = api({ commit });
    const queue = new UploadQueue({
      client,
      transfer: transfer(),
      hashFile: vi.fn(async () => 'a'.repeat(64)),
      createId: (() => {
        let value = 0;
        return () => `stable-${++value}`;
      })(),
    });

    const [retryItem, cancelItem] = queue.addFiles([
      file('retry.jpg'),
      file('cancel.jpg'),
    ]);
    expect(retryItem).toBeDefined();
    expect(cancelItem).toBeDefined();
    if (!retryItem || !cancelItem) {
      throw new Error('Expected queued upload items.');
    }
    await vi.waitFor(() =>
      expect(queue.getItem(retryItem.id)?.phase).toBe('error'),
    );
    const firstCommitKey = commit.mock.calls[0]?.[2];

    queue.retry(retryItem.id);
    await vi.waitFor(() =>
      expect(queue.getItem(retryItem.id)?.phase).toBe('duplicate'),
    );
    expect(commit.mock.calls.at(-1)?.[2]).toBe(firstCommitKey);

    await queue.cancel(cancelItem.id);
    expect(client.abort).toHaveBeenCalledWith(
      expect.any(String),
      expect.any(Number),
    );
    expect(queue.getItem(cancelItem.id)?.phase).toBe('cancelled');
  });

  it('persists resumable metadata without signed URLs or headers', async () => {
    const persistence = new MemoryUploadPersistence();
    const queue = new UploadQueue({
      client: api(),
      transfer: transfer({
        signed: vi.fn(
          () =>
            new Promise<{ etag: string }>(() => {
              // Keep the item uploading while persistence is inspected.
            }),
        ),
      }),
      persistence,
      hashFile: vi.fn(async () => 'a'.repeat(64)),
    });

    queue.addFiles([file()]);
    await vi.waitFor(() =>
      expect(persistence.records[0]?.uploadId).toMatch(/^upload-/),
    );
    const serialized = JSON.stringify(persistence.records);

    expect(serialized).toContain('upload-');
    expect(serialized).not.toContain('uploads.invalid');
    expect(serialized).not.toContain('Authorization');
    expect(serialized).not.toContain('signed secret');
  });

  it('restores resumable metadata as paused until the same local file is selected', async () => {
    const persistence = new MemoryUploadPersistence();
    persistence.records = [
      {
        id: 'restored',
        fileName: 'resume.jpg',
        sizeBytes: 4,
        contentType: 'image/jpeg',
        lastModified: 1,
        phase: 'uploading',
        progress: 50,
        sha256: 'a'.repeat(64),
        uploadId: 'upload-restored',
        strategy: 'direct',
        version: 1,
        completedParts: [],
        createKey: 'create-stable',
        commitKey: 'commit-stable',
      },
    ];
    const client = api();
    const queue = new UploadQueue({
      client,
      transfer: transfer(),
      persistence,
      hashFile: vi.fn(async () => 'a'.repeat(64)),
    });

    await queue.restore();

    expect(queue.getItem('restored')).toMatchObject({
      phase: 'paused',
      progress: 50,
      message: expect.stringMatching(/same file/i),
    });

    queue.resumeWithFile('restored', file('resume.jpg'));
    await vi.waitFor(() =>
      expect(queue.getItem('restored')?.phase).toBe('ready'),
    );
    expect(client.create).toHaveBeenCalledOnce();
  });

  it('polls processing uploads to ready and surfaces a rejected processing result', async () => {
    const processing = {
      ...readyStatus('processing', 2),
      state: 'verifying',
    };
    const client = api({
      commit: vi.fn(async () => processing),
      status: vi
        .fn()
        .mockResolvedValueOnce(readyStatus('processing', 3))
        .mockResolvedValueOnce({
          ...readyStatus('rejected', 3),
          state: 'rejected',
        }),
    });
    const phases: string[] = [];
    const queue = new UploadQueue({
      client,
      transfer: transfer(),
      hashFile: vi.fn(async () => 'a'.repeat(64)),
      waitForPoll: vi.fn(async () => undefined),
      maxConcurrent: 1,
    });
    queue.subscribe(() => {
      phases.push(...queue.getItems().map((item) => item.phase));
    });

    const [ready, rejected] = queue.addFiles([
      file('processing.jpg'),
      file('rejected.jpg'),
    ]);
    expect(ready).toBeDefined();
    expect(rejected).toBeDefined();
    if (!ready || !rejected) {
      throw new Error('Expected processing upload items.');
    }

    await vi.waitFor(() => expect(queue.getItem(ready.id)?.phase).toBe('ready'));
    await vi.waitFor(() =>
      expect(queue.getItem(rejected.id)?.phase).toBe('error'),
    );
    expect(phases).toContain('processing');
  });
});
