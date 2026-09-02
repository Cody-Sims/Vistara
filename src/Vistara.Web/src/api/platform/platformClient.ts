import {
  VistaraApiError,
  type ApiClientOptions,
} from '../generated/client';
import { credentialedFetch } from '../credentialedFetch';
import { sessionCredentials, type SessionCredentials } from '../credentials';
import type { ApiProblemDetails } from '../generated/models';
import { readRetryAfterSeconds, VistaraThrottledError } from './throttling';
import type { EntityTag } from '../generated/models';
import type {
  ApiKeyCollection,
  Capabilities,
  CreateApiKeyRequest,
  CreatedApiKey,
  CurrentUser,
  HostedSignOut,
  InviteTenantMemberRequest,
  JobCollection,
  JobQuery,
  JobStatus,
  LoginRequest,
  LoginResponse,
  TenantCollection,
  TenantMember,
  ProvisionedOwner,
  ProvisionFirstOwnerRequest,
  SetupState,
  StorageSummary,
  StorageValidationRequest,
  StorageValidationResponse,
  StorageValidationSupport,
  TenantPolicies,
  TenantMemberCollection,
  UpdateTenantMemberRequest,
  UpdateUserPreferencesRequest,
  UserPreferences,
} from './models';

export interface VersionedRequestOptions {
  readonly ifMatch: EntityTag;
}

export interface VersionedResult<T> {
  readonly data: T;
  readonly etag?: EntityTag;
}

/** Routes the API accepts without a session, and therefore without a token. */
const anonymousRoutes = new Set(['/api/v1/auth/login', '/api/v1/setup']);

export interface PlatformApiClientOptions extends ApiClientOptions {
  /**
   * The credential every client of this browser session spends. It defaults
   * to the shared one so the gallery and upload clients see each session read,
   * login, and rotation this client performs.
   */
  readonly credentials?: SessionCredentials;
}

/**
 * Typed boundary for the account, tenant, API key, capability, and job routes
 * published by the API. Gallery resources use the generated client.
 */
export class PlatformApiClient {
  readonly #baseUrl: string;
  readonly #fetch: typeof fetch;
  readonly #credentials: SessionCredentials;
  readonly #unauthorized = new Set<() => void>();

  public constructor(options: PlatformApiClientOptions = {}) {
    this.#baseUrl = options.baseUrl?.replace(/\/+$/, '') ?? '';
    this.#credentials = options.credentials ?? sessionCredentials;
    // Every unsafe request this client sends is answered for by the same
    // provider that answers for the gallery and upload clients.
    this.#fetch = credentialedFetch(options.fetch, this.#credentials);
    // This client owns the session routes, so it is how every other client
    // reads the session again when it needs a token it does not hold.
    this.#credentials.useRefresher(() => this.getSession());
  }

  public get antiforgeryHeaderName(): string {
    return this.#credentials.headerName;
  }

  /**
   * Notifies when a request that needed a session was refused. The session
   * provider uses this to end the session once, however many calls fail.
   */
  public onUnauthorized(listener: () => void): () => void {
    this.#unauthorized.add(listener);
    return () => {
      this.#unauthorized.delete(listener);
    };
  }

  public async getSession(): Promise<CurrentUser> {
    const user = await this.#request<CurrentUser>('GET', '/api/v1/me');
    this.#credentials.adopt(user);
    return user;
  }

  public async login(request: LoginRequest): Promise<LoginResponse> {
    const session = await this.#request<LoginResponse>(
      'POST',
      '/api/v1/auth/login',
      { body: request },
    );

