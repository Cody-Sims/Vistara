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

/**
 * The credential the API authenticated a request with, as published by
 * `GET /api/v1/me` and by the `user` of a successful login. Only `cookie` is
 * an interactive browser session; the other two are bound to one tenant and
 * carry the scopes of the key or token rather than of the membership role.
 */
export type AuthenticationKind = 'cookie' | 'apiKey' | 'bearer';

/** Response of `GET /api/v1/me`, and the `user` of a successful login. */
export interface CurrentUser {
  readonly userId: Uuid;
  readonly email: string;
  readonly displayName: string;
  /** The tenant this session is scoped to. */
  readonly tenantId?: Uuid;
  readonly role?: TenantRole;
  readonly tenants: readonly TenantMembership[];
  /**
   * How the API authenticated this session. Optional only so a deployment
   * that has not published it yet is read as an unknown credential rather
   * than mistaken for an interactive session.
   */
  readonly authenticationKind?: AuthenticationKind;
  /** Header the deployment expects the antiforgery token in. */
  readonly csrfHeaderName: string;
  /**
   * Antiforgery token for the current cookie session, used only on unsafe
   * requests. It is transient: a cookie session between issues has none, so
   * it never says which credential authenticated the session.
   */
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

/** Published job states; filters accept these spellings. */
export type JobState =
  | 'pending'
  | 'leased'
  | 'retryScheduled'
  | 'completed'
  | 'deadLettered';

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

/** Operator actions this release can apply to a job. */
export interface JobActions {
  readonly retry: boolean;
  readonly cancel: boolean;
}

export interface JobStatus {
  readonly id: Uuid;
  readonly type: string;
  readonly state: JobState;
  readonly attempts: number;
  readonly maxAttempts: number;
  readonly createdAt: UtcDateTime;
  readonly availableAt: UtcDateTime;
  readonly completedAt?: UtcDateTime;
  readonly failure?: JobFailure;
  readonly version: ResourceVersion;
  readonly actions: JobActions;
}

/**
 * One hosted identity provider a visitor may sign in with, as published by
 * `GET /api/v1/setup`. Only the key, the label, and the same-origin path to
 * start at are ever published: the directory, the client identifier, and the
 * authority stay on the server. Sign-in starts by navigating to `startUrl`;
 * it is never fetched, because the response is a redirect to the provider that
 * only the browser may follow.
 */
export interface SignInProvider {
  readonly id: string;
  readonly displayName: string;
  readonly startUrl: string;
}

/** Response of `GET /api/v1/setup`; tells the browser whether first run is open. */
export interface SetupState {
  readonly available: boolean;
  /**
   * Optional only so a deployment that publishes no provider list is read as
   * having none rather than failing the sign-in page.
   */
  readonly signInProviders?: readonly SignInProvider[];
}

/**
 * Where the browser may end the provider session, answered by relying-party
 * sign-out after the Vistara session has already been revoked. Absent when the
 * provider publishes no end-session endpoint, in which case signing out is
 * already complete.
 */
export interface HostedSignOut {
  readonly endSessionUrl?: string | null;
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
 * `managedIdentity` submits no secret and is the recommended default; the
 * other two are bounded ephemeral secrets used for one throwaway client.
 */
export type AzureCredentialKind =
  | 'managedIdentity'
  | 'accountKey'
  | 'sasToken';

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
    readonly credentialKind: AzureCredentialKind;
    /** Required for `accountKey`; never sent for `managedIdentity`. */
    readonly accountKey?: string;
    /** Required for `sasToken`; never sent for `managedIdentity`. */
    readonly sasToken?: string;
  };
  readonly s3?: {
    readonly endpoint: string;
    readonly region: string;
    readonly bucket: string;
    readonly forcePathStyle: boolean;
    /** Sent with `secretAccessKey`, or both omitted for an anonymous client. */
    readonly accessKeyId?: string;
    readonly secretAccessKey?: string;
    /** Only valid alongside an access key. */
    readonly sessionToken?: string;
  };
}

export type StorageCheckStatus = 'passed' | 'failed' | 'skipped';

/** The five checks a validation always answers with, in this order. */
export type StorageCheckId =
  | 'reachable'
  | 'authenticated'
  | 'read'
  | 'write'
  | 'delete';

export const storageCheckOrder: readonly StorageCheckId[] = [
  'reachable',
  'authenticated',
  'read',
  'write',
  'delete',
];

export interface StorageValidationCheck {
  readonly id: StorageCheckId;
  readonly status: StorageCheckStatus;
  /** Redacted, human-readable outcome; never contains a submitted secret. */
  readonly detail?: string;
}

/** Response of `GET /api/v1/admin/storage/validate`; no credential involved. */
export interface StorageValidationSupport {
  readonly supported: boolean;
  readonly providers: readonly StorageProviderKind[];
}

export interface StorageValidationResponse {
  readonly valid: boolean;
  readonly provider: StorageProviderKind;
  readonly checks: readonly StorageValidationCheck[];
  readonly message?: string;
}

export interface RetentionPolicies {
  readonly trashRetentionDays: number;
  readonly purgeGraceDays: number;
}

export interface SharingPolicies {
  readonly publicLinksEnabled: boolean;
  readonly maxLinkLifetimeDays: number;
  readonly requirePasswordForPublicLinks: boolean;
}

/** A `null` quota means the tenant has no limit; zero would allow nothing. */
export interface QuotaPolicies {
  readonly storageBytes: number | null;
  readonly dailyTransformPixels: number | null;
  readonly concurrentUploads: number | null;
}

export interface TenantPolicies {
  readonly retention: RetentionPolicies;
  readonly sharing: SharingPolicies;
  readonly quotas: QuotaPolicies;
  readonly version: ResourceVersion;
}
