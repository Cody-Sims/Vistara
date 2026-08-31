import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// The assistant renders the whole administration page for every case, and the
// credential fields are filled through real user events, so this file needs
// more room than the default when the suite runs in parallel.
vi.setConfig({ testTimeout: 30_000 });
import { VistaraApiError } from '../../api/generated/client';
import { VistaraThrottledError } from '../../api/platform';
import type {
  StorageSummary,
  StorageValidationRequest,
  StorageValidationResponse,
} from '../../api/platform';
import { AdminStoragePage } from './AdminStoragePage';
import { clearStorageDraft, storageDraftSecrets } from './storageDraft';

const secret = 's3-secret-access-key-value';
const sessionSecret = 's3-session-token-value';

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
    options?: { signal?: AbortSignal },
  ) => Promise<StorageValidationResponse>;
  readonly getStorageValidationSupport?: () => Promise<{
    supported: boolean;
    providers: readonly ('filesystem' | 'azureBlob' | 's3')[];
  }>;
}

function renderStorage(options: Options = {}) {
  const client = {
    getStorageSummary: vi.fn(
      options.getStorageSummary ?? (async () => summary),
    ),
    getStorageValidationSupport: vi.fn(
      options.getStorageValidationSupport ??
        (async () => ({
          supported: true,
          providers: ['filesystem', 'azureBlob', 's3'] as const,
        })),
    ),
    validateStorage: vi.fn<
      (
        request: StorageValidationRequest,
        options?: { signal?: AbortSignal },
      ) => Promise<StorageValidationResponse>
    >(
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

  // Operators paste these values; pasting is also far cheaper than typing
  // every character through the event pipeline.
  for (const [label, value] of [
    ['Endpoint URL', 'https://s3.eu-central-1.example'],
    ['Region', 'eu-central-1'],
    ['Bucket', 'vistara-media'],
    ['Access key ID', 'AKIAEXAMPLE'],
    ['Secret access key', secret],
    ['Session token', sessionSecret],
  ] as const) {
    await user.click(screen.getByLabelText(label));
    await user.paste(value);
  }
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
    expect(
      screen.getAllByText(/Storage Blob Data Contributor/),
    ).not.toHaveLength(0);

    // A managed identity is the default and asks for no secret at all.
    expect(
      screen.getByRole('radio', {
        name: 'Use a managed identity (recommended)',
      }),
    ).toBeChecked();
    expect(screen.queryByLabelText('Account key')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('SAS token')).not.toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'Use an account key' }));

    expect(screen.getByLabelText('Account key')).toHaveAttribute(
      'type',
      'password',
    );

    await user.click(screen.getByRole('radio', { name: 'Use a SAS token' }));

    expect(screen.getByLabelText('SAS token')).toHaveAttribute(
      'type',
      'password',
    );
    expect(screen.queryByLabelText('Account key')).not.toBeInTheDocument();
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
      within(problems).getByText(/Enter the bucket region/),
    ).toBeInTheDocument();
    expect(client.validateStorage).not.toHaveBeenCalled();
  });

  it('tests a connection and reports each check', async () => {
    const user = userEvent.setup();
    const { client } = renderStorage();

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));

    await waitFor(() =>
      expect(client.validateStorage).toHaveBeenCalledWith(
        {
          provider: 's3',
          s3: {
            endpoint: 'https://s3.eu-central-1.example',
            region: 'eu-central-1',
            bucket: 'vistara-media',
            accessKeyId: 'AKIAEXAMPLE',
            secretAccessKey: secret,
            sessionToken: sessionSecret,
            forcePathStyle: true,
          },
        },
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      ),
    );
    expect(await screen.findByText('Connection succeeded.')).toBeInTheDocument();

    // The five published checks always render, in the published order.
    const checks = screen.getByRole('list', { name: 'Connection checks' });
    const rendered = within(checks).getAllByRole('listitem');
    expect(rendered).toHaveLength(5);
    expect(rendered[0]).toHaveTextContent('Endpoint reachable');
    expect(rendered[4]).toHaveTextContent('Removed the probe object');
    expect(rendered[2]).toHaveTextContent('Skipped');
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
      screen.getByText(/cannot test storage connections/),
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