    // Login answers the token beside the user rather than on it, and opens a
    // cookie session by definition.
    this.#credentials.adopt({
      ...session.user,
      authenticationKind: session.user.authenticationKind ?? 'cookie',
      csrfToken: session.csrfToken ?? session.user.csrfToken,
    });
    return session;
  }

  public async logout(): Promise<void> {
    try {
      await this.#request<void>('POST', '/api/v1/auth/logout');
    } finally {
      this.#credentials.clear();
    }
  }

  /**
   * Relying-party initiated sign-out. The Vistara session is revoked first,
   * and the answer only says where the provider session may be ended; sending
   * the browser there is the caller's decision, and nothing about it is read
   * from this client.
   *
   * There is deliberately no counterpart for starting sign-in: the start route
   * answers a redirect to the provider that only a browser navigation may
   * follow, so fetching it would either be refused or would strip the flow of
   * the top-level navigation it depends on.
   */
  public async signOutFromProvider(providerId: string): Promise<HostedSignOut> {
    try {
      return await this.#request<HostedSignOut>(
        'POST',
        `/api/v1/auth/oidc/${segment(providerId)}/sign-out`,
      );
    } finally {
      this.#credentials.clear();
    }
  }

  /** Reads whether first-run provisioning is still open. */
  public getSetupState(): Promise<SetupState> {
    return this.#request<SetupState>('GET', '/api/v1/setup');
  }

  /**
   * Creates the first workspace and owner. The password is sent once and is
   * never held by the client afterwards.
   */
  public provisionFirstOwner(
    request: ProvisionFirstOwnerRequest,
  ): Promise<ProvisionedOwner> {
    return this.#request<ProvisionedOwner>('POST', '/api/v1/setup', {
      body: request,
    });
  }

  public getPolicies(): Promise<TenantPolicies> {
    return this.#request<TenantPolicies>('GET', '/api/v1/admin/policies');
  }

  public getStorageSummary(): Promise<StorageSummary> {
    return this.#request<StorageSummary>('GET', '/api/v1/admin/storage');
  }

  /**
   * Asks whether this deployment can test a storage configuration at all. It
   * carries no credential, so it is safe to call before one is entered.
   */
  public getStorageValidationSupport(): Promise<StorageValidationSupport> {
    return this.#request<StorageValidationSupport>(
      'GET',
      '/api/v1/admin/storage/validate',
    );
  }

  /**
   * Checks a candidate storage configuration. The request body is built for
   * this call only; nothing about it is stored, cached, or logged.
   */
  public validateStorage(
    request: StorageValidationRequest,
    options: { signal?: AbortSignal } = {},
  ): Promise<StorageValidationResponse> {
    return this.#request<StorageValidationResponse>(
      'POST',
      '/api/v1/admin/storage/validate',
      { body: request, ...(options.signal ? { signal: options.signal } : {}) },
    );
  }

  public getCapabilities(): Promise<Capabilities> {
    return this.#request<Capabilities>('GET', '/api/v1/capabilities');
  }

  public listTenants(): Promise<TenantCollection> {
    return this.#request<TenantCollection>('GET', '/api/v1/tenants');
  }

  public listTenantMembers(tenantId: string): Promise<TenantMemberCollection> {
    return this.#request<TenantMemberCollection>(
      'GET',
      `/api/v1/tenants/${segment(tenantId)}/members`,
    );
  }

  public inviteTenantMember(
    tenantId: string,
    request: InviteTenantMemberRequest,
  ): Promise<TenantMember> {
    return this.#request<TenantMember>(
      'POST',
      `/api/v1/tenants/${segment(tenantId)}/members`,
      { body: request },
    );
  }

  public listApiKeys(): Promise<ApiKeyCollection> {
    return this.#request<ApiKeyCollection>('GET', '/api/v1/api-keys');
  }

  public createApiKey(request: CreateApiKeyRequest): Promise<CreatedApiKey> {
    return this.#request<CreatedApiKey>('POST', '/api/v1/api-keys', {
      body: request,
    });
  }

  public revokeApiKey(keyId: string): Promise<void> {
    return this.#request<void>(
      'DELETE',
      `/api/v1/api-keys/${segment(keyId)}`,
    );
  }

  public getJob(jobId: string): Promise<JobStatus> {
    return this.#request<JobStatus>('GET', `/api/v1/jobs/${segment(jobId)}`);
  }

  public listJobs(query: JobQuery = {}): Promise<JobCollection> {
    return this.#request<JobCollection>(
      'GET',
      appendQuery('/api/v1/jobs', query),
    );
  }

  public retryJob(
    jobId: string,
    options: VersionedRequestOptions,
  ): Promise<VersionedResult<JobStatus>> {
    return this.#versioned<JobStatus>(
      'POST',
      `/api/v1/jobs/${segment(jobId)}/retry`,
      { ifMatch: options.ifMatch },
    );
  }

  public cancelJob(
    jobId: string,
    options: VersionedRequestOptions,
  ): Promise<VersionedResult<JobStatus>> {
    return this.#versioned<JobStatus>(
      'POST',
      `/api/v1/jobs/${segment(jobId)}/cancel`,
      { ifMatch: options.ifMatch },
    );
  }

  public getPreferences(): Promise<VersionedResult<UserPreferences>> {
    return this.#versioned<UserPreferences>('GET', '/api/v1/me/preferences');
  }

  public updatePreferences(
    request: UpdateUserPreferencesRequest,
    options: VersionedRequestOptions,
  ): Promise<VersionedResult<UserPreferences>> {
    return this.#versioned<UserPreferences>('PATCH', '/api/v1/me/preferences', {
      body: request,
      ifMatch: options.ifMatch,
    });
  }

  public updateTenantMember(
    tenantId: string,
    userId: string,
    request: UpdateTenantMemberRequest,
    options: VersionedRequestOptions,
  ): Promise<VersionedResult<TenantMember>> {
    return this.#versioned<TenantMember>(
      'PATCH',
      `/api/v1/tenants/${segment(tenantId)}/members/${segment(userId)}`,
      { body: request, ifMatch: options.ifMatch },
    );
  }

  async #versioned<T>(
    method: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE',
    path: string,
    options: { body?: unknown; ifMatch?: EntityTag } = {},
  ): Promise<VersionedResult<T>> {
    const response = await this.#send(method, path, options);
    const etag = response.headers.get('ETag') as EntityTag | null;
    const data =
      response.status === 204
        ? (undefined as T)
        : ((await response.json()) as T);

    return etag === null ? { data } : { data, etag };
  }

  async #request<T>(
    method: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE',
    path: string,
    options: { body?: unknown; signal?: AbortSignal } = {},
  ): Promise<T> {
    const response = await this.#send(method, path, options);

    return response.status === 204
      ? (undefined as T)
      : ((await response.json()) as T);
  }

  async #send(
    method: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE',
    path: string,
    options: { body?: unknown; ifMatch?: EntityTag; signal?: AbortSignal },
  ): Promise<Response> {
    // The antiforgery header is added by the wrapped fetch, which reads the
    // session first when a reloaded browser holds the cookie but no token.
    const headers = new Headers({ Accept: 'application/json' });
    if (options.ifMatch) {
      headers.set('If-Match', options.ifMatch);
    }
    if (options.body !== undefined) {
      headers.set('Content-Type', 'application/json');
    }

    const response = await this.#fetch(`${this.#baseUrl}${path}`, {
      method,
      headers,
      credentials: 'same-origin',
      ...(options.signal ? { signal: options.signal } : {}),
      body:
        options.body === undefined ? undefined : JSON.stringify(options.body),
    });

    if (response.status === 401 && !anonymousRoutes.has(path)) {
      this.#credentials.clear();
      for (const listener of this.#unauthorized) {
        listener();
      }
    }

    if (!response.ok) {
      const problem = await readProblem(response);
      throw response.status === 429
        ? new VistaraThrottledError(
            problem,
            readRetryAfterSeconds(response.headers),
          )
        : new VistaraApiError(response.status, problem);
    }

    return response;
  }
}

function appendQuery(path: string, query: object): string {
  const parameters = new URLSearchParams();
  for (const [name, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') {
      continue;
    }

    if (Array.isArray(value)) {
      for (const item of value) {
        parameters.append(name, String(item));
      }
      continue;
    }

    parameters.set(name, String(value));
  }

  const encoded = parameters.toString();
  return encoded.length === 0 ? path : `${path}?${encoded}`;
}

async function readProblem(response: Response): Promise<ApiProblemDetails> {
  try {
    return (await response.json()) as ApiProblemDetails;
  } catch {
    return {
      type: 'about:blank',
      title: response.statusText || 'Request failed',
      status: response.status,
      code: 'unexpected_response',
      errors: {},
    };
  }
}

function segment(value: string): string {
  return encodeURIComponent(value);
}
