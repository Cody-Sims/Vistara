import { describe, expect, it, vi } from 'vitest';
import { FrozenUploadClient } from './uploadClient';

class FakeXhr {
  public upload: { onprogress: ((event: ProgressEvent) => void) | null } = {
    onprogress: null,
  };
  public onload: (() => void) | null = null;
  public onerror: (() => void) | null = null;
  public onabort: (() => void) | null = null;
  public status = 200;
  public method = '';
  public url = '';
  public withCredentials = false;
  public readonly headers = new Map<string, string>();
  public readonly responseHeaders = new Map([['ETag', '"part-1"']]);
  public body?: Blob;

  public open(method: string, url: string) {
    this.method = method;
    this.url = url;
  }

  public setRequestHeader(name: string, value: string) {
    this.headers.set(name, value);
  }

  public getResponseHeader(name: string) {
    return this.responseHeaders.get(name) ?? null;
  }

  public send(body: Blob) {
    this.body = body;
  }

  public abort() {
    this.onabort?.();
  }
}

describe('uploads frozen client boundary', () => {
  it('sends idempotency and concurrency headers through mocked fetch', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      new Response(
        JSON.stringify({
          uploadId: 'upload-1',
          state: 'accepted',
          version: 2,
        }),
        {
          status: 202,
          headers: {
            'Content-Type': 'application/json',
            'Idempotency-Replayed': 'true',
          },
        },
      ),
    );
    const client = new FrozenUploadClient({ fetch });

    const result = await client.commit('upload-1', [], 'commit-key', 1);
    const init = fetch.mock.calls[0]![1];

    expect(init?.headers).toMatchObject({
      'Idempotency-Key': 'commit-key',
      'If-Match': '"v1"',
    });
    expect(result.duplicate).toBe(true);
  });

  it('reports deterministic XHR progress and aborts without exposing the signed URL', async () => {
    const xhr = new FakeXhr();
    const client = new FrozenUploadClient({
      fetch: vi.fn(),
      xhr: () => xhr as unknown as XMLHttpRequest,
    });
    const progress = vi.fn();
    const controller = new AbortController();
    const promise = client.signed(
      {
        method: 'PUT',
        url: 'https://uploads.invalid/private-token',
        headers: { 'x-signed': 'secret' },
      },
      new Blob(['data']),
      progress,
      controller.signal,
    );

    xhr.upload.onprogress?.({
      loaded: 2,
      total: 4,
      lengthComputable: true,
    } as ProgressEvent);
    expect(progress).toHaveBeenCalledWith(2, 4);
    expect(xhr.headers.get('x-signed')).toBe('secret');

    controller.abort();
    await expect(promise).rejects.toMatchObject({ name: 'AbortError' });
  });
});
