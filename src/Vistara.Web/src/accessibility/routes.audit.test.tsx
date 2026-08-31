import { render, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type {
  ApiKeyCollection,
  JobStatus,
  TenantCollection,
  TenantMemberCollection,
} from '../api/platform';
import {
  AdminAuditPage,
  AdminJobsPage,
  AdminPoliciesPage,
  AdminStoragePage,
  AdminUsersPage,
} from '../features/admin';
import { SearchView } from '../features/search';
import { LoginPage, SessionProvider } from '../features/session';
import { SetupPage } from '../features/session/SetupPage';
import { currentUser } from '../features/session/sessionTestData';
import { SettingsPage } from '../features/settings';
import { auditAccessibilityTree } from './audit';

const sessionClient = {
  getSession: vi.fn(async () => currentUser({}, 'TenantAdmin')),
  login: vi.fn(),
  logout: vi.fn(async () => undefined),
};

const members: TenantMemberCollection = {
  items: [
    {
      userId: 'user-2',
      email: 'grace@example.test',
      displayName: 'Grace Hopper',
      role: 'Member',
      status: 'Active',
      invitedAt: '2026-01-01T00:00:00Z',
      version: 1,
    },
  ],
};

const job: JobStatus = {
  id: 'job-1',
  type: 'derivatives',
  state: 'completed',
  attempts: 1,
  maxAttempts: 3,
  createdAt: '2026-01-01T00:00:00Z',
  availableAt: '2026-01-01T00:00:00Z',
  version: 1,
  actions: { retry: false, cancel: false },
};

const tenants: TenantCollection = {
  items: [
    {
      id: 'tenant-a',
      slug: 'studio',
      name: 'Studio',
      status: 'Active',
      role: 'TenantAdmin',
      membershipStatus: 'Active',
    },
  ],
};

const apiKeys: ApiKeyCollection = {
  items: [
    {
      id: 'key-1',
      prefix: 'vst_abc',
      ownerId: 'user-1',
      scopes: ['assets.read'],
      status: 'Active',
      createdAt: '2026-01-01T00:00:00Z',
    },
  ],
};

const settingsClient = {
  listTenants: vi.fn(async () => tenants),
  listApiKeys: vi.fn(async () => apiKeys),
  createApiKey: vi.fn(),
  revokeApiKey: vi.fn(),
  getPreferences: vi.fn(async () => ({
    data: {
      density: 'comfortable' as const,
      reducedMotion: false,
      screenReaderPagedMode: false,
      version: 1,
    },
    etag: '"v1"',
  })),
  updatePreferences: vi.fn(),
};

function routed(element: React.ReactNode, entry: string) {
  const router = createMemoryRouter([{ path: '*', element }], {
    initialEntries: [entry],
  });

  return (
    <SessionProvider client={sessionClient}>
      <RouterProvider router={router} />
    </SessionProvider>
  );
}

const pages: readonly { name: string; element: React.ReactNode }[] = [
  { name: 'sign in', element: routed(<LoginPage />, '/login') },
  {
    name: 'first-run setup',
    element: routed(
      <SetupPage client={{ provisionFirstOwner: vi.fn() }} />,
      '/setup',
    ),
  },
  {
    name: 'settings',
    element: routed(<SettingsPage client={settingsClient} />, '/settings'),
  },
  {
    name: 'search',
    element: routed(
      <SearchView
        client={{ listAssets: async () => ({ data: { items: [] } }) }}
      />,
      '/search?q=harbour',
    ),
  },
  {
    name: 'administration people',
    element: routed(
      <AdminUsersPage
        client={{
          listTenantMembers: async () => members,
          inviteTenantMember: vi.fn(),
          updateTenantMember: vi.fn(),
        }}
        tenantId="tenant-a"
      />,
      '/admin/users',
    ),
  },
  {
    name: 'administration storage',
    element: routed(
      <AdminStoragePage
        client={{
          getStorageSummary: async () => ({
            buckets: [],
            originalBytes: 0,
            derivativeBytes: 0,
            stagingBytes: 0,
            quotaBytes: 0,
            pendingUploadBytes: 0,
          }),
          validateStorage: vi.fn(),
          getStorageValidationSupport: async () => ({
            supported: true,
            providers: ['filesystem', 'azureBlob', 's3'] as const,
          }),
        }}
      />,
      '/admin/storage',
    ),
  },
  {
    name: 'administration jobs',
    element: routed(
      <AdminJobsPage
        client={{
          listJobs: async () => ({ items: [job] }),
          retryJob: vi.fn(),
          cancelJob: vi.fn(),
        }}
      />,
      '/admin/jobs',
    ),
  },
  {
    name: 'administration policies',
    element: routed(
      <AdminPoliciesPage
        client={{
          getPolicies: async () => ({
            retention: { trashRetentionDays: 30, purgeGraceDays: 7 },
            sharing: {
              publicLinksEnabled: true,
              maxLinkLifetimeDays: 30,
              requirePasswordForPublicLinks: false,
            },
            quotas: {
              storageBytes: null,
              dailyTransformPixels: null,
              concurrentUploads: 4,
            },
            version: 3,
          }),
        }}
      />,
      '/admin/policies',
    ),
  },
  { name: 'administration audit', element: routed(<AdminAuditPage />, '/admin/audit') },
];

describe('route accessibility baseline', () => {
  for (const page of pages) {
    it(`keeps the ${page.name} route free of serious findings`, async () => {
      const { container } = render(page.element);

      await waitFor(() =>
        expect(container.querySelector('h1')).toBeInTheDocument(),
      );

      const findings = auditAccessibilityTree(container).filter(
        ({ impact }) => impact === 'serious' || impact === 'critical',
      );

      expect(findings.map((finding) => finding.message)).toEqual([]);
    });
  }
});
