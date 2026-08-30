import {
  VistaraApiError,
  type ApiClientOptions,
  type ApiResponse,
} from '../generated/client';
import type { ApiProblemDetails, EntityTag } from '../generated/models';
import type {
  AdminJob,
  AdminJobPage,
  AdminJobQuery,
  AdminUser,
  AdminUserPage,
  AdminUserQuery,
  AuditEventPage,
  AuditQuery,
  Capabilities,
  LoginRequest,
  PolicySettings,
  SessionSnapshot,
  StorageOverview,
  UpdateAdminUserRequest,
  UpdatePolicySettingsRequest,
  UpdatePreferencesRequest,
} from './models';

export interface VersionedRequestOptions {
  readonly ifMatch: EntityTag;
}

/**
 * Typed boundary for the session, account, and platform administration routes
 * described in the specification. Gallery resources use the generated client.
 */
export class PlatformApiClient {
  readonly #baseUrl: string;
  readonly #fetch: typeof fetch;
  #antiforgeryToken = '';

  public constructor(options: ApiClientOptions = {}) {
    this.#baseUrl = options.baseUrl?.replace(/\/+$/, '') ?? '';
    this.#fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
  }

  public get antiforgeryToken(): string {
    return this.#antiforgeryToken;
  }

  public async getSession(): Promise<SessionSnapshot> {
    const response = await this.#request<SessionSnapshot>('GET', '/api/v1/me');
    return this.#adoptSession(response.data);
  }

  public async login(request: LoginRequest): Promise<SessionSnapshot> {
    const response = await this.#request<SessionSnapshot>(
      'POST',
      '/api/v1/auth/login',
      { body: request },
    );
    return this.#adoptSession(response.data);
  }

  public async logout(): Promise<void> {
    await this.#request<void>('POST', '/api/v1/auth/logout');
    this.#antiforgeryToken = '';
  }

  public async getCapabilities(): Promise<Capabilities> {
    const response = await this.#request<Capabilities>(
      'GET',
      '/api/v1/capabilities',
    );
    return response.data;
  }

  public async updatePreferences(
    request: UpdatePreferencesRequest,
  ): Promise<SessionSnapshot> {
    const response = await this.#request<SessionSnapshot>(
      'PATCH',
      '/api/v1/me/preferences',
      { body: request },
    );
    return this.#adoptSession(response.data);
  }

  public listAdminUsers(query: AdminUserQuery = {}) {
    return this.#request<AdminUserPage>(
      'GET',
      appendQuery('/api/v1/admin/users', query),
    );
  }

  public updateAdminUser(
    id: string,
    request: UpdateAdminUserRequest,
    options: VersionedRequestOptions,
  ) {
    return this.#request<AdminUser>(
      'PATCH',
      `/api/v1/admin/users/${segment(id)}`,
      { body: request, ifMatch: options.ifMatch },
    );
  }

  public getStorageOverview() {
    return this.#request<StorageOverview>('GET', '/api/v1/admin/storage');
  }

  public listAdminJobs(query: AdminJobQuery = {}) {
    return this.#request<AdminJobPage>(
      'GET',
      appendQuery('/api/v1/admin/jobs', query),
    );
  }

  public retryJob(id: string) {
    return this.#request<AdminJob>(
      'POST',
      `/api/v1/admin/jobs/${segment(id)}/retry`,
    );
  }

  public cancelJob(id: string) {
    return this.#request<AdminJob>(
      'POST',
      `/api/v1/admin/jobs/${segment(id)}/cancel`,
    );
  }

  public getPolicies() {
    return this.#request<PolicySettings>('GET', '/api/v1/admin/policies');
  }

  public updatePolicies(
    request: UpdatePolicySettingsRequest,
    options: VersionedRequestOptions,
  ) {
    return this.#request<PolicySettings>('PATCH', '/api/v1/admin/policies', {
      body: request,
      ifMatch: options.ifMatch,
    });
  }

  public listAuditEvents(query: AuditQuery = {}) {
    return this.#request<AuditEventPage>(
      'GET',
      appendQuery('/api/v1/admin/audit', query),
    );
  }

  #adoptSession(session: SessionSnapshot): SessionSnapshot {
    if (session.antiforgeryToken) {
      this.#antiforgeryToken = session.antiforgeryToken;
    }

    return session;
  }

  async #request<T>(
    method: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE',
    path: string,
    options: { body?: unknown; ifMatch?: EntityTag } = {},
  ): Promise<ApiResponse<T>> {
    const headers = new Headers();
    if (method !== 'GET') {
      headers.set('Accept', 'application/json');
      if (this.#antiforgeryToken) {
        headers.set('X-CSRF-TOKEN', this.#antiforgeryToken);
      }
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

    const etag = response.headers.get('ETag') as EntityTag | null;
    const data =
      response.status === 204
        ? (undefined as T)
        : ((await response.json()) as T);
    return etag === null ? { data } : { data, etag };
  }
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

function segment(value: string): string {
  return encodeURIComponent(value);
}
