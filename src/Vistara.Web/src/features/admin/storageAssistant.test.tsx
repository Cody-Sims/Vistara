import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type {
  StorageSummary,
  StorageValidationRequest,
  StorageValidationResponse,
} from '../../api/platform';
import { AdminStoragePage } from './AdminStoragePage';
import { clearStorageDraft, storageDraftSecrets } from './storageDraft';

const secret = 's3-secret-access-key-value';

const summary: StorageSummary = {
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
      quotaBytes: 0,
      objectCount: 8000,
      lastCheckedAt: '2026-02-10T09:00:00Z',
      message: 'Disk is nearly full.',
    },
  ],
  originalBytes: 4_000_000_000,
  derivativeBytes: 500_000_000,
  stagingBytes: 0,
  quotaBytes: 10_000_000_000,
  pendingUploadBytes: 12_000_000,
};

function apiError(status: number, code = 'failed') {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: code,
    status,
    code,
    errors: {},
  });
}

interface Options {
  readonly getStorageSummary?: () => Promise<StorageSummary>;
  readonly validateStorage?: (
    request: StorageValidationRequest,
  ) => Promise<StorageValidationResponse>;
  readonly getStorageValidationSupport?: () => Promise<{
    supported: boolean;
  }>;
}

function renderStorage(options: Options = {}) {
  const client = {
    getStorageSummary: vi.fn(
      options.getStorageSummary ?? (async () => summary),
    ),
    getStorageValidationSupport: vi.fn(
      options.getStorageValidationSupport ?? (async () => ({ supported: true })),
    ),
    validateStorage: vi.fn(
      options.validateStorage ??
        (async () => ({
          valid: true,
          provider: 's3' as const,
          checks: [
            { id: 'reachable' as const, status: 'passed' as const },
            { id: 'write' as const, status: 'passed' as const },
          ],
        })),
    ),
  };
  const router = createMemoryRouter(
    [{ path: '*', element: <AdminStoragePage client={client} /> }],
    { initialEntries: ['/admin/storage'] },
  );

  const view = render(<RouterProvider router={router} />);
  return { client, view };
}

async function fillS3(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('radio', { name: /S3-compatible/ }));
  await user.type(
    screen.getByLabelText('Endpoint URL'),
    'https://s3.eu-central-1.example',
  );
  await user.type(screen.getByLabelText('Region'), 'eu-central-1');
  await user.type(screen.getByLabelText('Bucket'), 'vistara-media');
  await user.type(screen.getByLabelText('Access key ID'), 'AKIAEXAMPLE');
  await user.type(screen.getByLabelText('Secret access key'), secret);
}

beforeEach(() => {
  clearStorageDraft();
});

afterEach(() => {
  clearStorageDraft();
  localStorage.clear();
  sessionStorage.clear();
});

