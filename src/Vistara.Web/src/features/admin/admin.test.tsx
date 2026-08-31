import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { Capabilities, JobStatus, TenantMember } from '../../api/platform';
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

const members: readonly TenantMember[] = [
  {
    userId: 'user-1',
    displayName: 'Ada Lovelace',
    email: 'ada@example.test',
    role: 'TenantAdmin',
    status: 'Active',
    invitedAt: '2026-01-01T00:00:00Z',
    joinedAt: '2026-01-02T00:00:00Z',
    version: 3,
  },
  {
    userId: 'user-2',
    displayName: 'Grace Hopper',
    email: 'grace@example.test',
    role: 'Member',
    status: 'Invited',
    invitedAt: '2026-01-05T00:00:00Z',
    version: 1,
  },
];

const capabilities: Capabilities = {
  schemaVersion: 1,
  database: { provider: 'postgres' },
  storage: {
    provider: 's3',
    directUpload: true,
    multipartUpload: true,
    rangeReads: true,
    maxObjectBytes: 4_000_000_000,
    maxMultipartParts: 10_000,
    minMultipartPartBytes: 5_000_000,
    maxMultipartPartBytes: 100_000_000,
  },
  imaging: {
    provider: 'skia',
    inputFormats: ['jpeg', 'png', 'webp'],
    outputFormats: ['jpeg', 'webp'],
    maxEncodedBytes: 50_000_000,
    maxWidth: 12_000,
    maxHeight: 12_000,
    maxAggregatePixels: 100_000_000,
    maxFrames: 1,
    maxEstimatedDecodedBytes: 500_000_000,
    processingDeadlineSeconds: 60,
    maxConcurrentTransforms: 4,
  },
  upload: {
    maxBytes: 1_000_000_000,
    maxConcurrentUploads: 4,
    concurrencyUnlimited: false,
    multipartThresholdBytes: 20_000_000,
    proxyUpload: true,
    directUpload: true,
    multipartUpload: true,
  },
  search: {
    text: true,
    facets: false,
    timeline: true,
    providerNativeFullText: true,
  },
  api: { defaultPageSize: 50, maxPageSize: 200, maxProxyUploadBytes: 100_000_000 },
};

