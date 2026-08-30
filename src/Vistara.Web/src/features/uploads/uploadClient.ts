import type {
  CompletedUploadPart,
  SignedUploadRequest,
  UploadApi,
  UploadCommitResult,
  UploadStatus,
  UploadTransfer,
} from './uploadQueue';

interface UploadClientOptions {
  readonly baseUrl?: string;
  readonly fetch?: typeof fetch;
  readonly xhr?: () => XMLHttpRequest;
}

interface ApiProblem {
  readonly title?: string;
  readonly detail?: string;
  readonly code?: string;
}

export class FrozenUploadClient implements UploadApi, UploadTransfer {
  readonly #baseUrl: string;
  readonly #fetch: typeof fetch;
  readonly #xhr: () => XMLHttpRequest;

  public constructor(options: UploadClientOptions = {}) {
    this.#baseUrl = options.baseUrl?.replace(/\/+$/, '') ?? '';
    this.#fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
    this.#xhr = options.xhr ?? (() => new XMLHttpRequest());
  }

  public async create(file: File, sha256: string, idempotencyKey: string) {
    return this.#json<UploadStatus>('/api/v1/uploads', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': idempotencyKey,
      },
      body: JSON.stringify({
        fileName: file.name,
        sizeBytes: file.size,
        contentType: file.type,
        sha256,
      }),
    });
  }

  public status(uploadId: string) {
    return this.#json<UploadStatus>(`/api/v1/uploads/${segment(uploadId)}`);
  }

  public refreshParts(
    uploadId: string,
    partNumbers: readonly number[],
    version: number,
  ) {
    return this.#json<{
      readonly parts: readonly import('./uploadQueue').SignedUploadPart[];
    }>(`/api/v1/uploads/${segment(uploadId)}/parts`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'If-Match': entityTag(version),
      },
      body: JSON.stringify({ partNumbers }),
    });
  }

  public async commit(
    uploadId: string,
    parts: readonly CompletedUploadPart[],
    idempotencyKey: string,
    version: number,
  ): Promise<UploadCommitResult> {
    const response = await this.#request(
      `/api/v1/uploads/${segment(uploadId)}/commit`,
      {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Idempotency-Key': idempotencyKey,
          'If-Match': entityTag(version),
        },
        body: JSON.stringify({ parts }),
      },
    );
    return {
      status: (await response.json()) as UploadStatus,
      duplicate: response.headers.get('Idempotency-Replayed') === 'true',
    };
  }

  public async abort(uploadId: string, version: number) {
    await this.#request(`/api/v1/uploads/${segment(uploadId)}`, {
      method: 'DELETE',
      headers: { 'If-Match': entityTag(version) },
    });
  }

  public signed(
    request: SignedUploadRequest,
    body: Blob,
    onProgress: (sent: number, total: number) => void,
    signal: AbortSignal,
  ) {
    return new Promise<{ etag: string; checksum?: string }>((resolve, reject) => {
      const xhr = this.#xhr();
      xhr.open(request.method, request.url);
      Object.entries(request.headers).forEach(([name, value]) =>
        xhr.setRequestHeader(name, value),
      );
      xhr.upload.onprogress = (event) =>
        onProgress(event.loaded, event.lengthComputable ? event.total : body.size);
      xhr.onload = () => {
        if (xhr.status < 200 || xhr.status >= 300) {
          reject(new UploadStorageError(xhr.status));
          return;
        }
        const etag = xhr.getResponseHeader('ETag');
        if (!etag) {
          reject(new Error('Storage did not return the uploaded part identifier.'));
          return;
        }
        resolve({
          etag,
          checksum:
            xhr.getResponseHeader('x-amz-checksum-sha256') ??
            xhr.getResponseHeader('x-ms-content-crc64') ??
            undefined,
        });
      };
      xhr.onerror = () => reject(new Error('The storage upload was interrupted.'));
      xhr.onabort = () => reject(new DOMException('Cancelled', 'AbortError'));
      signal.addEventListener('abort', () => xhr.abort(), { once: true });
      xhr.send(body);
    });
  }

  public proxy(
    contentUrl: string,
    version: number,
    body: File,
    onProgress: (sent: number, total: number) => void,
    signal: AbortSignal,
  ) {
    return new Promise<number>((resolve, reject) => {
      const xhr = this.#xhr();
      xhr.open('PUT', `${this.#baseUrl}${contentUrl}`);
      xhr.withCredentials = true;
      xhr.setRequestHeader('Content-Type', body.type);
      xhr.setRequestHeader('If-Match', entityTag(version));
      xhr.upload.onprogress = (event) =>
        onProgress(event.loaded, event.lengthComputable ? event.total : body.size);
      xhr.onload = () => {
        if (xhr.status < 200 || xhr.status >= 300) {
          reject(new Error(`The upload service rejected the content (${xhr.status}).`));
          return;
        }
        const nextVersion = parseEntityTag(xhr.getResponseHeader('ETag'));
        if (nextVersion === undefined) {
          reject(new Error('The upload service did not return a version.'));
          return;
        }
        resolve(nextVersion);
      };
      xhr.onerror = () => reject(new Error('The proxy upload was interrupted.'));
      xhr.onabort = () => reject(new DOMException('Cancelled', 'AbortError'));
      signal.addEventListener('abort', () => xhr.abort(), { once: true });
      xhr.send(body);
    });
  }

  async #json<T>(path: string, init?: RequestInit) {
    const response = await this.#request(path, init);
    return (await response.json()) as T;
  }

  async #request(path: string, init?: RequestInit) {
    const response = await this.#fetch(`${this.#baseUrl}${path}`, {
      ...init,
      credentials: 'same-origin',
    });
    if (!response.ok) {
      let problem: ApiProblem | undefined;
      try {
        problem = (await response.json()) as ApiProblem;
      } catch {
        problem = undefined;
      }
      throw new UploadRequestError(
        response.status,
        problem?.code,
        problem?.detail ?? problem?.title,
      );
    }
    return response;
  }
}

export class UploadRequestError extends Error {
  public constructor(
    public readonly status: number,
    public readonly code?: string,
    detail?: string,
  ) {
    super(detail ?? `The upload request failed (${status}).`);
    this.name = 'UploadRequestError';
  }
}

export class UploadStorageError extends Error {
  public constructor(public readonly status: number) {
    super(`Storage rejected the upload (${status}).`);
    this.name = 'UploadStorageError';
  }
}

function entityTag(version: number) {
  return `"v${version}"`;
}

function parseEntityTag(value: string | null) {
  const match = /^"v(\d+)"$/.exec(value ?? '');
  return match ? Number(match[1]) : undefined;
}

function segment(value: string) {
  return encodeURIComponent(value);
}
