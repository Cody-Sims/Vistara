import type { StorageValidationRequest } from '../../api/platform';
import type { StorageDraft } from './storageDraft';

export interface DeployTemplate {
  readonly fileName: string;
  readonly contents: string;
}

const placeholders: Record<string, string> = {
  azureAccountKey: '${VISTARA_AZURE_ACCOUNT_KEY}',
  azureSasToken: '${VISTARA_AZURE_SAS_TOKEN}',
  s3AccessKeyId: '${VISTARA_S3_ACCESS_KEY_ID}',
  s3SecretAccessKey: '${VISTARA_S3_SECRET_ACCESS_KEY}',
};

/**
 * Builds the environment file a deployment applies by hand. Credentials are
 * always replaced by placeholders: the generated artifact is safe to paste
 * into a ticket, a screenshot, or a pull request.
 */
export function buildDeployTemplate(draft: StorageDraft): DeployTemplate {
  const carriesSecret = draft.provider !== 'filesystem';
  const lines: string[] = [
    '# Vistara storage configuration',
    '# Apply these values on the server, then restart the API and worker so',
    '# the new provider is used.',
    ...(carriesSecret
      ? [
          '# Placeholder values are never filled in here: set them from your',
          '# secret store and never commit the real credential.',
        ]
      : []),
    '',
    `VISTARA_STORAGE__PROVIDER=${draft.provider}`,
  ];

  if (draft.provider === 'filesystem') {
    lines.push(
      `VISTARA_STORAGE__FILESYSTEM__ROOT_PATH=${draft.filesystem.rootPath}`,
      '# The directory must be writable by the API and worker users only.',
    );
  }

  if (draft.provider === 'azureBlob') {
    const azure = draft.azureBlob;
    lines.push(
      `VISTARA_STORAGE__AZURE__ACCOUNT_NAME=${azure.accountName}`,
      `VISTARA_STORAGE__AZURE__CONTAINER=${azure.container}`,
    );
    if (azure.endpointSuffix) {
      lines.push(
        `VISTARA_STORAGE__AZURE__ENDPOINT_SUFFIX=${azure.endpointSuffix}`,
      );
    }

    lines.push(
      azure.credentialKind === 'sasToken'
        ? `VISTARA_STORAGE__AZURE__SAS_TOKEN=${placeholders.azureSasToken}`
        : `VISTARA_STORAGE__AZURE__ACCOUNT_KEY=${placeholders.azureAccountKey}`,
      '',
      '# Least privilege: grant only this container, not the whole account.',
    );
    if (azure.subscriptionId || azure.resourceGroup) {
      lines.push(
        `# az storage container create --name ${azure.container || '<container>'} \\`,
        `#   --account-name ${azure.accountName || '<account>'} \\`,
        `#   --resource-group ${azure.resourceGroup || '<resource-group>'}`,
      );
      if (azure.subscriptionId) {
        lines.push(`#   --subscription ${azure.subscriptionId}`);
      }
    }
  }

  if (draft.provider === 's3') {
    const s3 = draft.s3;
    lines.push(
      `VISTARA_STORAGE__S3__ENDPOINT=${s3.endpoint}`,
      `VISTARA_STORAGE__S3__REGION=${s3.region}`,
      `VISTARA_STORAGE__S3__BUCKET=${s3.bucket}`,
      `VISTARA_STORAGE__S3__FORCE_PATH_STYLE=${String(s3.forcePathStyle)}`,
      `VISTARA_STORAGE__S3__ACCESS_KEY_ID=${placeholders.s3AccessKeyId}`,
      `VISTARA_STORAGE__S3__SECRET_ACCESS_KEY=${placeholders.s3SecretAccessKey}`,
      '',
      '# Least privilege: limit the policy to this bucket and the actions',
      '# s3:GetObject, s3:PutObject, s3:DeleteObject, s3:AbortMultipartUpload,',
      '# s3:ListBucket, and s3:ListBucketMultipartUploads.',
    );
  }

  return {
    fileName: `vistara-storage-${draft.provider}.env`,
    contents: `${lines.join('\n')}\n`,
  };
}

