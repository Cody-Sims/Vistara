import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated';
import type {
  PurgeBatch,
  PurgeDryRun,
  TrashAsset,
} from '../../api/generated/models';
import { TrashManager } from './TrashManager';

const NOW = new Date('2026-08-29T17:00:00.000Z');

function trashedAsset(overrides: Partial<TrashAsset> = {}): TrashAsset {
  return {
    asset: {
      id: 'asset-1',
      title: 'Old portrait',
      status: 'trashed',
      visibility: 'private',
      revisionNumber: 7,
      contentType: 'image/jpeg',
      format: 'jpeg',
      width: 1000,
      height: 800,
      sizeBytes: 2048,
      importedAt: '2026-07-01T00:00:00.000Z',
      updatedAt: '2026-08-20T00:00:00.000Z',
      favorite: false,
      tags: [],
      renditions: [],
      version: 9,
    },
    deletedAt: '2026-08-20T00:00:00.000Z',
    purgeAt: '2026-09-19T00:00:00.000Z',
    reason: 'Duplicate',
    activeHoldCount: 1,
    blockingReferenceCount: 2,
    estimatedReclaimBytes: 2048,
    ...overrides,
  };
}

const dryRun: PurgeDryRun = {
  batchId: 'batch-1',
  state: 'dryRunCompleted',
  dryRunDigest: 'digest',
  expiresAt: '2026-08-29T17:10:00.000Z',
  candidateCount: 2,
  eligibleCount: 1,
  estimatedReclaimBytes: 3072,
  items: [
    {
      assetId: 'asset-1',
      revisionNumber: 7,
      title: 'Old portrait',
      eligible: false,
      barriers: ['activeHold', 'blockingReference'],
      sharedLinkImpact: 3,
      estimatedReclaimBytes: 2048,
    },
    {
      assetId: 'asset-2',
      revisionNumber: 3,
      title: 'Expired draft',
      eligible: true,
      barriers: [],
      sharedLinkImpact: 0,
      estimatedReclaimBytes: 1024,
    },
  ],
  version: 5,
};

function setupClient() {
  const eligibleAsset = trashedAsset({
    asset: {
      ...trashedAsset().asset,
      id: 'asset-2',
      title: 'Expired draft',
      revisionNumber: 3,
    },
    activeHoldCount: 0,
    blockingReferenceCount: 0,
    estimatedReclaimBytes: 1024,
  });
  return {
    listTrash: vi.fn().mockResolvedValue({
      data: { items: [trashedAsset(), eligibleAsset] },
    }),
    restoreAssets: vi.fn().mockResolvedValue({
      data: {
        jobId: 'restore-job',
        state: 'queued',
        submittedCount: 1,
        submittedAt: NOW.toISOString(),
      },
    }),
    dryRunPurge: vi.fn().mockResolvedValue({ data: dryRun }),
    confirmPurge: vi.fn(),
    getPurgeStatus: vi.fn(),
  };
}

