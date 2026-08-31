import {
  VistaraApiError,
  type ApiClientOptions,
} from '../generated/client';
import type { ApiProblemDetails } from '../generated/models';
import type { EntityTag } from '../generated/models';
import type {
  ApiKeyCollection,
  Capabilities,
  CreateApiKeyRequest,
  CreatedApiKey,
  CurrentUser,
  InviteTenantMemberRequest,
  JobCollection,
  JobQuery,
  JobStatus,
  LoginRequest,
  LoginResponse,
  TenantCollection,
  TenantMember,
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

/** Header the platform uses until `GET /api/v1/me` names another one. */
const defaultAntiforgeryHeader = 'X-Vistara-CSRF';

/**
 * Typed boundary for the account, tenant, API key, capability, and job routes
 * published by the API. Gallery resources use the generated client.
 */
export class PlatformApiClient {
  readonly #baseUrl: string;
  readonly #fetch: typeof fetch;
  #antiforgeryHeader = defaultAntiforgeryHeader;
  #antiforgeryToken = '';

  public constructor(options: ApiClientOptions = {}) {
    this.#baseUrl = options.baseUrl?.replace(/\/+$/, '') ?? '';
    this.#fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
  }

  public get antiforgeryHeaderName(): string {
    return this.#antiforgeryHeader;
  }

  public async getSession(): Promise<CurrentUser> {
    const user = await this.#request<CurrentUser>('GET', '/api/v1/me');
    this.#adoptHeaderName(user.csrfHeaderName);
    if (user.csrfToken) {
      this.#antiforgeryToken = user.csrfToken;
    }

    return user;
  }

  public async login(request: LoginRequest): Promise<LoginResponse> {
    const session = await this.#request<LoginResponse>(
      'POST',
      '/api/v1/auth/login',
      { body: request },
    );

    this.#adoptHeaderName(session.user.csrfHeaderName);
    this.#antiforgeryToken = session.csrfToken;
    return session;
  }

  public async logout(): Promise<void> {
    try {
      await this.#request<void>('POST', '/api/v1/auth/logout');
    } finally {
      this.#antiforgeryToken = '';
    }
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

  #adoptHeaderName(name: string | undefined) {
    if (name && /^[\w-]+$/.test(name)) {
      this.#antiforgeryHeader = name;
    }
  }

  /**
   * A reloaded browser holds the session cookie but no antiforgery token, so
   * the session is read once before the first unsafe request.
   */
  async #ensureAntiforgeryToken(): Promise<void> {
    if (this.#antiforgeryToken) {
      return;
    }

    try {
      await this.getSession();
    } catch {
      // An unreadable session leaves the request to fail on its own terms.
    }
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
    options: { body?: unknown } = {},
  ): Promise<T> {
    const response = await this.#send(method, path, options);

    return response.status === 204
      ? (undefined as T)
      : ((await response.json()) as T);
  }

  async #send(
    method: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE',
    path: string,
    options: { body?: unknown; ifMatch?: EntityTag },
  ): Promise<Response> {
    if (method !== 'GET' && path !== '/api/v1/auth/login') {
      await this.#ensureAntiforgeryToken();
    }

    const headers = new Headers({ Accept: 'application/json' });
    if (method !== 'GET' && this.#antiforgeryToken) {
      headers.set(this.#antiforgeryHeader, this.#antiforgeryToken);
    }
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
      body:
        options.body === undefined ? undefined : JSON.stringify(options.body),
    });

    if (!response.ok) {
      throw new VistaraApiError(response.status, await readProblem(response));
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
