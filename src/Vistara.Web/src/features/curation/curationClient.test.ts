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
      ]),
    ).toEqual([{ id: 'a', version: 4 }]);
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

    const job = await restoreTrashedAssets(
      { restoreAssets } as never,
      [{ id: 'a', version: 4 }],
      'key-4',
    );

    expect(restoreAssets).toHaveBeenCalledWith(
      { items: [{ id: 'a', version: 4 }] },
      { idempotencyKey: 'key-4' },
    );
    expect(job.submittedCount).toBe(1);
  });
});
