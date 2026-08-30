import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type {
  AdminJob,
  AdminUser,
  AuditEvent,
  PolicySettings,
  StorageOverview,
} from '../../api/platform';
import { AdminAuditPage } from './AdminAuditPage';
import { AdminJobsPage } from './AdminJobsPage';
import { AdminPoliciesPage } from './AdminPoliciesPage';
import { AdminStoragePage } from './AdminStoragePage';
import { AdminUsersPage } from './AdminUsersPage';

function apiError(status: number) {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: 'Failed',
    status,
    code: 'failed',
    errors: {},
  });
}

function renderRoute(element: React.ReactNode, entry = '/admin') {
  const router = createMemoryRouter([{ path: '*', element }], {
    initialEntries: [entry],
  });
  render(<RouterProvider router={router} />);
  return router;
}

const people: readonly AdminUser[] = [
  {
    id: 'user-1',
    displayName: 'Ada Lovelace',
    email: 'ada@example.test',
    role: 'TenantAdmin',
    status: 'active',
    createdAt: '2026-01-01T00:00:00Z',
    lastSeenAt: '2026-02-10T09:00:00Z',
    version: 3,
  },
  {
    id: 'user-2',
    displayName: 'Grace Hopper',
    email: 'grace@example.test',
    role: 'Member',
    status: 'invited',
    createdAt: '2026-01-05T00:00:00Z',
    version: 1,
  },
];

