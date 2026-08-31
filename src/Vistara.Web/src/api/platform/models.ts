import type { ResourceVersion, UtcDateTime, Uuid } from '../generated/models';

export type TenantRole = 'TenantOwner' | 'TenantAdmin' | 'Member' | 'Viewer';

export type MembershipStatus = 'Invited' | 'Active' | 'Suspended' | 'Removed';

/** A tenant the signed-in user belongs to, as published by `GET /api/v1/me`. */
export interface TenantMembership {
  readonly id: Uuid;
  readonly slug: string;
  readonly name: string;
  readonly role: TenantRole;
  readonly membershipStatus: MembershipStatus;
}

/** Response of `GET /api/v1/me`, and the `user` of a successful login. */
export interface CurrentUser {
  readonly userId: Uuid;
  readonly email: string;
  readonly displayName: string;
  /** The tenant this session is scoped to. */
  readonly tenantId?: Uuid;
  readonly role?: TenantRole;
  readonly tenants: readonly TenantMembership[];
  /** Header the deployment expects the antiforgery token in. */
  readonly csrfHeaderName: string;
  /** Token for the current cookie session; absent for non-browser principals. */
  readonly csrfToken?: string;
}

export interface LoginRequest {
  /** The email address or user name accepted by the deployment. */
  readonly login: string;
  readonly password: string;
  readonly tenantId?: Uuid;
}

export interface LoginResponse {
  readonly user: CurrentUser;
  readonly csrfToken: string;
}

export interface DatabaseCapabilities {
  readonly provider: string;
}

export interface StorageCapabilities {
  readonly provider: string;
  readonly directUpload: boolean;
  readonly multipartUpload: boolean;
  readonly rangeReads: boolean;
  readonly maxObjectBytes: number;
  readonly maxMultipartParts: number;
  readonly minMultipartPartBytes: number;
  readonly maxMultipartPartBytes: number;
}

export interface ImagingCapabilities {
  readonly provider: string;
  readonly inputFormats: readonly string[];
  readonly outputFormats: readonly string[];
  readonly maxEncodedBytes: number;
  readonly maxWidth: number;
  readonly maxHeight: number;
  readonly maxAggregatePixels: number;
  readonly maxFrames: number;
  readonly maxEstimatedDecodedBytes: number;
  readonly processingDeadlineSeconds: number;
  readonly maxConcurrentTransforms: number;
}

export interface UploadCapabilities {
  readonly maxBytes: number;
  readonly maxConcurrentUploads: number;
  readonly concurrencyUnlimited: boolean;
  readonly multipartThresholdBytes: number;
  readonly proxyUpload: boolean;
  readonly directUpload: boolean;
  readonly multipartUpload: boolean;
}

export interface SearchCapabilities {
  readonly text: boolean;
  readonly facets: boolean;
  readonly timeline: boolean;
  readonly providerNativeFullText: boolean;
}

export interface ApiCapabilities {
  readonly defaultPageSize: number;
  readonly maxPageSize: number;
  readonly maxProxyUploadBytes: number;
}

/** Response of `GET /api/v1/capabilities`. */
export interface Capabilities {
  readonly schemaVersion: number;
  readonly database: DatabaseCapabilities;
  readonly storage: StorageCapabilities;
  readonly imaging: ImagingCapabilities;
  readonly upload: UploadCapabilities;
  readonly search: SearchCapabilities;
  readonly api: ApiCapabilities;
}

export interface TenantSummary {
  readonly id: Uuid;
  readonly slug: string;
  readonly name: string;
  readonly status: string;
  readonly role: TenantRole;
  readonly membershipStatus: MembershipStatus;
  readonly joinedAt?: UtcDateTime;
}

export interface TenantCollection {
  readonly items: readonly TenantSummary[];
}

export interface TenantMember {
  readonly userId: Uuid;
  readonly email: string;
  readonly displayName: string;
  readonly role: TenantRole;
  readonly status: MembershipStatus;
  readonly invitedAt: UtcDateTime;
  readonly joinedAt?: UtcDateTime;
  readonly version: ResourceVersion;
}

export interface TenantMemberCollection {
  readonly items: readonly TenantMember[];
}

export interface InviteTenantMemberRequest {
  readonly email: string;
  readonly role: TenantRole;
}

export interface ApiKeySummary {
  readonly id: Uuid;
  readonly prefix: string;
  readonly ownerId: Uuid;
  readonly scopes: readonly string[];
  readonly status: string;
  readonly createdAt: UtcDateTime;
  readonly expiresAt?: UtcDateTime;
  readonly lastUsedAt?: UtcDateTime;
  readonly revokedAt?: UtcDateTime;
}

export interface ApiKeyCollection {
  readonly items: readonly ApiKeySummary[];
}

