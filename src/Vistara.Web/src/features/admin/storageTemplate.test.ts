import { afterEach, describe, expect, it } from 'vitest';
import {
  clearStorageDraft,
  emptyStorageDraft,
  getStorageDraft,
  storageDraftSecrets,
  updateProviderDraft,
  updateStorageDraft,
  type StorageDraft,
} from './storageDraft';
import {
  buildDeployTemplate,
  buildValidationRequest,
  checkStorageDraft,
} from './storageTemplate';

const azureSecret = 'azure-account-key-value';
const s3Secret = 's3-secret-access-key-value';

function azureDraft(): StorageDraft {
  return {
    ...emptyStorageDraft,
    provider: 'azureBlob',
    azureBlob: {
      subscriptionId: '00000000-0000-0000-0000-000000000000',
      resourceGroup: 'vistara-rg',
      accountName: 'vistaramedia',
      container: 'originals',
      endpointSuffix: '',
      credentialKind: 'accountKey',
      accountKey: azureSecret,
      sasToken: '',
    },
  };
}

function s3Draft(): StorageDraft {
  return {
    ...emptyStorageDraft,
    provider: 's3',
    s3: {
      endpoint: 'https://s3.eu-central-1.example',
      region: 'eu-central-1',
      bucket: 'vistara-media',
      accessKeyId: 'AKIAEXAMPLE',
      secretAccessKey: s3Secret,
      forcePathStyle: true,
    },
  };
}

afterEach(() => {
  clearStorageDraft();
});

describe('storage draft', () => {
  it('keeps the candidate configuration in memory only', () => {
    updateProviderDraft('s3', { bucket: 'vistara-media', secretAccessKey: s3Secret });

    expect(getStorageDraft().s3.bucket).toBe('vistara-media');
    expect(JSON.stringify(localStorage)).not.toContain(s3Secret);
    expect(JSON.stringify(sessionStorage)).not.toContain(s3Secret);
  });

  it('forgets every secret when cleared', () => {
    updateStorageDraft({ provider: 'azureBlob' });
    updateProviderDraft('azureBlob', { accountKey: azureSecret });
    updateProviderDraft('s3', { secretAccessKey: s3Secret });

    expect(storageDraftSecrets()).toHaveLength(2);

    clearStorageDraft();

    expect(storageDraftSecrets()).toHaveLength(0);
    expect(getStorageDraft()).toEqual(emptyStorageDraft);
  });
});

describe('deploy template', () => {
  it('redacts the Azure credential and keeps the topology', () => {
    const template = buildDeployTemplate(azureDraft());

    expect(template.fileName).toBe('vistara-storage-azureBlob.env');
    expect(template.contents).toContain(
      'VISTARA_STORAGE__AZURE__ACCOUNT_NAME=vistaramedia',
    );
    expect(template.contents).toContain(
      'VISTARA_STORAGE__AZURE__ACCOUNT_KEY=${VISTARA_AZURE_ACCOUNT_KEY}',
    );
    expect(template.contents).not.toContain(azureSecret);
    expect(template.contents).toContain('restart the API and worker');
    expect(template.contents).toContain('az storage container create');
  });

  it('redacts both S3 credentials and states least privilege', () => {
    const template = buildDeployTemplate(s3Draft());

    expect(template.contents).toContain(
      'VISTARA_STORAGE__S3__ACCESS_KEY_ID=${VISTARA_S3_ACCESS_KEY_ID}',
    );
    expect(template.contents).toContain(
      'VISTARA_STORAGE__S3__SECRET_ACCESS_KEY=${VISTARA_S3_SECRET_ACCESS_KEY}',
    );
    expect(template.contents).not.toContain(s3Secret);
    expect(template.contents).not.toContain('AKIAEXAMPLE');
    expect(template.contents).toContain('s3:AbortMultipartUpload');
  });

  it('describes a filesystem deployment without any credential', () => {
    const template = buildDeployTemplate({
      ...emptyStorageDraft,
      filesystem: { rootPath: '/var/lib/vistara/media' },
    });

    expect(template.contents).toContain(
      'VISTARA_STORAGE__FILESYSTEM__ROOT_PATH=/var/lib/vistara/media',
    );
    expect(template.contents).not.toContain('${');
  });
});

describe('validation request', () => {
  it('sends the chosen Azure credential and nothing else', () => {
    const request = buildValidationRequest(azureDraft());

    expect(request).toEqual({
      provider: 'azureBlob',
      azureBlob: {
        accountName: 'vistaramedia',
        container: 'originals',
        credentialKind: 'accountKey',
        accountKey: azureSecret,
      },
    });
    expect(JSON.stringify(request)).not.toContain('vistara-rg');
  });

  it('sends the SAS token when that credential is chosen', () => {
    const draft = azureDraft();
    const request = buildValidationRequest({
      ...draft,
      azureBlob: {
        ...draft.azureBlob,
        credentialKind: 'sasToken',
        sasToken: 'sv=2024-01-01&sig=example',
      },
    });

    expect(request.azureBlob?.sasToken).toBe('sv=2024-01-01&sig=example');
    expect(request.azureBlob?.accountKey).toBeUndefined();
  });

  it('sends the S3 keys with the trimmed topology', () => {
    const request = buildValidationRequest(s3Draft());

    expect(request.s3).toEqual({
      endpoint: 'https://s3.eu-central-1.example',
      region: 'eu-central-1',
      bucket: 'vistara-media',
      accessKeyId: 'AKIAEXAMPLE',
      secretAccessKey: s3Secret,
      forcePathStyle: true,
    });
  });
});

describe('browser-side checks', () => {
  it('accepts a complete configuration', () => {
    expect(checkStorageDraft(azureDraft())).toEqual([]);
    expect(checkStorageDraft(s3Draft())).toEqual([]);
  });

  it('applies Azure naming rules before any credential is sent', () => {
    const draft = azureDraft();
    const problems = checkStorageDraft({
      ...draft,
      azureBlob: { ...draft.azureBlob, accountName: 'Vistara-Media', container: 'no' },
    });

    expect(problems.map((problem) => problem.field)).toEqual([
      'azureBlob.accountName',
      'azureBlob.container',
    ]);
    expect(problems[0]?.message).toContain('3 to 24');
  });

  it('requires the credential that matches the chosen kind', () => {
    const draft = azureDraft();

    expect(
      checkStorageDraft({
        ...draft,
        azureBlob: { ...draft.azureBlob, accountKey: '' },
      }).map((problem) => problem.field),
    ).toEqual(['azureBlob.accountKey']);
    expect(
      checkStorageDraft({
        ...draft,
        azureBlob: {
          ...draft.azureBlob,
          credentialKind: 'sasToken',
          accountKey: '',
          sasToken: '',
        },
      }).map((problem) => problem.field),
    ).toEqual(['azureBlob.sasToken']);
  });

  it('checks the S3 endpoint, bucket, and keys', () => {
    const problems = checkStorageDraft({
      ...emptyStorageDraft,
      provider: 's3',
      s3: {
        endpoint: 'not a url',
        region: '',
        bucket: 'A',
        accessKeyId: '',
        secretAccessKey: '',
        forcePathStyle: false,
      },
    });

    expect(problems.map((problem) => problem.field)).toEqual([
      's3.endpoint',
      's3.region',
      's3.bucket',
      's3.accessKeyId',
      's3.secretAccessKey',
    ]);
  });

  it('asks for a filesystem path', () => {
    expect(checkStorageDraft(emptyStorageDraft)).toEqual([
      {
        field: 'filesystem.rootPath',
        message: 'Enter the directory the server should store originals in.',
      },
    ]);
  });
});