/** Builds the one-shot validation body. Secrets are used here and nowhere else. */
export function buildValidationRequest(
  draft: StorageDraft,
): StorageValidationRequest {
  if (draft.provider === 'filesystem') {
    return {
      provider: 'filesystem',
      filesystem: { rootPath: draft.filesystem.rootPath.trim() },
    };
  }

  if (draft.provider === 'azureBlob') {
    const azure = draft.azureBlob;
    return {
      provider: 'azureBlob',
      azureBlob: {
        accountName: azure.accountName.trim(),
        container: azure.container.trim(),
        ...(azure.endpointSuffix.trim()
          ? { endpointSuffix: azure.endpointSuffix.trim() }
          : {}),
        credentialKind: azure.credentialKind,
        ...(azure.credentialKind === 'accountKey'
          ? { accountKey: azure.accountKey }
          : { sasToken: azure.sasToken }),
      },
    };
  }

  return {
    provider: 's3',
    s3: {
      endpoint: draft.s3.endpoint.trim(),
      region: draft.s3.region.trim(),
      bucket: draft.s3.bucket.trim(),
      accessKeyId: draft.s3.accessKeyId,
      secretAccessKey: draft.s3.secretAccessKey,
      forcePathStyle: draft.s3.forcePathStyle,
    },
  };
}

export interface DraftProblem {
  readonly field: string;
  readonly message: string;
}

const azureAccount = /^[a-z0-9]{3,24}$/;
const azureContainer = /^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])$/;
const bucketName = /^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$/;

export interface DraftCheckOptions {
  /**
   * Credentials are only required for a connection test. The deploy template
   * never contains one, so generating it must not demand a secret that the
   * assistant has already cleared.
   */
  readonly requireCredentials?: boolean;
}

/** Checks what can be checked in the browser before any credential is sent. */
export function checkStorageDraft(
  draft: StorageDraft,
  options: DraftCheckOptions = {},
): readonly DraftProblem[] {
  const requireCredentials = options.requireCredentials !== false;
  const problems: DraftProblem[] = [];

  if (draft.provider === 'filesystem') {
    if (!draft.filesystem.rootPath.trim()) {
      problems.push({
        field: 'filesystem.rootPath',
        message: 'Enter the directory the server should store originals in.',
      });
    }

    return problems;
  }

  if (draft.provider === 'azureBlob') {
    const azure = draft.azureBlob;
    if (!azureAccount.test(azure.accountName.trim())) {
      problems.push({
        field: 'azureBlob.accountName',
        message:
          'Storage account names are 3 to 24 characters, lowercase letters and numbers only.',
      });
    }

    if (!azureContainer.test(azure.container.trim())) {
      problems.push({
        field: 'azureBlob.container',
        message:
          'Container names are 3 to 63 characters, lowercase letters, numbers, and inner hyphens.',
      });
    }

    if (
      requireCredentials &&
      azure.credentialKind === 'accountKey' &&
      !azure.accountKey
    ) {
      problems.push({
        field: 'azureBlob.accountKey',
        message: 'Paste the account key, or switch to a SAS token.',
      });
    }

    if (
      requireCredentials &&
      azure.credentialKind === 'sasToken' &&
      !azure.sasToken
    ) {
      problems.push({
        field: 'azureBlob.sasToken',
        message: 'Paste the SAS token, or switch to an account key.',
      });
    }

    return problems;
  }

  const s3 = draft.s3;
  if (!isHttpUrl(s3.endpoint.trim())) {
    problems.push({
      field: 's3.endpoint',
      message: 'Enter the endpoint URL, for example https://s3.eu-central-1.example.',
    });
  }

  if (!s3.region.trim()) {
    problems.push({ field: 's3.region', message: 'Enter the bucket region.' });
  }

  if (!bucketName.test(s3.bucket.trim())) {
    problems.push({
      field: 's3.bucket',
      message:
        'Bucket names are 3 to 63 characters, lowercase letters, numbers, dots, and hyphens.',
    });
  }

  if (requireCredentials && !s3.accessKeyId) {
    problems.push({
      field: 's3.accessKeyId',
      message: 'Enter the access key ID for the least-privilege user.',
    });
  }

  if (requireCredentials && !s3.secretAccessKey) {
    problems.push({
      field: 's3.secretAccessKey',
      message: 'Enter the secret access key.',
    });
  }

  return problems;
}

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' || url.protocol === 'http:';
  } catch {
    return false;
  }
}