describe('administration: people', () => {
  it('lists members with their role and membership state', async () => {
    const listAdminUsers = vi.fn(async () => ({ data: { items: people } }));
    renderRoute(
      <AdminUsersPage
        client={{ listAdminUsers, updateAdminUser: vi.fn() }}
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent('Loading people');

    const rows = await screen.findAllByRole('row');
    expect(within(rows[1]!).getByText('Ada Lovelace')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('ada@example.test')).toBeInTheDocument();
    expect(within(rows[2]!).getByText('Invited')).toBeInTheDocument();
  });

  it('retries after a failed load', async () => {
    const user = userEvent.setup();
    const listAdminUsers = vi
      .fn()
      .mockRejectedValueOnce(apiError(503))
      .mockResolvedValueOnce({ data: { items: people } });
    renderRoute(
      <AdminUsersPage
        client={{ listAdminUsers, updateAdminUser: vi.fn() }}
      />,
    );

    expect(
      await screen.findByRole('heading', { name: 'People are unavailable' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
  });

  it('saves a role change with the row version and confirms it', async () => {
    const user = userEvent.setup();
    const updateAdminUser = vi.fn(async () => ({
      data: { ...people[1]!, role: 'TenantAdmin' as const, version: 2 },
    }));
    renderRoute(
      <AdminUsersPage
        client={{
          listAdminUsers: vi.fn(async () => ({ data: { items: people } })),
          updateAdminUser,
        }}
      />,
    );

    const select = await screen.findByLabelText('Role for Grace Hopper');
    await user.selectOptions(select, 'TenantAdmin');
    await user.click(
      screen.getByRole('button', { name: 'Save role for Grace Hopper' }),
    );

    await waitFor(() =>
      expect(updateAdminUser).toHaveBeenCalledWith(
        'user-2',
        { role: 'TenantAdmin' },
        { ifMatch: '"1"' },
      ),
    );
    expect(await screen.findByRole('status')).toHaveTextContent(
      'Grace Hopper is now an administrator',
    );
  });

  it('explains a conflicting role change instead of overwriting it', async () => {
    const user = userEvent.setup();
    renderRoute(
      <AdminUsersPage
        client={{
          listAdminUsers: vi.fn(async () => ({ data: { items: people } })),
          updateAdminUser: vi.fn(async () => {
            throw apiError(409);
          }),
        }}
      />,
    );

    const select = await screen.findByLabelText('Role for Grace Hopper');
    await user.selectOptions(select, 'Viewer');
    await user.click(
      screen.getByRole('button', { name: 'Save role for Grace Hopper' }),
    );

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'changed somewhere else',
    );
  });
});

const storage: StorageOverview = {
  buckets: [
    {
      id: 'originals',
      kind: 's3',
      status: 'healthy',
      usedBytes: 4_000_000_000,
      quotaBytes: 10_000_000_000,
      objectCount: 1200,
      lastCheckedAt: '2026-02-10T09:00:00Z',
    },
    {
      id: 'derivatives',
      kind: 'filesystem',
      status: 'degraded',
      usedBytes: 500_000_000,
      objectCount: 8000,
      message: 'Disk is nearly full.',
    },
  ],
  originalBytes: 4_000_000_000,
  derivativeBytes: 500_000_000,
  stagingBytes: 0,
  quotaBytes: 10_000_000_000,
};

describe('administration: storage', () => {
  it('summarises usage and flags an unhealthy bucket', async () => {
    renderRoute(
      <AdminStoragePage client={{ getStorageOverview: vi.fn(async () => ({ data: storage })) }} />,
    );

    expect(
      await screen.findByRole('heading', { name: 'Storage' }),
    ).toBeInTheDocument();
    const buckets = screen.getByRole('list', { name: 'Storage buckets' });
    expect(within(buckets).getByText('4 GB')).toBeInTheDocument();
    expect(within(buckets).getByText('Disk is nearly full.')).toBeInTheDocument();
    expect(within(buckets).getByText('Degraded')).toBeInTheDocument();
  });

  it('reports an empty deployment without pretending it failed', async () => {
    renderRoute(
      <AdminStoragePage
        client={{
          getStorageOverview: vi.fn(async () => ({
            data: {
              buckets: [],
              originalBytes: 0,
              derivativeBytes: 0,
              stagingBytes: 0,
            },
          })),
        }}
      />,
    );

    expect(
      await screen.findByText('No storage buckets are configured yet.'),
    ).toBeInTheDocument();
  });
});

const jobs: readonly AdminJob[] = [
  {
    id: 'job-1',
    kind: 'derivatives',
    state: 'failed',
    attempts: 3,
    maxAttempts: 5,
    queuedAt: '2026-02-10T08:00:00Z',
    lastError: 'Decoder rejected the source image.',
  },
  {
    id: 'job-2',
    kind: 'purge',
    state: 'queued',
    attempts: 0,
    queuedAt: '2026-02-10T08:30:00Z',
  },
];

describe('administration: jobs', () => {
  it('filters by state through the address', async () => {
    const user = userEvent.setup();
    const listAdminJobs = vi.fn(async () => ({ data: { items: jobs } }));
    const router = renderRoute(
      <AdminJobsPage
        client={{ listAdminJobs, retryJob: vi.fn(), cancelJob: vi.fn() }}
      />,
      '/admin/jobs',
    );

    await screen.findByText('derivatives');
    await user.selectOptions(
      screen.getByLabelText('Show jobs'),
      'failed',
    );

    await waitFor(() =>
      expect(router.state.location.search).toBe('?state=failed'),
    );
    expect(listAdminJobs).toHaveBeenLastCalledWith(
      expect.objectContaining({ states: ['failed'] }),
    );
  });

  it('retries a failed job and refreshes the queue', async () => {
    const user = userEvent.setup();
    const retryJob = vi.fn(async () => ({
      data: { ...jobs[0]!, state: 'queued' as const },
    }));
    const listAdminJobs = vi.fn(async () => ({ data: { items: jobs } }));
    renderRoute(
      <AdminJobsPage
        client={{ listAdminJobs, retryJob, cancelJob: vi.fn() }}
      />,
    );

    await user.click(
      await screen.findByRole('button', { name: 'Retry derivatives job' }),
    );

    await waitFor(() => expect(retryJob).toHaveBeenCalledWith('job-1'));
    await waitFor(() => expect(listAdminJobs).toHaveBeenCalledTimes(2));
  });

  it('keeps the queue visible when an action fails', async () => {
    const user = userEvent.setup();
    renderRoute(
      <AdminJobsPage
        client={{
          listAdminJobs: vi.fn(async () => ({ data: { items: jobs } })),
          retryJob: vi.fn(async () => {
            throw apiError(500);
          }),
          cancelJob: vi.fn(),
        }}
      />,
    );

    await user.click(
      await screen.findByRole('button', { name: 'Retry derivatives job' }),
    );

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'could not be retried',
    );
    expect(screen.getByText('purge')).toBeInTheDocument();
  });
});

const policies: PolicySettings = {
  retention: { trashRetentionDays: 30, purgeGraceDays: 7 },
  sharing: {
    publicLinksEnabled: true,
    maxLinkLifetimeDays: 30,
    requirePasswordForPublicLinks: false,
  },
  quotas: { storageBytes: 10_000_000_000, concurrentUploads: 4 },
  version: 7,
};

describe('administration: policies', () => {
  it('loads the current policy into an editable form', async () => {
    renderRoute(
      <AdminPoliciesPage
        client={{
          getPolicies: vi.fn(async () => ({
            data: policies,
            etag: '"7"',
          })),
          updatePolicies: vi.fn(),
        }}
      />,
    );

    expect(await screen.findByLabelText('Trash retention (days)')).toHaveValue(
      30,
    );
    expect(screen.getByLabelText('Allow public links')).toBeChecked();
  });

  it('saves with the loaded version and confirms', async () => {
    const user = userEvent.setup();
    const updatePolicies = vi.fn(async () => ({ data: policies }));
    renderRoute(
      <AdminPoliciesPage
        client={{
          getPolicies: vi.fn(async () => ({ data: policies, etag: '"7"' })),
          updatePolicies,
        }}
      />,
    );

    const retention = await screen.findByLabelText('Trash retention (days)');
    await user.clear(retention);
    await user.type(retention, '45');
    await user.click(screen.getByRole('button', { name: 'Save policies' }));

    await waitFor(() =>
      expect(updatePolicies).toHaveBeenCalledWith(
        expect.objectContaining({
          retention: expect.objectContaining({ trashRetentionDays: 45 }),
        }),
        { ifMatch: '"7"' },
      ),
    );
    expect(await screen.findByRole('status')).toHaveTextContent(
      'Policies saved',
    );
  });

  it('refuses to overwrite a policy that changed elsewhere', async () => {
    const user = userEvent.setup();
    renderRoute(
      <AdminPoliciesPage
        client={{
          getPolicies: vi.fn(async () => ({ data: policies, etag: '"7"' })),
          updatePolicies: vi.fn(async () => {
            throw apiError(409);
          }),
        }}
      />,
    );

    await user.click(
      await screen.findByRole('button', { name: 'Save policies' }),
    );

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'changed somewhere else',
    );
    expect(
      screen.getByRole('button', { name: 'Reload policies' }),
    ).toBeInTheDocument();
  });
});

const auditEvents: readonly AuditEvent[] = [
  {
    id: 'audit-1',
    occurredAt: '2026-02-10T09:15:00Z',
    actor: { kind: 'user', id: 'user-1', displayName: 'Ada Lovelace' },
    action: 'share.created',
    outcome: 'succeeded',
    resourceType: 'share',
    resourceId: 'share-1',
  },
  {
    id: 'audit-2',
    occurredAt: '2026-02-10T09:20:00Z',
    actor: { kind: 'apiKey', displayName: 'Backup key' },
    action: 'asset.purged',
    outcome: 'denied',
  },
];

describe('administration: audit', () => {
  it('lists recorded events with actor and outcome', async () => {
    renderRoute(
      <AdminAuditPage
        client={{
          listAuditEvents: vi.fn(async () => ({
            data: { items: auditEvents },
          })),
        }}
      />,
      '/admin/audit',
    );

    expect(await screen.findByText('share.created')).toBeInTheDocument();
    const events = screen.getByRole('list', { name: 'Audit events' });
    expect(within(events).getByText('Backup key')).toBeInTheDocument();
    expect(within(events).getByText('Denied')).toBeInTheDocument();
  });

  it('keeps the outcome filter in the address', async () => {
    const user = userEvent.setup();
    const listAuditEvents = vi.fn(async () => ({
      data: { items: auditEvents },
    }));
    const router = renderRoute(
      <AdminAuditPage client={{ listAuditEvents }} />,
      '/admin/audit',
    );

    await screen.findByText('share.created');
    await user.selectOptions(screen.getByLabelText('Outcome'), 'denied');

    await waitFor(() =>
      expect(router.state.location.search).toBe('?outcome=denied'),
    );
    expect(listAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ outcome: 'denied' }),
    );
  });

  it('continues from the returned cursor', async () => {
    const user = userEvent.setup();
    const listAuditEvents = vi
      .fn()
      .mockResolvedValueOnce({
        data: { items: [auditEvents[0]!], nextCursor: 'cursor-2' },
      })
      .mockResolvedValueOnce({ data: { items: [auditEvents[1]!] } });
    renderRoute(<AdminAuditPage client={{ listAuditEvents }} />);

    await user.click(
      await screen.findByRole('button', { name: 'Show earlier events' }),
    );

    expect(await screen.findByText('asset.purged')).toBeInTheDocument();
    expect(screen.getByText('share.created')).toBeInTheDocument();
  });

  it('reports an empty audit log', async () => {
    renderRoute(
      <AdminAuditPage
        client={{ listAuditEvents: vi.fn(async () => ({ data: { items: [] } })) }}
      />,
    );

    expect(
      await screen.findByText('No audit events match these filters.'),
    ).toBeInTheDocument();
  });
});