describe('storage status', () => {
  it('reports published consumption and flags an unhealthy bucket', async () => {
    renderStorage();

    const buckets = await screen.findByRole('list', { name: 'Storage buckets' });
    expect(within(buckets).getByText('4 GB')).toBeInTheDocument();
    expect(within(buckets).getByText('Degraded')).toBeInTheDocument();
    expect(within(buckets).getByText('Disk is nearly full.')).toBeInTheDocument();
  });

  it('retries a failed status read without losing the assistant', async () => {
    const user = userEvent.setup();
    const getStorageSummary = vi
      .fn()
      .mockRejectedValueOnce(apiError(503))
      .mockResolvedValueOnce(summary);
    renderStorage({ getStorageSummary });

    expect(
      await screen.findByRole('heading', { name: 'Storage status is unavailable' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('radio', { name: /Local filesystem/ }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('Disk is nearly full.')).toBeInTheDocument();
  });
});

describe('provider assistant', () => {
  it('offers the three supported providers as a keyboard-reachable choice', async () => {
    renderStorage();

    const providers = await screen.findByRole('radiogroup', {
      name: 'Storage provider',
    });
    for (const name of [
      /Local filesystem/,
      /Azure Blob Storage/,
      /S3-compatible/,
    ]) {
      expect(within(providers).getByRole('radio', { name })).toBeInTheDocument();
    }
    expect(
      within(providers).getByRole('radio', { name: /Local filesystem/ }),
    ).toBeChecked();
  });

  it('shows Azure naming and least-privilege guidance with masked credentials', async () => {
    const user = userEvent.setup();
    renderStorage();

    await user.click(
      await screen.findByRole('radio', { name: /Azure Blob Storage/ }),
    );

    expect(screen.getByLabelText('Storage account name')).toBeInTheDocument();
    expect(screen.getByLabelText('Container name')).toBeInTheDocument();
    expect(screen.getByLabelText('Subscription ID')).toBeInTheDocument();
    expect(screen.getByLabelText('Resource group')).toBeInTheDocument();
    expect(screen.getByText(/3 to 24 characters/)).toBeInTheDocument();
    expect(screen.getByText(/Storage Blob Data Contributor/)).toBeInTheDocument();
    expect(screen.getByLabelText('Account key')).toHaveAttribute(
      'type',
      'password',
    );
  });

  it('checks the configuration in the browser before sending a credential', async () => {
    const user = userEvent.setup();
    const { client } = renderStorage();

    await user.click(
      await screen.findByRole('radio', { name: /S3-compatible/ }),
    );
    await user.click(screen.getByRole('button', { name: 'Test connection' }));

    const problems = await screen.findByRole('alert', {
      name: 'Configuration problems',
    });
    expect(
      within(problems).getByText(/Enter the endpoint URL/),
    ).toBeInTheDocument();
    expect(
      within(problems).getByText(/Enter the secret access key/),
    ).toBeInTheDocument();
    expect(client.validateStorage).not.toHaveBeenCalled();
  });

  it('tests a connection and reports each check', async () => {
    const user = userEvent.setup();
    const { client } = renderStorage();

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));

    await waitFor(() =>
      expect(client.validateStorage).toHaveBeenCalledWith({
        provider: 's3',
        s3: {
          endpoint: 'https://s3.eu-central-1.example',
          region: 'eu-central-1',
          bucket: 'vistara-media',
          accessKeyId: 'AKIAEXAMPLE',
          secretAccessKey: secret,
          forcePathStyle: true,
        },
      }),
    );
    expect(await screen.findByText('Connection succeeded.')).toBeInTheDocument();
    const checks = screen.getByRole('list', { name: 'Connection checks' });
    expect(within(checks).getAllByRole('listitem')).toHaveLength(2);
  });

  it('reports a rejected connection without echoing the credential', async () => {
    const user = userEvent.setup();
    renderStorage({
      validateStorage: async () => ({
        valid: false,
        provider: 's3',
        checks: [
          { id: 'reachable', status: 'passed' },
          {
            id: 'authenticated',
            status: 'failed',
            detail: 'The credential was rejected by the provider.',
          },
        ],
        message: 'Storage rejected the credential.',
      }),
    });

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Storage rejected the credential.');
    expect(alert.textContent).not.toContain(secret);
    expect(document.body.innerHTML).not.toContain(secret);
  });

  it('never sends a credential to a deployment that cannot validate', async () => {
    const user = userEvent.setup();
    const { client } = renderStorage({
      getStorageValidationSupport: async () => {
        throw apiError(404, 'not_found');
      },
    });

    await fillS3(user);

    const test = await screen.findByRole('button', {
      name: 'Test connection',
    });
    await waitFor(() => expect(test).toBeDisabled());
    expect(
      screen.getByText(/cannot test storage connections yet/),
    ).toBeInTheDocument();
    expect(client.validateStorage).not.toHaveBeenCalled();
  });

  it('reports a validation route that answers with a failure', async () => {
    const user = userEvent.setup();
    renderStorage({
      validateStorage: async () => {
        throw apiError(500);
      },
    });

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'could not be tested',
    );
  });

  it('announces local problems and moves focus to the first one', async () => {
    const user = userEvent.setup();
    renderStorage();

    await user.click(
      await screen.findByRole('radio', { name: /S3-compatible/ }),
    );
    await user.click(screen.getByRole('button', { name: 'Test connection' }));

    const problems = await screen.findByRole('alert', {
      name: 'Configuration problems',
    });
    expect(problems).toHaveTextContent(/Enter the endpoint URL/);
    await waitFor(() =>
      expect(screen.getByLabelText('Endpoint URL')).toHaveFocus(),
    );
  });
});

