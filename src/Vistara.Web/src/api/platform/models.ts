import type {
  CursorPage,
  EntityTag,
  ResourceVersion,
  UtcDateTime,
  Uuid,
} from '../generated/models';

export type TenantRole = 'TenantOwner' | 'TenantAdmin' | 'Member' | 'Viewer';

export type MembershipStatus = 'active' | 'invited' | 'suspended';

export type ThemePreference = 'system' | 'light' | 'dark';

export interface TenantMembership {
  readonly tenantId: Uuid;
  readonly tenantName: string;
  readonly role: TenantRole;
  readonly status: MembershipStatus;
  readonly joinedAt?: UtcDateTime;
}

export interface SessionUser {
  readonly id: Uuid;
  readonly displayName: string;
  readonly email: string;
  readonly platformAdmin: boolean;
}

export interface SessionPreferences {
  readonly theme?: ThemePreference;
  readonly locale?: string;
  readonly timeZone?: string;
  readonly density?: 'comfortable' | 'compact';
  readonly reducedMotion?: boolean;
  readonly screenReaderPagedMode?: boolean;
}

/** Response of `GET /api/v1/me`; also returned by a successful login. */
export interface SessionSnapshot {
  readonly user: SessionUser;
  readonly memberships: readonly TenantMembership[];
  readonly activeTenantId?: Uuid;
  readonly preferences: SessionPreferences;
  /** Antiforgery token bootstrapped for unsafe cookie-authenticated requests. */
  readonly antiforgeryToken?: string;
}

export interface LoginRequest {
  readonly email: string;
  readonly password: string;
  readonly rememberMe?: boolean;
}

export interface OidcProvider {
  readonly displayName: string;
  readonly startPath: string;
}

export interface AuthenticationCapabilities {
  readonly localAccounts: boolean;
  readonly oidc?: OidcProvider;
}

export interface UploadCapabilities {
  readonly maxUploadBytes?: number;
  readonly allowedContentTypes?: readonly string[];
  readonly multipart?: boolean;
}

export interface Capabilities {
  readonly database?: 'sqlite' | 'postgres';
  readonly storage?: string;
  readonly search?: string;
  readonly authentication?: AuthenticationCapabilities;
  readonly uploads?: UploadCapabilities;
}

export interface UpdatePreferencesRequest {
  readonly theme?: ThemePreference;
  readonly locale?: string;
  readonly timeZone?: string;
  readonly density?: 'comfortable' | 'compact';
  readonly reducedMotion?: boolean;
  readonly screenReaderPagedMode?: boolean;
}

export interface AdminUserQuery {
  readonly limit?: number;
  readonly cursor?: string;
  readonly search?: string;
  readonly role?: TenantRole;
  readonly status?: MembershipStatus;
}

export interface AdminUser {
  readonly id: Uuid;
  readonly displayName: string;
  readonly email: string;
  readonly role: TenantRole;
  readonly status: MembershipStatus;
  readonly createdAt: UtcDateTime;
  readonly lastSeenAt?: UtcDateTime;
  readonly version: ResourceVersion;
}

export interface UpdateAdminUserRequest {
  readonly role?: TenantRole;
  readonly status?: MembershipStatus;
}

export interface StorageBucket {
  readonly id: string;
  readonly kind: 'filesystem' | 's3' | 'azure' | 'gcs';
  readonly status: 'healthy' | 'degraded' | 'unavailable';
  readonly usedBytes: number;
  readonly quotaBytes?: number;
  readonly objectCount: number;
  readonly lastCheckedAt?: UtcDateTime;
  readonly message?: string;
}

export interface StorageOverview {
  readonly buckets: readonly StorageBucket[];
  readonly originalBytes: number;
  readonly derivativeBytes: number;
  readonly stagingBytes: number;
  readonly quotaBytes?: number;
  readonly pendingUploadBytes?: number;
}

export type JobState =
  | 'queued'
  | 'running'
  | 'succeeded'
  | 'failed'
  | 'dead'
  | 'cancelled';

export interface AdminJobQuery {
  readonly limit?: number;
  readonly cursor?: string;
  readonly states?: readonly JobState[];
  readonly kind?: string;
}

export interface AdminJob {
  readonly id: string;
  readonly kind: string;
  readonly state: JobState;
  readonly attempts?: number;
  readonly maxAttempts?: number;
  readonly queuedAt?: UtcDateTime;
  readonly startedAt?: UtcDateTime;
  readonly completedAt?: UtcDateTime;
  readonly nextAttemptAt?: UtcDateTime;
  readonly lastError?: string;
}

export interface RetentionPolicies {
  readonly trashRetentionDays?: number;
  readonly purgeGraceDays?: number;
}

export interface SharingPolicies {
  readonly publicLinksEnabled?: boolean;
  readonly maxLinkLifetimeDays?: number;
  readonly requirePasswordForPublicLinks?: boolean;
}

export interface QuotaPolicies {
  readonly storageBytes?: number;
  readonly dailyTransformPixels?: number;
  readonly concurrentUploads?: number;
}

export interface PolicySettings {
  readonly retention: RetentionPolicies;
  readonly sharing: SharingPolicies;
  readonly quotas: QuotaPolicies;
  readonly version: ResourceVersion;
}

export interface UpdatePolicySettingsRequest {
  readonly retention?: RetentionPolicies;
  readonly sharing?: SharingPolicies;
  readonly quotas?: QuotaPolicies;
}

export interface AuditQuery {
  readonly limit?: number;
  readonly cursor?: string;
  readonly action?: string;
  readonly actorId?: Uuid;
  readonly outcome?: AuditOutcome;
  readonly occurredFrom?: UtcDateTime;
  readonly occurredTo?: UtcDateTime;
}

export type AuditOutcome = 'succeeded' | 'denied' | 'failed';

export interface AuditActor {
  readonly kind: 'user' | 'apiKey' | 'system';
  readonly id?: Uuid;
  readonly displayName: string;
}

export interface AuditEvent {
  readonly id: string;
  readonly occurredAt: UtcDateTime;
  readonly actor: AuditActor;
  readonly action: string;
  readonly outcome: AuditOutcome;
  readonly resourceType?: string;
  readonly resourceId?: string;
  readonly requestId?: string;
}

export type AdminUserPage = CursorPage<AdminUser>;
export type AdminJobPage = CursorPage<AdminJob>;
export type AuditEventPage = CursorPage<AuditEvent>;

export type { EntityTag };
