import {
  useCallback,
  useEffect,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from 'react';
import { VistaraApiError } from '../../api/generated/client';
import type {
  PlatformApiClient,
  StorageProviderKind,
  StorageSummary,
  StorageValidationResponse,
} from '../../api/platform';
import { useRemoteResource } from '../../app/useRemoteResource';
import {
  AdminEmpty,
  AdminFailure,
  AdminLoading,
  AdminPage,
  AdminPendingContract,
} from './AdminPage';
import { formatBytes, formatMoment } from './format';
import {
  clearStorageDraft,
  getStorageDraft,
  subscribeToStorageDraft,
  updateProviderDraft,
  updateStorageDraft,
} from './storageDraft';
import { StorageProviderIcon } from './StorageProviderIcon';
import {
  buildDeployTemplate,
  buildValidationRequest,
  checkStorageDraft,
  type DraftProblem,
} from './storageTemplate';
import styles from './admin.module.css';

export type AdminStorageClient = Pick<
  PlatformApiClient,
  'getStorageSummary' | 'validateStorage'
>;

interface AdminStoragePageProps {
  readonly client: AdminStorageClient;
}

type TestState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'testing' }
  | { readonly kind: 'answered'; readonly result: StorageValidationResponse }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'failed'; readonly message: string };

const providers: readonly {
  kind: StorageProviderKind;
  title: string;
  summary: string;
}[] = [
  {
    kind: 'filesystem',
    title: 'Local filesystem',
    summary: 'A directory on this server or an attached volume.',
  },
  {
    kind: 'azureBlob',
    title: 'Azure Blob Storage',
    summary: 'A container in an Azure storage account.',
  },
  {
    kind: 's3',
    title: 'S3-compatible',
    summary: 'Amazon S3, MinIO, Ceph, Backblaze B2, and similar.',
  },
];

const statusLabels: Record<string, string> = {
  healthy: 'Healthy',
  degraded: 'Degraded',
  unavailable: 'Unavailable',
};

const checkLabels: Record<string, string> = {
  reachable: 'Endpoint reachable',
  authenticated: 'Credential accepted',
  read: 'Read a test object',
  write: 'Wrote a test object',
  delete: 'Removed the test object',
};