describe('trash', () => {
  it('shows retention and holds and restores through an undo action', async () => {
    const user = userEvent.setup();
    const client = setupClient();

    render(
      <TrashManager
        client={client}
        now={() => NOW}
        createIdempotencyKey={() => 'restore-key'}
        reauthenticate={vi.fn()}
      />,
    );

    const item = await screen.findByRole('article', { name: 'Old portrait' });
    expect(within(item).getByText(/Deleted Aug 20, 2026/)).toBeInTheDocument();
    expect(within(item).getByText(/Scheduled purge Sep 19, 2026/)).toBeInTheDocument();
    expect(within(item).getByText('1 active hold')).toBeInTheDocument();
    expect(within(item).getByText('2 blocking references')).toBeInTheDocument();

    await user.click(
      within(item).getByRole('button', { name: 'Undo deletion' }),
    );

    expect(client.restoreAssets).toHaveBeenCalledWith(
      { items: [{ id: 'asset-1', version: 9 }] },
      { idempotencyKey: 'restore-key' },
    );
    expect(await screen.findByRole('status')).toHaveTextContent(
      'Restore queued for 1 item.',
    );
  });

  it('requires a dry run, reauthentication, and typed confirmation before purge', async () => {
    const user = userEvent.setup();
    const client = setupClient();
    const reauthenticate = vi.fn().mockResolvedValue(true);
    client.confirmPurge.mockResolvedValue({
      data: {
        batchId: 'batch-1',
        state: 'executing',
        requestedAt: NOW.toISOString(),
        approvedAt: NOW.toISOString(),
        startedAt: NOW.toISOString(),
        candidateCount: 2,
        eligibleCount: 1,
        processedCount: 0,
        reclaimedBytes: 0,
        items: [],
        version: 6,
      } satisfies PurgeBatch,
    });

    render(
      <TrashManager
        client={client}
        now={() => NOW}
        createIdempotencyKey={() => 'purge-key'}
        reauthenticate={reauthenticate}
      />,
    );

    await user.click(await screen.findByLabelText('Select Old portrait'));
    await user.click(screen.getByLabelText('Select Expired draft'));
    await user.click(screen.getByRole('button', { name: 'Review permanent deletion' }));

    expect(client.dryRunPurge).toHaveBeenCalledWith(
      {
        phase: 'dryRun',
        items: [
          { id: 'asset-1', version: 9 },
          { id: 'asset-2', version: 9 },
        ],
      },
      { idempotencyKey: 'purge-key' },
    );
    const review = await screen.findByRole('region', {
      name: 'Permanent deletion impact',
    });
    expect(within(review).getByText(/3\s+active share links/)).toBeInTheDocument();
    expect(within(review).getByText('Active hold')).toBeInTheDocument();
    expect(within(review).getByText('Blocking reference')).toBeInTheDocument();
    expect(within(review).getByText(/AI suggestions cannot approve/)).toBeInTheDocument();

    await user.click(
      within(review).getByRole('button', { name: 'Continue to confirmation' }),
    );
    expect(reauthenticate).toHaveBeenCalledOnce();

    const confirm = await screen.findByRole('dialog', {
      name: 'Confirm permanent deletion',
    });
    const confirmButton = within(confirm).getByRole('button', {
      name: 'Permanently delete',
    });
    expect(confirmButton).toBeDisabled();

    await user.type(
      within(confirm).getByLabelText('Type PURGE 1 ITEMS to confirm'),
      'PURGE 1 ITEMS',
    );
    expect(confirmButton).toBeEnabled();
    await user.click(confirmButton);

    expect(client.confirmPurge).toHaveBeenCalledWith(
      'batch-1',
      {
        dryRunDigest: 'digest',
        acknowledgePermanentDeletion: true,
      },
      { idempotencyKey: 'purge-key', ifMatch: '"v5"' },
    );
    expect(await screen.findByRole('status')).toHaveTextContent(
      'Permanent deletion is executing: 0 of 2 processed.',
    );
  });

  it('reloads purge status after an ETag conflict', async () => {
    const user = userEvent.setup();
    const client = setupClient();
    client.confirmPurge.mockRejectedValue(
      new VistaraApiError(412, {
        type: 'about:blank',
        title: 'Precondition failed',
        status: 412,
        code: 'etag_mismatch',
        errors: {},
      }),
    );
    client.getPurgeStatus.mockResolvedValue({
      data: {
        batchId: 'batch-1',
        state: 'approved',
        requestedAt: NOW.toISOString(),
        approvedAt: NOW.toISOString(),
        candidateCount: 2,
        eligibleCount: 1,
        processedCount: 0,
        reclaimedBytes: 0,
        items: [],
        version: 6,
      } satisfies PurgeBatch,
    });

    render(
      <TrashManager
        client={client}
        now={() => NOW}
        createIdempotencyKey={() => 'purge-key'}
        reauthenticate={vi.fn().mockResolvedValue(true)}
      />,
    );

    await user.click(await screen.findByLabelText('Select Old portrait'));
    await user.click(screen.getByLabelText('Select Expired draft'));
    await user.click(screen.getByRole('button', { name: 'Review permanent deletion' }));
    await user.click(
      (await screen.findByRole('region', {
        name: 'Permanent deletion impact',
      })).querySelector('button')!,
    );
    const dialog = await screen.findByRole('dialog');
    await user.type(
      within(dialog).getByLabelText('Type PURGE 1 ITEMS to confirm'),
      'PURGE 1 ITEMS',
    );
    await user.click(
      within(dialog).getByRole('button', { name: 'Permanently delete' }),
    );

    expect(client.getPurgeStatus).toHaveBeenCalledWith('batch-1');
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'This purge changed elsewhere. Its latest status has been loaded.',
    );
    expect(screen.getByRole('status')).toHaveTextContent(
      'Permanent deletion is approved: 0 of 2 processed.',
    );
  });
});