describe('deploy template', () => {
  it('can be generated after a test has cleared the credentials', async () => {
    const user = userEvent.setup();
    renderStorage();

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));
    await screen.findByText('Connection succeeded.');
    expect(screen.getByLabelText('Secret access key')).toHaveValue('');

    await user.click(
      screen.getByRole('button', { name: 'Generate deploy template' }),
    );

    const template = (await screen.findByLabelText(
      'Deploy template',
    )) as HTMLTextAreaElement;
    expect(template.value).toContain(
      'VISTARA_STORAGE__S3__BUCKET=vistara-media',
    );
    expect(template.value).not.toContain(secret);
  });

  it('withdraws a template that no longer matches the form', async () => {
    const user = userEvent.setup();
    renderStorage();

    await fillS3(user);
    await user.click(
      screen.getByRole('button', { name: 'Generate deploy template' }),
    );
    await screen.findByLabelText('Deploy template');

    await user.type(screen.getByLabelText('Bucket'), '-two');

    expect(screen.queryByLabelText('Deploy template')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Download template' }),
    ).not.toBeInTheDocument();
  });

  it('generates a redacted template and offers copy and download', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn<(value: string) => Promise<void>>(
      async () => undefined,
    );
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    renderStorage();

    await fillS3(user);
    await user.click(
      screen.getByRole('button', { name: 'Generate deploy template' }),
    );

    const template = (await screen.findByLabelText(
      'Deploy template',
    )) as HTMLTextAreaElement;
    expect(template.value).toContain(
      'VISTARA_STORAGE__S3__BUCKET=vistara-media',
    );
    expect(template.value).not.toContain(secret);
    expect(template.value).toContain('restart the API and worker');
    expect(
      screen.getByText(/Set the real values from your secret store/),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Copy template' }));

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1));
    expect(writeText.mock.calls[0]?.[0] ?? '').not.toContain(secret);
    expect(
      screen.getByRole('button', { name: 'Download template' }),
    ).toBeInTheDocument();
  });
});

describe('credential handling', () => {
  it('never writes a credential to browser storage', async () => {
    const user = userEvent.setup();
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    renderStorage();

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));
    await screen.findByText('Connection succeeded.');

    for (const call of setItem.mock.calls) {
      expect(String(call[1])).not.toContain(secret);
    }
    expect(JSON.stringify(localStorage)).not.toContain(secret);
    expect(JSON.stringify(sessionStorage)).not.toContain(secret);
  });

  it('keeps the credential out of the document after the test', async () => {
    const user = userEvent.setup();
    renderStorage();

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));
    await screen.findByText('Connection succeeded.');

    expect(screen.getByLabelText('Secret access key')).toHaveValue('');
    expect(document.body.innerHTML).not.toContain(secret);
  });

  it('forgets every credential when the page is left', async () => {
    const user = userEvent.setup();
    const { view } = renderStorage();

    await user.click(
      await screen.findByRole('radio', { name: /Azure Blob Storage/ }),
    );
    await user.type(screen.getByLabelText('Account key'), 'azure-key-value');

    expect(storageDraftSecrets()).toHaveLength(1);

    view.unmount();

    expect(storageDraftSecrets()).toHaveLength(0);
  });
});
