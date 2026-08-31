import { describe, expect, it, vi } from 'vitest';
import {
  defaultTrashReason,
  outcomeForTrashStatus,
  restorableReferences,
  restoreTrashedAssets,
  trashAssets,
} from './curationClient';

const items = [
  { id: 'a', version: 3 },
  { id: 'b', version: 4 },
];

describe('trash and restore boundary', () => {
  it('asks the bulk route for a trash action with the versions it was given', async () => {
    const bulkMutateAssets = vi.fn().mockResolvedValue({
      data: [
        { assetId: 'a', status: 'trashed', version: 4 },
        { assetId: 'b', status: 'trashed', version: 5 },
      ],
    });

    const answer = await trashAssets(
      { bulkMutateAssets } as never,
      items,
      '  spring clean  ',
      'key-1',
    );

    expect(bulkMutateAssets).toHaveBeenCalledWith(
      { items, action: { kind: 'trash', reason: 'spring clean' } },
      { idempotencyKey: 'key-1' },
    );
    expect(answer).toEqual({
      kind: 'results',
      results: [
        { assetId: 'a', status: 'trashed', version: 4 },
        { assetId: 'b', status: 'trashed', version: 5 },
      ],
    });
  });

  it('sends a reason the API accepts when the person did not write one', async () => {
    const bulkMutateAssets = vi.fn().mockResolvedValue({ data: [] });

    await trashAssets({ bulkMutateAssets } as never, items, '   ', 'key-2');

    const [request] = bulkMutateAssets.mock.calls[0] as [
      { action: unknown },
    ];
    expect(request.action).toEqual({
      kind: 'trash',
      reason: defaultTrashReason,
    });
    expect(defaultTrashReason.trim()).toBe(defaultTrashReason);
    expect(defaultTrashReason.length).toBeGreaterThan(0);
  });

  it('reports a queued answer instead of inventing per-asset results', async () => {
    const job = {
      jobId: 'job-1',
      state: 'queued',
      submittedCount: 2,
      submittedAt: '2026-01-01T00:00:00Z',
    };
    const bulkMutateAssets = vi.fn().mockResolvedValue({ data: job });

    await expect(
      trashAssets({ bulkMutateAssets } as never, items, 'why', 'key-3'),
    ).resolves.toEqual({ kind: 'queued', job });
  });

  it('maps every status the lifecycle route publishes', () => {
    expect(outcomeForTrashStatus('trashed')).toBe('updated');
    expect(outcomeForTrashStatus('alreadyTrashed')).toBe('unchanged');
    expect(outcomeForTrashStatus('versionConflict')).toBe('conflict');
    expect(outcomeForTrashStatus('notFound')).toBe('notFound');
    expect(outcomeForTrashStatus('invalidState')).toBe('failed');
    expect(outcomeForTrashStatus('something-else')).toBe('failed');
  });

  it('offers an undo only for the assets that reached the trash', () => {
    expect(
      restorableReferences([
        { assetId: 'a', status: 'trashed', version: 4 },
        { assetId: 'b', status: 'versionConflict', errorCode: 'x' },
        { assetId: 'c', status: 'trashed' },
        { assetId: 'd', status: 'alreadyTrashed', version: 7 },
      ]),
    ).toEqual([
      { id: 'a', version: 4 },
      { id: 'd', version: 7 },
    ]);
  });

  it('restores with the versions the trash answered with', async () => {
    const restoreAssets = vi.fn().mockResolvedValue({
      data: {
        jobId: 'job-2',
        state: 'queued',
        submittedCount: 1,
        submittedAt: '2026-01-01T00:00:00Z',
      },
    });

    const jobs = await restoreTrashedAssets(
      { restoreAssets } as never,
      [{ id: 'a', version: 4 }],
      () => 'key-4',
    );

    expect(restoreAssets).toHaveBeenCalledWith(
      { items: [{ id: 'a', version: 4 }] },
      { idempotencyKey: 'key-4' },
    );
    expect(jobs).toHaveLength(1);
    expect(jobs[0]?.submittedCount).toBe(1);
  });

  it('restores more references than one batch may carry', async () => {
    const restoreAssets = vi.fn(
      async (request: { items: readonly unknown[] }) => ({
        data: {
          jobId: 'job-3',
          state: 'queued',
          submittedCount: request.items.length,
          submittedAt: '2026-01-01T00:00:00Z',
        },
      }),
    );
    const references = Array.from({ length: 450 }, (_, index) => ({
      id: `asset-${index}`,
      version: 2,
    }));
    let key = 0;

    const jobs = await restoreTrashedAssets(
      { restoreAssets } as never,
      references,
      () => `key-${++key}`,
    );

    expect(jobs).toHaveLength(3);
    expect(jobs.reduce((total, job) => total + job.submittedCount, 0)).toBe(450);
    const keys = new Set<string>();
    for (const call of restoreAssets.mock.calls as unknown[][]) {
      const request = call[0] as { items: readonly unknown[] };
      const options = call[1] as { idempotencyKey: string };
      expect(request.items.length).toBeLessThanOrEqual(200);
      keys.add(options.idempotencyKey);
    }
    expect(keys.size).toBe(3);
  });
});
