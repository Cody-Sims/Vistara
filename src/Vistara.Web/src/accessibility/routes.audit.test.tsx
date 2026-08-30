import { render, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type {
  AdminJob,
  AdminUser,
  AuditEvent,
  PolicySettings,
  SessionSnapshot,
  StorageOverview,
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
import { SettingsPage } from '../features/settings';
import { auditAccessibilityTree } from './audit';

const session: SessionSnapshot = {
  user: {
    id: 'user-1',
    displayName: 'Ada Lovelace',
    email: 'ada@example.test',
    platformAdmin: true,
  },
  memberships: [
    {
      tenantId: 'tenant-1',
      tenantName: 'Studio',
      role: 'TenantAdmin',
      status: 'active',
    },
  ],
  activeTenantId: 'tenant-1',
  preferences: { theme: 'system', density: 'comfortable' },
};

const sessionClient = {
  getSession: vi.fn(async () => session),
  login: vi.fn(async () => session),
  logout: vi.fn(async () => undefined),
  updatePreferences: vi.fn(async () => session),
};

const person: AdminUser = {
  id: 'user-2',
  displayName: 'Grace Hopper',
  email: 'grace@example.test',
  role: 'Member',
  status: 'active',
  createdAt: '2026-01-01T00:00:00Z',
  version: 1,
};

const job: AdminJob = {
  id: 'job-1',
  kind: 'derivatives',
  state: 'failed',
  attempts: 2,
  maxAttempts: 5,
  queuedAt: '2026-02-01T00:00:00Z',
  lastError: 'Decoder rejected the source image.',
};

const storage: StorageOverview = {
  buckets: [
    {
      id: 'originals',
      kind: 's3',
      status: 'healthy',
      usedBytes: 1_000_000,
      quotaBytes: 4_000_000,
      objectCount: 12,
    },
  ],
  originalBytes: 1_000_000,
  derivativeBytes: 250_000,
  stagingBytes: 0,
};

const policies: PolicySettings = {
  retention: { trashRetentionDays: 30, purgeGraceDays: 7 },
  sharing: {
    publicLinksEnabled: true,
    maxLinkLifetimeDays: 30,
    requirePasswordForPublicLinks: false,
  },
  quotas: { concurrentUploads: 4 },
  version: 2,
};

const auditEvent: AuditEvent = {
  id: 'audit-1',
  occurredAt: '2026-02-01T00:00:00Z',
  actor: { kind: 'user', id: 'user-1', displayName: 'Ada Lovelace' },
  action: 'share.created',
  outcome: 'succeeded',
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
  {
    name: 'sign in',
    element: routed(
      <LoginPage
        capabilities={{
          getCapabilities: async () => ({
            authentication: {
              localAccounts: true,
              oidc: { displayName: 'Corp SSO', startPath: '/api/v1/auth/oidc' },
            },
          }),
        }}
      />,
      '/login',
    ),
  },
  { name: 'settings', element: routed(<SettingsPage />, '/settings') },
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
          listAdminUsers: async () => ({ data: { items: [person] } }),
          updateAdminUser: vi.fn(),
        }}
      />,
      '/admin/users',
    ),
  },
  {
    name: 'administration storage',
    element: routed(
      <AdminStoragePage
        client={{ getStorageOverview: async () => ({ data: storage }) }}
      />,
      '/admin/storage',
    ),
  },
  {
    name: 'administration jobs',
    element: routed(
      <AdminJobsPage
        client={{
          listAdminJobs: async () => ({ data: { items: [job] } }),
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
          getPolicies: async () => ({ data: policies, etag: '"2"' }),
          updatePolicies: vi.fn(),
        }}
      />,
      '/admin/policies',
    ),
  },
  {
    name: 'administration audit',
    element: routed(
      <AdminAuditPage
        client={{
          listAuditEvents: async () => ({ data: { items: [auditEvent] } }),
        }}
      />,
      '/admin/audit',
    ),
  },
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