describe('validation statuses', () => {
  async function testWith(
    validateStorage: () => Promise<StorageValidationResponse>,
  ) {
    const user = userEvent.setup();
    renderStorage({ validateStorage });
    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));
    return user;
  }

  it('reports a throttled attempt with the wait the API published', async () => {
    await testWith(async () => {
      throw new VistaraThrottledError(
        {
          type: 'about:blank',
          title: 'storage_validation.throttled',
          status: 429,
          code: 'storage_validation.throttled',
          errors: {},
        },
        45,
      );
    });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Too many validation attempts');
    expect(alert).toHaveTextContent('45 seconds');
    expect(alert.textContent).not.toContain(secret);
  });

  it('marks the members a 422 rejected', async () => {
    await testWith(async () => {
      throw new VistaraApiError(422, {
        type: 'about:blank',
        title: 'storage_validation.invalid_request',
        status: 422,
        code: 'storage_validation.invalid_request',
        errors: { 's3.bucket': ['The bucket name is not allowed.'] },
      });
    });

    const problems = await screen.findByRole('alert', {
      name: 'Configuration problems',
    });
    expect(problems).toHaveTextContent('The bucket name is not allowed.');
    expect(
      screen.getByText(/rejected part of this configuration/),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Bucket')).toHaveAttribute(
      'aria-invalid',
      'true',
    );
  });

  it('explains a body the deployment refused as too large', async () => {
    await testWith(async () => {
      throw new VistaraApiError(413, {
        type: 'about:blank',
        title: 'storage_validation.body_too_large',
        status: 413,
        code: 'storage_validation.body_too_large',
        errors: {},
      });
    });

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'too large to check',
    );
  });

  it('explains a forbidden attempt without blaming the credential', async () => {
    await testWith(async () => {
      throw new VistaraApiError(403, {
        type: 'about:blank',
        title: 'storage_validation.forbidden',
        status: 403,
        code: 'storage_validation.forbidden',
        errors: {},
      });
    });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('workspace owner rights');
    expect(alert).toHaveTextContent('Nothing was sent to the provider');
  });

  it('renders a failed validation as a completed answer, not an error', async () => {
    await testWith(async () => ({
      valid: false,
      provider: 's3',
      checks: [
        { id: 'reachable', status: 'passed' },
        {
          id: 'authenticated',
          status: 'failed',
          detail: 'The credential was rejected.',
        },
        { id: 'read', status: 'skipped' },
        { id: 'write', status: 'skipped' },
        { id: 'delete', status: 'skipped' },
      ],
      message: 'The storage settings were rejected with the supplied credential.',
    }));

    const checks = await screen.findByRole('list', {
      name: 'Connection checks',
    });
    expect(within(checks).getAllByRole('listitem')).toHaveLength(5);
    expect(within(checks).getByText('The credential was rejected.')).toBeInTheDocument();
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'rejected with the supplied credential',
    );
  });

  it('reports a server-side timeout through its check detail', async () => {
    await testWith(async () => ({
      valid: false,
      provider: 's3',
      checks: [
        {
          id: 'reachable',
          status: 'failed',
          detail: 'The provider did not answer within the validation timeout.',
        },
        { id: 'authenticated', status: 'skipped' },
        { id: 'read', status: 'skipped' },
        { id: 'write', status: 'skipped' },
        { id: 'delete', status: 'skipped' },
      ],
      message: 'The storage settings could not be checked within the timeout.',
    }));

    expect(
      await screen.findByText(
        'The provider did not answer within the validation timeout.',
      ),
    ).toBeInTheDocument();
  });

  it('cancels a running test and says so', async () => {
    const user = userEvent.setup();
    let release: ((value: StorageValidationResponse) => void) | undefined;
    const { client } = renderStorage({
      validateStorage: () =>
        new Promise<StorageValidationResponse>((resolve) => {
          release = resolve;
        }),
    });

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));

    const cancel = await screen.findByRole('button', { name: 'Cancel test' });
    await user.click(cancel);

    expect(await screen.findByText('Test cancelled.')).toBeInTheDocument();
    expect(screen.getByLabelText('Secret access key')).toHaveValue('');
    expect(document.body.innerHTML).not.toContain(secret);

    const signal = client.validateStorage.mock.calls[0]![1]?.signal;
    expect(signal?.aborted).toBe(true);

    release?.({ valid: true, provider: 's3', checks: [] });
  });

  it('moves off a provider the deployment cannot validate', async () => {
    const { client } = renderStorage({
      getStorageValidationSupport: async () => ({
        supported: true,
        providers: ['azureBlob', 's3'],
      }),
    });

    const group = await screen.findByRole('radiogroup', {
      name: 'Storage provider',
    });
    await waitFor(() =>
      expect(
        within(group).getByRole('radio', { name: /Azure Blob Storage/ }),
      ).toBeChecked(),
    );
    expect(
      within(group).queryByRole('radio', { name: /Local filesystem/ }),
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText('Storage account name')).toBeInTheDocument();
    expect(client.validateStorage).not.toHaveBeenCalled();
  });

  it('offers nothing to test when the deployment lists no provider', async () => {
    renderStorage({
      getStorageValidationSupport: async () => ({
        supported: true,
        providers: [],
      }),
    });

    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Test connection' }),
      ).toBeDisabled(),
    );
    expect(
      screen.getByText(/cannot test storage connections/),
    ).toBeInTheDocument();
  });

  it('offers only the providers the deployment can validate', async () => {
    renderStorage({
      getStorageValidationSupport: async () => ({
        supported: true,
        providers: ['filesystem'],
      }),
    });

    const group = await screen.findByRole('radiogroup', {
      name: 'Storage provider',
    });
    await waitFor(() =>
      expect(within(group).getAllByRole('radio')).toHaveLength(1),
    );
    expect(
      within(group).getByRole('radio', { name: /Local filesystem/ }),
    ).toBeInTheDocument();
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

  it('keeps every credential out of the document after the test', async () => {
    const user = userEvent.setup();
    renderStorage();

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));
    await screen.findByText('Connection succeeded.');

    expect(screen.getByLabelText('Access key ID')).toHaveValue('');
    expect(screen.getByLabelText('Secret access key')).toHaveValue('');
    expect(screen.getByLabelText('Session token')).toHaveValue('');
    expect(storageDraftSecrets()).toHaveLength(0);
    expect(document.body.innerHTML).not.toContain(secret);
    expect(document.body.innerHTML).not.toContain(sessionSecret);
  });

  it('forgets a session token when a test is cancelled', async () => {
    const user = userEvent.setup();
    renderStorage({
      validateStorage: () => new Promise<StorageValidationResponse>(() => {}),
    });

    await fillS3(user);
    await user.click(screen.getByRole('button', { name: 'Test connection' }));
    await user.click(await screen.findByRole('button', { name: 'Cancel test' }));

    expect(storageDraftSecrets()).toHaveLength(0);
    expect(document.body.innerHTML).not.toContain(sessionSecret);
  });

  it('forgets every credential when the page is left', async () => {
    const user = userEvent.setup();
    const { view } = renderStorage();

    await user.click(
      await screen.findByRole('radio', { name: /Azure Blob Storage/ }),
    );
    await user.click(screen.getByRole('radio', { name: 'Use an account key' }));
    await user.type(screen.getByLabelText('Account key'), 'azure-key-value');

    expect(storageDraftSecrets()).toHaveLength(1);

    view.unmount();

    expect(storageDraftSecrets()).toHaveLength(0);
  });
});
