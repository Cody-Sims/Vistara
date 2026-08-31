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
  readonly scopes?: readonly string[];
  readonly expiresAt?: UtcDateTime;
}

export interface CreatedApiKey {
  readonly key: ApiKeySummary;
  /** Returned once; never stored by the browser. */
  readonly secret: string;
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
