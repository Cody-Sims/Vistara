import {
  VistaraApiError,
  type ApiClientOptions,
} from '../generated/client';
import type { ApiProblemDetails } from '../generated/models';
import type {
  ApiKeyCollection,
  Capabilities,
  CreateApiKeyRequest,
  CreatedApiKey,
  CurrentUser,
  InviteTenantMemberRequest,
  JobStatus,
  LoginRequest,
  LoginResponse,
  TenantCollection,
  TenantMember,
  TenantMemberCollection,
} from './models';

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

  #adoptHeaderName(name: string | undefined) {
    if (name && /^[\w-]+$/.test(name)) {
      this.#antiforgeryHeader = name;
    }
  }

  async #request<T>(
    method: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE',
    path: string,
    options: { body?: unknown } = {},
  ): Promise<T> {
    const headers = new Headers({ Accept: 'application/json' });
    if (method !== 'GET' && this.#antiforgeryToken) {
      headers.set(this.#antiforgeryHeader, this.#antiforgeryToken);
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

    return response.status === 204
      ? (undefined as T)
      : ((await response.json()) as T);
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

function segment(value: string): string {
  return encodeURIComponent(value);
}