export interface CreateApiKeyRequest {
  /** At least one scope is required by the API. */
  readonly scopes: readonly string[];
  readonly expiresAt?: UtcDateTime;
}

export interface CreatedApiKey {
  readonly key: ApiKeySummary;
  /** Returned once; never stored by the browser. */
  readonly secret: string;
}

export type Density = 'comfortable' | 'compact';

/** Response of `GET /api/v1/me/preferences`, ETag `"v{version}"`. */
export interface UserPreferences {
  readonly density: Density;
  readonly reducedMotion: boolean;
  readonly screenReaderPagedMode: boolean;
  readonly locale?: string;
  readonly timeZone?: string;
  readonly version: ResourceVersion;
}

export interface UpdateUserPreferencesRequest {
  readonly density?: Density;
  readonly reducedMotion?: boolean;
  readonly screenReaderPagedMode?: boolean;
  readonly locale?: string | null;
  readonly timeZone?: string | null;
}

export interface UpdateTenantMemberRequest {
  readonly role?: TenantRole;
  readonly status?: MembershipStatus;
}

export type JobState =
  | 'Pending'
  | 'Leased'
  | 'RetryScheduled'
  | 'Completed'
  | 'DeadLettered';

export interface JobQuery {
  readonly states?: readonly JobState[];
  readonly type?: string;
  readonly limit?: number;
  readonly cursor?: string;
}

export interface JobCollection {
  readonly items: readonly JobStatus[];
  readonly nextCursor?: string;
}

export interface JobFailure {
  readonly code: string;
  readonly summary: string;
}

export interface JobStatus {
  readonly id: Uuid;
  readonly type: string;
  readonly state: string;
  readonly attempts: number;
  readonly maxAttempts: number;
  readonly createdAt: UtcDateTime;
  readonly availableAt: UtcDateTime;
  readonly completedAt?: UtcDateTime;
  readonly failure?: JobFailure;
  readonly version: ResourceVersion;
}

/** Response of `GET /api/v1/setup`; tells the browser whether first run is open. */
export interface SetupState {
  readonly available: boolean;
}

export interface ProvisionFirstOwnerRequest {
  readonly tenantSlug: string;
  readonly tenantName: string;
  readonly email: string;
  readonly displayName: string;
  readonly password: string;
}

export interface ProvisionedOwner {
  readonly tenantId: Uuid;
  readonly tenantSlug: string;
  readonly tenantName: string;
  readonly userId: Uuid;
  readonly email: string;
  readonly displayName: string;
  readonly role: TenantRole;
}

/** Consumption and health only; provider topology is never published. */
export interface StorageBucketSummary {
  readonly id: string;
  readonly kind: string;
  readonly status: string;
  readonly usedBytes: number;
  readonly quotaBytes: number;
  readonly objectCount: number;
  readonly lastCheckedAt: UtcDateTime;
  readonly message?: string;
}

export interface StorageSummary {
  readonly buckets: readonly StorageBucketSummary[];
  readonly originalBytes: number;
  readonly derivativeBytes: number;
  readonly stagingBytes: number;
  readonly quotaBytes: number;
  readonly pendingUploadBytes: number;
}

export type StorageProviderKind = 'filesystem' | 'azureBlob' | 's3';

/**
 * A candidate storage configuration. Secrets are only ever held in memory for
 * the length of one validation request and are never persisted anywhere.
 */
export interface StorageValidationRequest {
  readonly provider: StorageProviderKind;
  readonly filesystem?: {
    readonly rootPath: string;
  };
  readonly azureBlob?: {
    readonly accountName: string;
    readonly container: string;
    readonly endpointSuffix?: string;
    readonly credentialKind: 'accountKey' | 'sasToken';
    readonly accountKey?: string;
    readonly sasToken?: string;
  };
  readonly s3?: {
    readonly endpoint: string;
    readonly region: string;
    readonly bucket: string;
    readonly accessKeyId: string;
    readonly secretAccessKey: string;
    readonly forcePathStyle: boolean;
  };
}

export type StorageCheckStatus = 'passed' | 'failed' | 'skipped';

export interface StorageValidationCheck {
  readonly id: 'reachable' | 'authenticated' | 'read' | 'write' | 'delete';
  readonly status: StorageCheckStatus;
  /** Redacted, human-readable outcome; never contains a submitted secret. */
  readonly detail?: string;
}

/** Response of `GET /api/v1/admin/storage/validate`; no credential involved. */
export interface StorageValidationSupport {
  readonly supported: boolean;
  readonly providers?: readonly StorageProviderKind[];
}

export interface StorageValidationResponse {
  readonly valid: boolean;
  readonly provider: StorageProviderKind;
  readonly checks: readonly StorageValidationCheck[];
  readonly message?: string;
}