describe('administration: people', () => {
  it('lists the members of the active tenant', async () => {
    const listTenantMembers = vi.fn(async () => ({ items: members }));
    renderRoute(
      <AdminUsersPage
        client={{ listTenantMembers, inviteTenantMember: vi.fn() }}
        tenantId="tenant-a"
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent('Loading people');

    const rows = await screen.findAllByRole('row');
    expect(within(rows[1]!).getByText('Ada Lovelace')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('Administrator')).toBeInTheDocument();
    expect(within(rows[2]!).getByText('Invited')).toBeInTheDocument();
    expect(listTenantMembers).toHaveBeenCalledWith('tenant-a');
  });

  it('retries after a failed load', async () => {
    const user = userEvent.setup();
    const listTenantMembers = vi
      .fn()
      .mockRejectedValueOnce(apiError(503))
      .mockResolvedValueOnce({ items: members });
    renderRoute(
      <AdminUsersPage
        client={{ listTenantMembers, inviteTenantMember: vi.fn() }}
        tenantId="tenant-a"
      />,
    );

    expect(
      await screen.findByRole('heading', { name: 'People are unavailable' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
  });

  it('invites a member and reloads the list', async () => {
    const user = userEvent.setup();
    const inviteTenantMember = vi.fn(async () => members[1]!);
    const listTenantMembers = vi.fn(async () => ({ items: members }));
    renderRoute(
      <AdminUsersPage
        client={{ listTenantMembers, inviteTenantMember }}
        tenantId="tenant-a"
      />,
    );

    await screen.findByText('Ada Lovelace');
    await user.type(
      screen.getByLabelText('Email address'),
      'grace@example.test',
    );
    await user.selectOptions(screen.getByLabelText('Role'), 'Member');
    await user.click(screen.getByRole('button', { name: 'Send invitation' }));

    await waitFor(() =>
      expect(inviteTenantMember).toHaveBeenCalledWith('tenant-a', {
        email: 'grace@example.test',
        role: 'Member',
      }),
    );
    await waitFor(() =>
      expect(listTenantMembers).toHaveBeenCalledTimes(2),
    );
    expect(await screen.findByRole('status')).toHaveTextContent(
      'Invitation sent to grace@example.test',
    );
  });

  it('separates a rejected invitation from a duplicate membership', async () => {
    const user = userEvent.setup();
    renderRoute(
      <AdminUsersPage
        client={{
          listTenantMembers: vi.fn(async () => ({ items: members })),
          inviteTenantMember: vi.fn(async () => {
            throw apiError(409);
          }),
        }}
        tenantId="tenant-a"
      />,
    );

    await screen.findByText('Ada Lovelace');
    await user.type(screen.getByLabelText('Email address'), 'ada@example.test');
    await user.click(screen.getByRole('button', { name: 'Send invitation' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'already a member',
    );
  });

  it('names the contract that role changes still need', async () => {
    renderRoute(
      <AdminUsersPage
        client={{
          listTenantMembers: vi.fn(async () => ({ items: members })),
          inviteTenantMember: vi.fn(),
        }}
        tenantId="tenant-a"
      />,
    );

    expect(
      await screen.findByText(/PATCH \/api\/v1\/tenants\/\{tenantId\}\/members/),
    ).toBeInTheDocument();
  });
});

describe('administration: storage', () => {
  it('reports the configured storage and upload limits', async () => {
    renderRoute(
      <AdminStoragePage
        client={{ getCapabilities: vi.fn(async () => capabilities) }}
      />,
    );

    expect(
      await screen.findByRole('heading', { name: 'Storage' }),
    ).toBeInTheDocument();
    expect(screen.getByText('s3')).toBeInTheDocument();
    expect(screen.getByText('4 GB')).toBeInTheDocument();
    expect(
      screen.getByText(/GET \/api\/v1\/admin\/storage/),
    ).toBeInTheDocument();
  });

  it('retries a failed capability read', async () => {
    const user = userEvent.setup();
    const getCapabilities = vi
      .fn()
      .mockRejectedValueOnce(apiError(503))
      .mockResolvedValueOnce(capabilities);
    renderRoute(<AdminStoragePage client={{ getCapabilities }} />);

    expect(
      await screen.findByRole('heading', { name: 'Storage is unavailable' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('s3')).toBeInTheDocument();
  });
});

const job: JobStatus = {
  id: '0195f0d4-0000-7000-8000-00000000job1',
  type: 'derivatives',
  state: 'Failed',
  attempts: 3,
  maxAttempts: 5,
  createdAt: '2026-02-10T08:00:00Z',
  availableAt: '2026-02-10T08:05:00Z',
  failure: { code: 'imaging.decode_failed', summary: 'The decoder rejected it.' },
  version: 4,
};

describe('administration: jobs', () => {
  it('looks a job up by identifier and keeps it in the address', async () => {
    const user = userEvent.setup();
    const getJob = vi.fn(async () => job);
    const router = renderRoute(
      <AdminJobsPage client={{ getJob }} />,
      '/admin/jobs',
    );

    await user.type(screen.getByLabelText('Job identifier'), 'job-1');
    await user.click(screen.getByRole('button', { name: 'Look up job' }));

    await waitFor(() => expect(getJob).toHaveBeenCalledWith('job-1'));
    expect(await screen.findByText('derivatives')).toBeInTheDocument();
    expect(screen.getByText('The decoder rejected it.')).toBeInTheDocument();
    expect(router.state.location.search).toBe('?job=job-1');
  });

  it('reads the job named in the address on arrival', async () => {
    const getJob = vi.fn(async () => job);
    renderRoute(<AdminJobsPage client={{ getJob }} />, '/admin/jobs?job=job-9');

    await waitFor(() => expect(getJob).toHaveBeenCalledWith('job-9'));
    expect(await screen.findByText('derivatives')).toBeInTheDocument();
  });

  it('separates an unknown job from an unavailable service', async () => {
    const user = userEvent.setup();
    renderRoute(
      <AdminJobsPage
        client={{
          getJob: vi.fn(async () => {
            throw apiError(404);
          }),
        }}
      />,
    );

    await user.type(screen.getByLabelText('Job identifier'), 'missing');
    await user.click(screen.getByRole('button', { name: 'Look up job' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'No job with that identifier',
    );
  });

  it('names the collection contract the queue view still needs', () => {
    renderRoute(<AdminJobsPage client={{ getJob: vi.fn() }} />);

    expect(screen.getByText(/GET \/api\/v1\/jobs\?states=/)).toBeInTheDocument();
  });
});

describe('administration: policies', () => {
  it('shows the enforced limits the deployment publishes', async () => {
    renderRoute(
      <AdminPoliciesPage
        client={{ getCapabilities: vi.fn(async () => capabilities) }}
      />,
    );

    expect(
      await screen.findByRole('heading', { name: 'Policies' }),
    ).toBeInTheDocument();
    expect(screen.getByText('200')).toBeInTheDocument();
    expect(
      screen.getByText(/PATCH \/api\/v1\/admin\/policies/),
    ).toBeInTheDocument();
  });

  it('offers no editing while the policy route is unavailable', async () => {
    renderRoute(
      <AdminPoliciesPage
        client={{ getCapabilities: vi.fn(async () => capabilities) }}
      />,
    );

    await screen.findByRole('heading', { name: 'Policies' });
    expect(screen.queryByRole('button', { name: 'Save policies' })).toBeNull();
  });
});

describe('administration: audit', () => {
  it('states that the audit route is not published yet and calls nothing', () => {
    renderRoute(<AdminAuditPage />, '/admin/audit');

    expect(
      screen.getByRole('heading', { name: 'Audit log' }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/GET \/api\/v1\/admin\/audit/),
    ).toBeInTheDocument();
  });
});
