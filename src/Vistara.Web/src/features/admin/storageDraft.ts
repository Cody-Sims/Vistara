import type { StorageProviderKind } from '../../api/platform';

export interface FilesystemDraft {
  readonly rootPath: string;
}

export interface AzureBlobDraft {
  readonly subscriptionId: string;
  readonly resourceGroup: string;
  readonly accountName: string;
  readonly container: string;
  readonly endpointSuffix: string;
  readonly credentialKind: 'accountKey' | 'sasToken';
  readonly accountKey: string;
  readonly sasToken: string;
}

export interface S3Draft {
  readonly endpoint: string;
  readonly region: string;
  readonly bucket: string;
  readonly accessKeyId: string;
  readonly secretAccessKey: string;
  readonly forcePathStyle: boolean;
}

export interface StorageDraft {
  readonly provider: StorageProviderKind;
  readonly filesystem: FilesystemDraft;
  readonly azureBlob: AzureBlobDraft;
  readonly s3: S3Draft;
}

export const emptyStorageDraft: StorageDraft = {
  provider: 'filesystem',
  filesystem: { rootPath: '' },
  azureBlob: {
    subscriptionId: '',
    resourceGroup: '',
    accountName: '',
    container: '',
    endpointSuffix: '',
    credentialKind: 'accountKey',
    accountKey: '',
    sasToken: '',
  },
  s3: {
    endpoint: '',
    region: '',
    bucket: '',
    accessKeyId: '',
    secretAccessKey: '',
    forcePathStyle: true,
  },
};

/** Fields that carry a credential and must never leave this browser tab. */
export const secretFields = [
  'azureBlob.accountKey',
  'azureBlob.sasToken',
  's3.accessKeyId',
  's3.secretAccessKey',
] as const;

const listeners = new Set<() => void>();
let draft: StorageDraft = emptyStorageDraft;

/**
 * The candidate storage configuration lives in memory only. Nothing here is
 * written to `localStorage`, `sessionStorage`, IndexedDB, or any query cache,
 * and the whole draft is dropped when the page unmounts or the session ends.
 */
export function getStorageDraft(): StorageDraft {
  return draft;
}

export function updateStorageDraft(update: Partial<StorageDraft>): void {
  draft = { ...draft, ...update };
  publish();
}

export function updateProviderDraft<
  K extends 'filesystem' | 'azureBlob' | 's3',
>(provider: K, update: Partial<StorageDraft[K]>): void {
  draft = { ...draft, [provider]: { ...draft[provider], ...update } };
  publish();
}

export function clearStorageDraft(): void {
  draft = emptyStorageDraft;
  publish();
}

export function subscribeToStorageDraft(onChange: () => void): () => void {
  listeners.add(onChange);
  return () => {
    listeners.delete(onChange);
  };
}

/** Every secret currently held, used only to assert they have been cleared. */
export function storageDraftSecrets(): readonly string[] {
  return [
    draft.azureBlob.accountKey,
    draft.azureBlob.sasToken,
    draft.s3.accessKeyId,
    draft.s3.secretAccessKey,
  ].filter(Boolean);
}

function publish() {
  for (const listener of listeners) {
    listener();
  }
}