export function AdminStoragePage({ client }: AdminStoragePageProps) {
  const load = useCallback(() => client.getStorageSummary(), [client]);
  const { state, reload } = useRemoteResource<StorageSummary>(load);
  const draft = useSyncExternalStore(
    subscribeToStorageDraft,
    getStorageDraft,
    getStorageDraft,
  );
  const [problems, setProblems] = useState<readonly DraftProblem[]>([]);
  const [test, setTest] = useState<TestState>({ kind: 'idle' });
  const [template, setTemplate] = useState<{
    fileName: string;
    contents: string;
  }>();
  const [copied, setCopied] = useState(false);

  // Credentials live only while this page is open.
  useEffect(() => clearStorageDraft, []);

  const problemFor = (field: string) =>
    problems.find((problem) => problem.field === field)?.message;

  function forgetSecrets() {
    updateProviderDraft('azureBlob', { accountKey: '', sasToken: '' });
    updateProviderDraft('s3', { accessKeyId: '', secretAccessKey: '' });
  }

  async function runTest() {
    const found = checkStorageDraft(draft);
    setProblems(found);
    setCopied(false);
    if (found.length > 0) {
      setTest({ kind: 'idle' });
      return;
    }

    setTest({ kind: 'testing' });
    const request = buildValidationRequest(draft);
    try {
      const result = await client.validateStorage(request);
      setTest({ kind: 'answered', result });
    } catch (error) {
      setTest(
        error instanceof VistaraApiError && error.status === 404
          ? { kind: 'unavailable' }
          : {
              kind: 'failed',
              message:
                'The connection could not be tested. Nothing was changed and no credential was stored.',
            },
      );
    } finally {
      // The credential has served its only purpose; it never survives the call.
      forgetSecrets();
    }
  }

  function generate() {
    const found = checkStorageDraft(draft);
    setProblems(found);
    setCopied(false);
    if (found.length === 0) {
      setTemplate(buildDeployTemplate(draft));
    }
  }

  function download() {
    if (!template) {
      return;
    }

    const blob = new Blob([template.contents], {
      type: 'text/plain;charset=utf-8',
    });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = template.fileName;
    anchor.click();
    URL.revokeObjectURL(url);
  }

  return (
    <AdminPage
      title="Storage"
      description="What this workspace is using today, and an assistant for pointing the deployment at a filesystem, Azure Blob Storage, or an S3-compatible provider."
      toolbar={
        <button
          className={styles.secondaryButton}
          type="button"
          onClick={reload}
        >
          Refresh status
        </button>
      }
    >
      <p className={styles.announce} role="status" aria-live="polite">
        {state.kind === 'loading' ? 'Loading storage status…' : ''}
      </p>

      {state.kind === 'loading' ? (
        <AdminLoading label="Loading storage status…" shape="card" />
      ) : null}

      {state.kind === 'failed' ? (
        <AdminFailure
          title="Storage status is unavailable"
          description="Consumption could not be read. Stored images are unaffected and the assistant below still works."
          onRetry={reload}
        />
      ) : null}

      {state.kind === 'ready' ? (
        <>
          <dl className={styles.summary}>
            <div>
              <dt>Originals</dt>
              <dd>{formatBytes(state.value.originalBytes)}</dd>
            </div>
            <div>
              <dt>Derivatives</dt>
              <dd>{formatBytes(state.value.derivativeBytes)}</dd>
            </div>
            <div>
              <dt>Staging</dt>
              <dd>{formatBytes(state.value.stagingBytes)}</dd>
            </div>
            <div>
              <dt>Pending uploads</dt>
              <dd>{formatBytes(state.value.pendingUploadBytes)}</dd>
            </div>
          </dl>

          {state.value.buckets.length === 0 ? (
            <AdminEmpty>
              No storage buckets are reporting yet. Configure a provider below.
            </AdminEmpty>
          ) : (
            <ul className={styles.cards} aria-label="Storage buckets">
              {state.value.buckets.map((bucket) => {
                const share =
                  bucket.quotaBytes > 0
                    ? Math.min(
                        100,
                        Math.round(
                          (bucket.usedBytes / bucket.quotaBytes) * 100,
                        ),
                      )
                    : undefined;

                return (
                  <li className={styles.card} key={bucket.id}>
                    <div className={styles.cardHeader}>
                      <h2>{bucket.id}</h2>
                      <span
                        className={styles.badge}
                        data-status={bucket.status}
                      >
                        {statusLabels[bucket.status] ?? bucket.status}
                      </span>
                    </div>
                    <p className={styles.cardFigure}>
                      {formatBytes(bucket.usedBytes)}
                      {bucket.quotaBytes > 0 ? (
                        <span className={styles.cardMeta}>
                          {' '}
                          of {formatBytes(bucket.quotaBytes)}
                        </span>
                      ) : null}
                    </p>
                    {share === undefined ? null : (
                      <div
                        className={styles.meter}
                        role="meter"
                        aria-valuemin={0}
                        aria-valuemax={100}
                        aria-valuenow={share}
                        aria-label={`${bucket.id} quota used`}
                      >
                        <span style={{ inlineSize: `${share}%` }} />
                      </div>
                    )}
                    <p className={styles.cardMeta}>
                      {new Intl.NumberFormat().format(bucket.objectCount)}{' '}
                      objects · checked {formatMoment(bucket.lastCheckedAt)}
                    </p>
                    {bucket.message ? (
                      <p className={styles.cardNotice}>{bucket.message}</p>
                    ) : null}
                  </li>
                );
              })}
            </ul>
          )}
        </>
      ) : null}

      <section className={styles.assistant} aria-labelledby="storage-assistant">
        <h2 id="storage-assistant">Connect storage</h2>
        <p className={styles.assistantIntro}>
          Credentials entered here are used for one connection test and are
          never stored by this browser. Applying a provider is a server change:
          generate the template, apply it, and restart the API and worker.
        </p>

        <fieldset
          className={styles.providerGroup}
          role="radiogroup"
          aria-label="Storage provider"
        >
          <legend className={styles.legend}>Provider</legend>
          {providers.map((provider) => (
            <label className={styles.providerCard} key={provider.kind}>
              <input
                aria-describedby={`provider-${provider.kind}-summary`}
                aria-label={provider.title}
                checked={draft.provider === provider.kind}
                name="storage-provider"
                type="radio"
                value={provider.kind}
                onChange={() => {
                  updateStorageDraft({ provider: provider.kind });
                  setProblems([]);
                  setTest({ kind: 'idle' });
                  setTemplate(undefined);
                }}
              />
              <span className={styles.providerBody}>
                <StorageProviderIcon provider={provider.kind} />
                <strong>{provider.title}</strong>
                <span
                  className={styles.choiceHint}
                  id={`provider-${provider.kind}-summary`}
                >
                  {provider.summary}
                </span>
              </span>
            </label>
          ))}
        </fieldset>

        {draft.provider === 'filesystem' ? (
          <fieldset className={styles.fieldset}>
            <legend>Filesystem</legend>
            <Field
              error={problemFor('filesystem.rootPath')}
              hint="An absolute path the API and worker can write to, for example /var/lib/vistara/media."
              id="storage-root-path"
              label="Storage directory"
              value={draft.filesystem.rootPath}
              onChange={(value) =>
                updateProviderDraft('filesystem', { rootPath: value })
              }
            />
            <p className={styles.guidance}>
              Least privilege: give the directory to the service user only, keep
              it off any web root, and back it up with the database.
            </p>
          </fieldset>
        ) : null}

        {draft.provider === 'azureBlob' ? (
          <fieldset className={styles.fieldset}>
            <legend>Azure Blob Storage</legend>
            <Field
              hint="Only used to build the CLI hint in the template; it is not sent anywhere."
              id="storage-azure-subscription"
              label="Subscription ID"
              value={draft.azureBlob.subscriptionId}
              onChange={(value) =>
                updateProviderDraft('azureBlob', { subscriptionId: value })
              }
            />
            <Field
              hint="Name it for its lifecycle, for example vistara-prod-rg."
              id="storage-azure-resource-group"
              label="Resource group"
              value={draft.azureBlob.resourceGroup}
              onChange={(value) =>
                updateProviderDraft('azureBlob', { resourceGroup: value })
              }
            />
            <Field
              error={problemFor('azureBlob.accountName')}
              hint="3 to 24 characters, lowercase letters and numbers only, globally unique."
              id="storage-azure-account"
              label="Storage account name"
              value={draft.azureBlob.accountName}
              onChange={(value) =>
                updateProviderDraft('azureBlob', { accountName: value })
              }
            />
            <Field
              error={problemFor('azureBlob.container')}
              hint="3 to 63 characters, lowercase letters, numbers, and inner hyphens."
              id="storage-azure-container"
              label="Container name"
              value={draft.azureBlob.container}
              onChange={(value) =>
                updateProviderDraft('azureBlob', { container: value })
              }
            />
            <Field
              hint="Leave empty for the public cloud, or set core.usgovcloudapi.net and similar."
              id="storage-azure-suffix"
              label="Endpoint suffix"
              value={draft.azureBlob.endpointSuffix}
              onChange={(value) =>
                updateProviderDraft('azureBlob', { endpointSuffix: value })
              }
            />

            <fieldset className={styles.choices}>
              <legend className={styles.legend}>Credential</legend>
              {(
                [
                  ['accountKey', 'Use an account key'],
                  ['sasToken', 'Use a SAS token'],
                ] as const
              ).map(([kind, label]) => (
                <label className={styles.choice} key={kind}>
                  <input
                    aria-label={label}
                    checked={draft.azureBlob.credentialKind === kind}
                    name="azure-credential"
                    type="radio"
                    value={kind}
                    onChange={() =>
                      updateProviderDraft('azureBlob', {
                        credentialKind: kind,
                      })
                    }
                  />
                  <span>
                    <strong>{label}</strong>
                  </span>
                </label>
              ))}
            </fieldset>

            {draft.azureBlob.credentialKind === 'accountKey' ? (
              <Field
                error={problemFor('azureBlob.accountKey')}
                hint="Pasted once for the connection test and then cleared."
                id="storage-azure-key"
                label="Account key"
                secret
                value={draft.azureBlob.accountKey}
                onChange={(value) =>
                  updateProviderDraft('azureBlob', { accountKey: value })
                }
              />
            ) : (
              <Field
                error={problemFor('azureBlob.sasToken')}
                hint="Scope the SAS to this container with read, write, delete, and list only."
                id="storage-azure-sas"
                label="SAS token"
                secret
                value={draft.azureBlob.sasToken}
                onChange={(value) =>
                  updateProviderDraft('azureBlob', { sasToken: value })
                }
              />
            )}

            <p className={styles.guidance}>
              Least privilege: prefer a managed identity with the Storage Blob
              Data Contributor role on this container. If you must use a key or
              SAS, scope it to the container, set an expiry, and rotate it.
            </p>
          </fieldset>
        ) : null}

        {draft.provider === 's3' ? (
          <fieldset className={styles.fieldset}>
            <legend>S3-compatible storage</legend>
            <Field
              error={problemFor('s3.endpoint')}
              hint="For example https://s3.eu-central-1.amazonaws.com or your MinIO URL."
              id="storage-s3-endpoint"
              label="Endpoint URL"
              value={draft.s3.endpoint}
              onChange={(value) =>
                updateProviderDraft('s3', { endpoint: value })
              }
            />
            <Field
              error={problemFor('s3.region')}
              id="storage-s3-region"
              label="Region"
              value={draft.s3.region}
              onChange={(value) => updateProviderDraft('s3', { region: value })}
            />
            <Field
              error={problemFor('s3.bucket')}
              hint="3 to 63 characters, lowercase letters, numbers, dots, and hyphens."
              id="storage-s3-bucket"
              label="Bucket"
              value={draft.s3.bucket}
              onChange={(value) => updateProviderDraft('s3', { bucket: value })}
            />
            <Field
              error={problemFor('s3.accessKeyId')}
              id="storage-s3-access-key"
              label="Access key ID"
              secret
              value={draft.s3.accessKeyId}
              onChange={(value) =>
                updateProviderDraft('s3', { accessKeyId: value })
              }
            />
            <Field
              error={problemFor('s3.secretAccessKey')}
              hint="Pasted once for the connection test and then cleared."
              id="storage-s3-secret"
              label="Secret access key"
              secret
              value={draft.s3.secretAccessKey}
              onChange={(value) =>
                updateProviderDraft('s3', { secretAccessKey: value })
              }
            />
            <label className={styles.toggle}>
              <input
                aria-label="Use path-style addressing"
                checked={draft.s3.forcePathStyle}
                type="checkbox"
                onChange={(event) =>
                  updateProviderDraft('s3', {
                    forcePathStyle: event.target.checked,
                  })
                }
              />
              <span>
                <strong>Use path-style addressing</strong>
                <span className={styles.choiceHint}>
                  Required by MinIO and most self-hosted gateways.
                </span>
              </span>
            </label>

            <p className={styles.guidance}>
              Least privilege: create a dedicated user whose policy covers only
              this bucket with s3:GetObject, s3:PutObject, s3:DeleteObject,
              s3:AbortMultipartUpload, s3:ListBucket, and
              s3:ListBucketMultipartUploads.
            </p>
          </fieldset>
        ) : null}

        <div className={styles.formActions}>
          <button
            className={styles.primaryButton}
            disabled={test.kind === 'testing'}
            type="button"
            onClick={() => void runTest()}
          >
            {test.kind === 'testing' ? 'Testing…' : 'Test connection'}
          </button>
          <button
            className={styles.secondaryButton}
            type="button"
            onClick={generate}
          >
            Generate deploy template
          </button>
        </div>

        <p className={styles.announce} role="status" aria-live="polite">
          {test.kind === 'testing' ? 'Testing the connection…' : ''}
          {test.kind === 'answered' && test.result.valid
            ? 'Connection succeeded.'
            : ''}
        </p>

        {problems.length > 0 ? (
          <ul className={styles.problems} aria-label="Configuration problems">
            {problems.map((problem) => (
              <li key={problem.field}>{problem.message}</li>
            ))}
          </ul>
        ) : null}

        {test.kind === 'failed' ? (
          <p className={styles.alert} role="alert">
            {test.message}
          </p>
        ) : null}

        {test.kind === 'unavailable' ? (
          <p className={styles.alert} role="alert">
            This deployment cannot test storage connections yet. Generate the
            template below and apply it on the server instead.
          </p>
        ) : null}

        {test.kind === 'answered' && !test.result.valid ? (
          <p className={styles.alert} role="alert">
            {test.result.message ??
              'The provider rejected the configuration. Nothing was changed.'}
          </p>
        ) : null}

        {test.kind === 'answered' ? (
          <ul className={styles.checks} aria-label="Connection checks">
            {test.result.checks.map((check) => (
              <li key={check.id}>
                <span className={styles.badge} data-status={check.status}>
                  {check.status === 'passed'
                    ? 'Passed'
                    : check.status === 'failed'
                      ? 'Failed'
                      : 'Skipped'}
                </span>
                <span>{checkLabels[check.id] ?? check.id}</span>
                {check.detail ? (
                  <span className={styles.choiceHint}>{check.detail}</span>
                ) : null}
              </li>
            ))}
          </ul>
        ) : null}

        {template ? (
          <div className={styles.template}>
            <label htmlFor="storage-template">Deploy template</label>
            <textarea
              className={styles.templateText}
              id="storage-template"
              readOnly
              rows={10}
              value={template.contents}
            />
            <p className={styles.guidance}>
              Credentials are placeholders here on purpose, so this file is safe
              to share. Set the real values from your secret store, then restart
              the API and worker to apply them.
            </p>
            <div className={styles.formActions}>
              <button
                className={styles.secondaryButton}
                type="button"
                onClick={() => {
                  void navigator.clipboard
                    ?.writeText(template.contents)
                    .then(() => setCopied(true))
                    .catch(() => setCopied(false));
                }}
              >
                Copy template
              </button>
              <button
                className={styles.secondaryButton}
                type="button"
                onClick={download}
              >
                Download template
              </button>
              <span className={styles.hintRow} role="status" aria-live="polite">
                {copied ? 'Template copied.' : ''}
              </span>
            </div>
          </div>
        ) : null}
      </section>

      <AdminPendingContract
        title="Connection testing"
        description="The assistant tests a candidate configuration through one route. Until it is published, testing reports that the deployment cannot check the connection."
        contract={
          'POST /api/v1/admin/storage/validate — body { provider, filesystem? { rootPath }, azureBlob? { accountName, container, endpointSuffix?, credentialKind, accountKey? | sasToken? }, s3? { endpoint, region, bucket, accessKeyId, secretAccessKey, forcePathStyle } } → 200 { valid, provider, checks: [{ id, status, detail? }], message? }; never echoes a submitted credential'
        }
      />
    </AdminPage>
  );
}

interface FieldProps {
  readonly error?: string;
  readonly hint?: string;
  readonly id: string;
  readonly label: string;
  readonly secret?: boolean;
  readonly value: string;
  onChange(value: string): void;
}

function Field({
  error,
  hint,
  id,
  label,
  onChange,
  secret = false,
  value,
}: FieldProps): ReactNode {
  const described = [hint ? `${id}-hint` : '', error ? `${id}-error` : '']
    .filter(Boolean)
    .join(' ');

  return (
    <div className={styles.field}>
      <label htmlFor={id}>{label}</label>
      <input
        aria-describedby={described || undefined}
        aria-invalid={error ? true : undefined}
        autoComplete="off"
        className={styles.control}
        id={id}
        name={id}
        spellCheck={false}
        type={secret ? 'password' : 'text'}
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
      {hint ? (
        <p className={styles.fieldHint} id={`${id}-hint`}>
          {hint}
        </p>
      ) : null}
      {error ? (
        <p className={styles.fieldError} id={`${id}-error`}>
          {error}
        </p>
      ) : null}
    </div>
  );
}
