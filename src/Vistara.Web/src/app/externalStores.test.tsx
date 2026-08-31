import { render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  StorageSummary,
  StorageValidationRequest,
  StorageValidationResponse,
  UserPreferences,
} from '../api/platform';
import { AdminStoragePage, clearStorageDraft } from '../features/admin';
import { SessionProvider } from '../features/session';
import { currentUser } from '../features/session/sessionTestData';
import { SettingsPage } from '../features/settings';
import { resetPreferences } from './preferences';

/**
 * Publishing to an external store while another component renders makes React
 * warn, and the warning is the only sign that a subscriber may render with a
 * value the store has already moved past. These suites fail on any such
 * warning rather than letting it scroll past in the output.
 */
function captureReactWarnings() {
  const messages: string[] = [];
  const spy = vi
    .spyOn(console, 'error')
    .mockImplementation((...args: unknown[]) => {
      messages.push(args.map((value) => String(value)).join(' '));
    });

  return {
    messages,
    renderWarnings: () =>
      messages.filter(
        (message) =>
          message.includes('Cannot update a component') ||
          message.includes('while rendering a different component') ||
          message.includes('useSyncExternalStore') ||
          message.includes('Warning:'),
      ),
    restore: () => spy.mockRestore(),
  };
}

const preferences: UserPreferences = {
  density: 'compact',
  reducedMotion: true,
  screenReaderPagedMode: true,
  version: 3,
};

const summary: StorageSummary = {
  buckets: [],
  originalBytes: 0,
  derivativeBytes: 0,
  stagingBytes: 0,
  quotaBytes: 0,
  pendingUploadBytes: 0,
};

beforeEach(() => {
  clearStorageDraft();
  resetPreferences();
});

afterEach(() => {
  clearStorageDraft();
  resetPreferences();
  localStorage.clear();
  for (const attribute of ['density', 'reducedMotion', 'pagedMode']) {
    delete document.documentElement.dataset[attribute];
  }
});

describe('external store publishing', () => {
  it('applies account preferences without warning about a render update', async () => {
    const warnings = captureReactWarnings();
    const client = {
      getSession: vi.fn(async () => currentUser({}, 'TenantAdmin')),
      login: vi.fn(),
      logout: vi.fn(async () => undefined),
      listTenants: vi.fn(async () => ({ items: [] })),
      listApiKeys: vi.fn(async () => ({ items: [] })),
      createApiKey: vi.fn(),
      revokeApiKey: vi.fn(),
      getPreferences: vi.fn(async () => ({
        data: preferences,
        etag: '"v3"',
      })),
      updatePreferences: vi.fn(),
    };
    const router = createMemoryRouter(
      [{ path: '*', element: <SettingsPage client={client} /> }],
      { initialEntries: ['/settings'] },
    );

    render(
      <SessionProvider client={client}>
        <RouterProvider router={router} />
      </SessionProvider>,
    );

    await waitFor(() =>
      expect(document.documentElement.dataset.density).toBe('compact'),
    );
    expect(document.documentElement.dataset.pagedMode).toBe('true');

    warnings.restore();
    expect(warnings.renderWarnings()).toEqual([]);
  });

  it('reconciles the storage provider without warning about a render update', async () => {
    const warnings = captureReactWarnings();
    const client = {
      getStorageSummary: vi.fn(async () => summary),
      getStorageValidationSupport: vi.fn(async () => ({
        supported: true,
        providers: ['s3'] as const,
      })),
      validateStorage: vi.fn<
        (
          request: StorageValidationRequest,
          options?: { signal?: AbortSignal },
        ) => Promise<StorageValidationResponse>
      >(),
    };
    const router = createMemoryRouter(
      [{ path: '*', element: <AdminStoragePage client={client} /> }],
      { initialEntries: ['/admin/storage'] },
    );

    render(<RouterProvider router={router} />);

    const s3 = await screen.findByRole('radio', { name: /S3-compatible/ });
    await waitFor(() => expect(s3).toBeChecked());

    warnings.restore();
    expect(warnings.renderWarnings()).toEqual([]);
  });
});
