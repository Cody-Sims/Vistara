export type UploadPhase =
  | 'queued'
  | 'hashing'
  | 'uploading'
  | 'paused'
  | 'processing'
  | 'ready'
  | 'duplicate'
  | 'cancelled'
  | 'error';

export type UploadStrategy = 'direct' | 'proxy' | 'multipart';

export interface SignedUploadRequest {
  readonly method: string;
  readonly url: string;
  readonly headers: Readonly<Record<string, string>>;
}

export interface SignedUploadPart {
  readonly partNumber: number;
  readonly request: SignedUploadRequest;
  readonly minBytes: number;
  readonly maxBytes: number;
  readonly expiresAt: string;
}

export type UploadPlan =
  | {
      readonly mode: 'proxy';
      readonly contentUrl: string;
    }
  | {
      readonly mode: 'direct';
      readonly expiresAt?: string;
      readonly request: SignedUploadRequest;
    }
  | {
      readonly mode: 'multipart';
      readonly multipart: {
        readonly maxParts: number;
        readonly minPartBytes: number;
        readonly maxPartBytes: number;
        readonly parts: readonly SignedUploadPart[];
      };
    };

export interface CompletedUploadPart {
  readonly partNumber: number;
  readonly etag: string;
  readonly checksum?: string;
  readonly sizeBytes: number;
}

export interface UploadStatus {
  readonly uploadId: string;
  readonly fileName: string;
  readonly strategy: UploadStrategy;
  readonly state: string;
  readonly expectedSizeBytes: number;
  readonly declaredContentType: string;
  readonly sha256: string;
  readonly expiresAt: string;
  readonly version: number;
  readonly parts: readonly {
    readonly partNumber: number;
    readonly sizeBytes: number;
    readonly checksum?: string;
  }[];
  readonly plan?: UploadPlan;
}

export interface UploadCommitResult {
  readonly status: UploadStatus;
  readonly duplicate: boolean;
}

export interface UploadApi {
  create(
    file: File,
    sha256: string,
    idempotencyKey: string,
  ): Promise<UploadStatus>;
  status(uploadId: string): Promise<UploadStatus>;
  refreshParts(
    uploadId: string,
    partNumbers: readonly number[],
    version: number,
  ): Promise<{ readonly parts: readonly SignedUploadPart[] }>;
  commit(
    uploadId: string,
    parts: readonly CompletedUploadPart[],
    idempotencyKey: string,
    version: number,
  ): Promise<UploadStatus | UploadCommitResult>;
  abort(uploadId: string, version: number): Promise<void>;
}

export interface UploadTransfer {
  signed(
    request: SignedUploadRequest,
    body: Blob,
    onProgress: (sent: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<{ readonly etag: string; readonly checksum?: string }>;
  proxy(
    contentUrl: string,
    version: number,
    body: File,
    onProgress: (sent: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<number>;
}

export interface PersistedUpload {
  readonly id: string;
  readonly fileName: string;
  readonly sizeBytes: number;
  readonly contentType: string;
  readonly lastModified: number;
  readonly phase: UploadPhase;
  readonly progress: number;
  readonly sha256?: string;
  readonly uploadId?: string;
  readonly strategy?: UploadStrategy;
  readonly serverState?: string;
  readonly version?: number;
  readonly transferComplete?: boolean;
  readonly completedParts: readonly CompletedUploadPart[];
  readonly createKey: string;
  readonly commitKey: string;
}

export interface UploadPersistence {
  save(record: PersistedUpload): Promise<void>;
  remove(id: string): Promise<void>;
  load(): Promise<readonly PersistedUpload[]>;
}

export class MemoryUploadPersistence implements UploadPersistence {
  public records: PersistedUpload[] = [];

  public async save(record: PersistedUpload) {
    this.records = [
      ...this.records.filter((candidate) => candidate.id !== record.id),
      structuredClone(record),
    ];
  }

  public async remove(id: string) {
    this.records = this.records.filter((record) => record.id !== id);
  }

  public async load() {
    return structuredClone(this.records);
  }
}

export class IndexedDbUploadPersistence implements UploadPersistence {
  readonly #databaseName: string;

  public constructor(databaseName = 'vistara-upload-queue') {
    this.#databaseName = databaseName;
  }

  public async save(record: PersistedUpload) {
    const database = await this.#open();
    await transactionDone(
      database
        .transaction('uploads', 'readwrite')
        .objectStore('uploads')
        .put(record),
    );
    database.close();
  }

  public async remove(id: string) {
    const database = await this.#open();
    await transactionDone(
      database
        .transaction('uploads', 'readwrite')
        .objectStore('uploads')
        .delete(id),
    );
    database.close();
  }

  public async load(): Promise<readonly PersistedUpload[]> {
    const database = await this.#open();
    const records = await requestResult<PersistedUpload[]>(
      database.transaction('uploads').objectStore('uploads').getAll(),
    );
    database.close();
    return records;
  }

  async #open() {
    return new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open(this.#databaseName, 1);
      request.onupgradeneeded = () => {
        if (!request.result.objectStoreNames.contains('uploads')) {
          request.result.createObjectStore('uploads', { keyPath: 'id' });
        }
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }
}

export interface UploadQueueItem {
  readonly id: string;
  readonly fileName: string;
  readonly sizeBytes: number;
  readonly phase: UploadPhase;
  readonly progress: number;
  readonly message?: string;
  readonly strategy?: UploadStrategy;
  readonly needsFile: boolean;
  readonly canPause: boolean;
  readonly canResume: boolean;
  readonly canRetry: boolean;
  readonly canCancel: boolean;
}

interface MutableUpload {
  id: string;
  file?: File;
  fileName: string;
  sizeBytes: number;
  contentType: string;
  lastModified: number;
  phase: UploadPhase;
  progress: number;
  message?: string;
  sha256?: string;
  upload?: UploadStatus;
  plan?: UploadPlan;
  completedParts: CompletedUploadPart[];
  createKey: string;
  commitKey: string;
  controller?: AbortController;
  running: boolean;
  transferComplete: boolean;
}

interface UploadQueueOptions {
  readonly client: UploadApi;
  readonly transfer: UploadTransfer;
  readonly persistence?: UploadPersistence;
  readonly hashFile?: (file: File) => Promise<string>;
  readonly createId?: () => string;
  readonly now?: () => Date;
  readonly waitForPoll?: (signal: AbortSignal) => Promise<void>;
  readonly maxConcurrent?: number;
  readonly maxFileBytes?: number;
}

const allowedTypes = new Set(['image/jpeg', 'image/png', 'image/webp']);
const activePhases = new Set<UploadPhase>([
  'queued',
  'hashing',
  'uploading',
  'paused',
  'processing',
]);

export class UploadQueue {
  readonly #client: UploadApi;
  readonly #transfer: UploadTransfer;
  readonly #persistence?: UploadPersistence;
  readonly #hashFile: (file: File) => Promise<string>;
  readonly #createId: () => string;
  readonly #now: () => Date;
  readonly #waitForPoll: (signal: AbortSignal) => Promise<void>;
  readonly #maxConcurrent: number;
  readonly #maxFileBytes: number;
  readonly #items = new Map<string, MutableUpload>();
  readonly #listeners = new Set<() => void>();
  #snapshot: readonly UploadQueueItem[] = [];

  public constructor(options: UploadQueueOptions) {
    this.#client = options.client;
    this.#transfer = options.transfer;
    this.#persistence = options.persistence;
    this.#hashFile = options.hashFile ?? sha256File;
    this.#createId = options.createId ?? randomId;
    this.#now = options.now ?? (() => new Date());
    this.#waitForPoll =
      options.waitForPoll ??
      ((signal) =>
        new Promise<void>((resolve, reject) => {
          const timeout = window.setTimeout(resolve, 1_000);
          signal.addEventListener(
            'abort',
            () => {
              window.clearTimeout(timeout);
              reject(new DOMException('Cancelled', 'AbortError'));
            },
            { once: true },
          );
        }));
    this.#maxConcurrent = Math.max(1, options.maxConcurrent ?? 2);
    this.#maxFileBytes = options.maxFileBytes ?? 2 * 1_024 * 1_024 * 1_024;
  }

  public subscribe = (listener: () => void) => {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  };

  public getItems = () => this.#snapshot;

  public getItem(id: string) {
    return this.#snapshot.find((item) => item.id === id);
  }

  public hasActiveUploads = () =>
    this.#snapshot.some((item) => activePhases.has(item.phase));

  public addFiles(files: Iterable<File>) {
    const added: UploadQueueItem[] = [];
    for (const selected of files) {
      const item: MutableUpload = {
        id: this.#createId(),
        file: selected,
        fileName: selected.name,
        sizeBytes: selected.size,
        contentType: selected.type,
        lastModified: selected.lastModified,
        phase: 'queued',
        progress: 0,
        completedParts: [],
        createKey: this.#createId(),
        commitKey: this.#createId(),
        running: false,
        transferComplete: false,
      };
      item.message = this.#validate(selected);
      if (item.message) {
        item.phase = 'error';
      }
      this.#items.set(item.id, item);
      added.push(this.#publicItem(item));
      void this.#persist(item);
    }
    this.#emit();
    this.#schedule();
    return added;
  }

  public async restore() {
    if (!this.#persistence) {
      return;
    }
    let records: readonly PersistedUpload[];
    try {
      records = await this.#persistence.load();
    } catch {
      return;
    }
    for (const record of records) {
      if (['ready', 'duplicate', 'cancelled'].includes(record.phase)) {
        continue;
      }
      this.#items.set(record.id, {
        id: record.id,
        fileName: record.fileName,
        sizeBytes: record.sizeBytes,
        contentType: record.contentType,
        lastModified: record.lastModified,
        phase: 'paused',
        progress: record.progress,
        message: 'Choose the same file to resume this upload.',
        sha256: record.sha256,
        upload:
          record.uploadId && record.strategy && record.version !== undefined
            ? {
                uploadId: record.uploadId,
                fileName: record.fileName,
                strategy: record.strategy,
                state: record.serverState ?? 'uploadIssued',
                expectedSizeBytes: record.sizeBytes,
                declaredContentType: record.contentType,
                sha256: record.sha256 ?? '',
                expiresAt: '',
                version: record.version,
                parts: record.completedParts.map((part) => ({
                  partNumber: part.partNumber,
                  sizeBytes: part.sizeBytes,
                  checksum: part.checksum,
                })),
              }
            : undefined,
        completedParts: [...record.completedParts],
        createKey: record.createKey,
        commitKey: record.commitKey,
        running: false,
        transferComplete: record.transferComplete ?? record.phase === 'processing',
      });
    }
    this.#emit();
  }

  public resumeWithFile(id: string, file: File) {
    const item = this.#items.get(id);
    if (!item || item.phase !== 'paused') {
      return;
    }
    if (
      file.name !== item.fileName ||
      file.size !== item.sizeBytes ||
      file.type !== item.contentType
    ) {
      item.message = 'Select the same file name, size, and type to resume.';
      this.#emit();
      return;
    }
    item.file = file;
    this.resume(id);
  }

  public async pause(id: string) {
    const item = this.#items.get(id);
    if (!item || !item.running || item.phase === 'processing') {
      return;
    }
    item.phase = 'paused';
    item.message = 'Paused';
    item.controller?.abort();
    this.#emit();
    await this.#persist(item);
  }

  public resume(id: string) {
    const item = this.#items.get(id);
    if (!item || item.phase !== 'paused') {
      return;
    }
    item.phase = 'queued';
    item.message = undefined;
    this.#emit();
    void this.#persist(item);
    this.#schedule();
  }

  public retry(id: string) {
    const item = this.#items.get(id);
    if (
      !item ||
      item.phase !== 'error' ||
      !allowedTypes.has(item.contentType) ||
      item.sizeBytes <= 0 ||
      item.sizeBytes > this.#maxFileBytes
    ) {
      return;
    }
    item.phase = 'queued';
    item.message = undefined;
    this.#emit();
    void this.#persist(item);
    this.#schedule();
  }

  public async cancel(id: string) {
    const item = this.#items.get(id);
    if (!item || item.phase === 'cancelled') {
      return;
    }
    item.phase = 'cancelled';
    item.message = 'Cancelled';
    item.controller?.abort();
    item.plan = undefined;
    this.#emit();
    if (item.upload) {
      try {
        await this.#client.abort(item.upload.uploadId, item.upload.version);
      } catch {
        item.message = 'Cancelled locally; the server will clean up the upload.';
        this.#emit();
      }
    }
    try {
      await this.#persistence?.remove(item.id);
    } catch {
      // Browser storage failure must not prevent cancellation.
    }
  }

  #validate(file: File) {
    if (!allowedTypes.has(file.type)) {
      return 'Only JPEG, PNG, and WebP images can be uploaded.';
    }
    if (file.size === 0) {
      return 'The selected file is empty.';
    }
    if (file.size > this.#maxFileBytes) {
      return `The selected file exceeds the ${formatBytes(this.#maxFileBytes)} limit.`;
    }
    const duplicate = [...this.#items.values()].some(
      (item) =>
        item.fileName === file.name &&
        item.sizeBytes === file.size &&
        item.lastModified === file.lastModified &&
        item.phase !== 'cancelled',
    );
    return duplicate ? 'This file is already in the upload queue.' : undefined;
  }

  #schedule() {
    queueMicrotask(() => {
      let available =
        this.#maxConcurrent -
        [...this.#items.values()].filter((item) => item.running).length;
      for (const item of this.#items.values()) {
        if (available === 0) {
          break;
        }
        if (item.phase === 'queued' && !item.running) {
          available -= 1;
          item.running = true;
          void this.#run(item).finally(() => {
            item.running = false;
            item.controller = undefined;
            this.#schedule();
          });
        }
      }
    });
  }

  async #run(item: MutableUpload) {
    const controller = new AbortController();
    item.controller = controller;
    try {
      if (!item.file) {
        this.#update(
          item,
          'paused',
          item.progress,
          'Choose the same file to resume this upload.',
        );
        return;
      }
      if (!item.sha256) {
        this.#update(item, 'hashing', 0, 'Calculating file fingerprint');
        item.sha256 = await this.#hashFile(item.file);
      }
      this.#throwIfStopped(item, controller.signal);

      if (!item.upload) {
        item.upload = await this.#client.create(
          item.file,
          item.sha256,
          item.createKey,
        );
        item.plan = item.upload.plan;
        await this.#persist(item);
      }
      this.#throwIfStopped(item, controller.signal);

      if (
        item.upload.state === 'accepted' ||
        !['pending', 'uploadIssued'].includes(item.upload.state)
      ) {
        this.#update(item, 'processing', item.progress, 'Processing image');
        await this.#followProcessing(item, controller.signal);
        return;
      }

      if (!item.transferComplete) {
        await this.#uploadContent(item, controller.signal);
        item.transferComplete = true;
        item.plan = undefined;
        await this.#persist(item);
      }
      this.#throwIfStopped(item, controller.signal);

      this.#update(item, 'processing', 100, 'Processing image');
      const committed = await this.#client.commit(
        item.upload.uploadId,
        item.completedParts,
        item.commitKey,
        item.upload.version,
      );
      const result = normalizeCommit(committed);
      item.upload = result.status;
      if (result.duplicate && result.status.state === 'accepted') {
        this.#finish(item, 'duplicate', 'Already uploaded; linked to the existing image.');
        return;
      }
      await this.#followProcessing(item, controller.signal);
    } catch (error) {
      if (isAbort(error) || item.phase === 'paused' || item.phase === 'cancelled') {
        return;
      }
      this.#update(item, 'error', item.progress, safeErrorMessage(error));
    }
  }

  async #uploadContent(item: MutableUpload, signal: AbortSignal) {
    const upload = item.upload;
    if (!upload) {
      throw new Error('The upload session is unavailable.');
    }
    let plan = item.plan;
    if (!plan || (plan.mode === 'direct' && this.#isExpired(plan.expiresAt))) {
      const refreshed = await this.#client.create(
        requireFile(item),
        item.sha256!,
        item.createKey,
      );
      item.upload = refreshed;
      plan = refreshed.plan;
      item.plan = plan;
    }
    if (!plan) {
      if (upload.state === 'accepted') {
        item.transferComplete = true;
        return;
      }
      throw new Error('The server did not provide an upload plan.');
    }

    this.#update(item, 'uploading', item.progress, `Uploading by ${plan.mode}`);
    if (plan.mode === 'proxy') {
      const version = await this.#transfer.proxy(
        plan.contentUrl,
        item.upload!.version,
        requireFile(item),
        (sent, total) => this.#progress(item, sent, total),
        signal,
      );
      item.upload = { ...item.upload!, version };
      return;
    }
    if (plan.mode === 'direct') {
      await this.#transfer.signed(
        plan.request,
        requireFile(item),
        (sent, total) => this.#progress(item, sent, total),
        signal,
      );
      return;
    }
    await this.#uploadMultipart(item, plan, signal);
  }

  async #uploadMultipart(
    item: MutableUpload,
    plan: Extract<UploadPlan, { mode: 'multipart' }>,
    signal: AbortSignal,
  ) {
    const details = plan.multipart;
    const file = requireFile(item);
    const partSize = Math.max(
      details.minPartBytes,
      Math.ceil(file.size / details.maxParts),
    );
    if (partSize > details.maxPartBytes) {
      throw new Error('The file cannot be split within the provider limits.');
    }
    const count = Math.ceil(file.size / partSize);
    const progress = new Map<number, number>();
    const completed = new Map(
      item.completedParts.map((part) => [part.partNumber, part]),
    );
    const initialPlans = new Map(
      details.parts.map((part) => [part.partNumber, part]),
    );

    for (let partNumber = 1; partNumber <= count; partNumber += 1) {
      if (completed.has(partNumber)) {
        progress.set(partNumber, completed.get(partNumber)!.sizeBytes);
        continue;
      }
      this.#throwIfStopped(item, signal);
      let partPlan = initialPlans.get(partNumber);
      if (!partPlan || this.#isExpired(partPlan.expiresAt)) {
        const refreshed = await this.#client.refreshParts(
          item.upload!.uploadId,
          [partNumber],
          item.upload!.version,
        );
        partPlan = refreshed.parts[0];
      }
      if (!partPlan || partPlan.partNumber !== partNumber) {
        throw new Error('The server returned an invalid multipart plan.');
      }
      const start = (partNumber - 1) * partSize;
      const end = Math.min(file.size, start + partSize);
      const blob = file.slice(start, end, file.type);
      if (blob.size < partPlan.minBytes || blob.size > partPlan.maxBytes) {
        throw new Error('A multipart chunk is outside the provider limits.');
      }
      const reportPartProgress = (sent: number) => {
        progress.set(partNumber, sent);
        this.#progress(
          item,
          [...progress.values()].reduce((sum, value) => sum + value, 0),
          file.size,
        );
      };
      let uploaded;
      try {
        uploaded = await this.#transfer.signed(
          partPlan.request,
          blob,
          reportPartProgress,
          signal,
        );
      } catch (error) {
        if (!isExpiredStoragePlan(error)) {
          throw error;
        }
        const refreshed = await this.#client.refreshParts(
          item.upload!.uploadId,
          [partNumber],
          item.upload!.version,
        );
        const replacement = refreshed.parts[0];
        if (!replacement || replacement.partNumber !== partNumber) {
          throw new Error('The server returned an invalid multipart plan.', {
            cause: error,
          });
        }
        uploaded = await this.#transfer.signed(
          replacement.request,
          blob,
          reportPartProgress,
          signal,
        );
      }
      const completedPart = {
        partNumber,
        etag: uploaded.etag,
        checksum: uploaded.checksum,
        sizeBytes: blob.size,
      };
      completed.set(partNumber, completedPart);
      item.completedParts = [...completed.values()].sort(
        (left, right) => left.partNumber - right.partNumber,
      );
      await this.#persist(item);
    }
  }

  async #followProcessing(item: MutableUpload, signal: AbortSignal) {
    while (item.upload) {
      const state = item.upload.state;
      if (state === 'accepted') {
        this.#finish(item, 'ready', 'Ready');
        return;
      }
      if (state === 'aborted') {
        this.#finish(item, 'cancelled', 'Cancelled');
        return;
      }
      if (state === 'expired' || state === 'rejected') {
        this.#finish(item, 'error', 'Processing failed.');
        return;
      }
      await this.#waitForPoll(signal);
      item.upload = await this.#client.status(item.upload.uploadId);
      await this.#persist(item);
    }
  }

  #finish(item: MutableUpload, phase: UploadPhase, message: string) {
    item.plan = undefined;
    this.#update(item, phase, 100, message);
    void this.#removePersisted(item.id);
  }

  #progress(item: MutableUpload, sent: number, total: number) {
    const progress =
      total <= 0 ? 0 : Math.max(0, Math.min(100, Math.round((sent / total) * 100)));
    this.#update(item, 'uploading', progress, `${progress}% uploaded`);
  }

  #update(
    item: MutableUpload,
    phase: UploadPhase,
    progress: number,
    message?: string,
  ) {
    item.phase = phase;
    item.progress = progress;
    item.message = message;
    this.#emit();
    void this.#persist(item);
  }

  #throwIfStopped(item: MutableUpload, signal: AbortSignal) {
    if (signal.aborted || item.phase === 'paused' || item.phase === 'cancelled') {
      throw new DOMException('Stopped', 'AbortError');
    }
  }

  #isExpired(expiresAt?: string) {
    if (!expiresAt) {
      return false;
    }
    return Date.parse(expiresAt) <= this.#now().getTime() + 5_000;
  }

  #emit() {
    this.#snapshot = [...this.#items.values()].map((item) =>
      this.#publicItem(item),
    );
    this.#listeners.forEach((listener) => listener());
  }

  #publicItem(item: MutableUpload): UploadQueueItem {
    return {
      id: item.id,
      fileName: item.fileName,
      sizeBytes: item.sizeBytes,
      phase: item.phase,
      progress: item.progress,
      message: item.message,
      strategy: item.upload?.strategy,
      canPause:
        item.phase === 'hashing' || item.phase === 'uploading' || item.phase === 'queued',
      needsFile: item.phase === 'paused' && !item.file,
      canResume: item.phase === 'paused' && Boolean(item.file),
      canRetry:
        item.phase === 'error' &&
        allowedTypes.has(item.contentType) &&
        item.sizeBytes > 0 &&
        item.sizeBytes <= this.#maxFileBytes,
      canCancel: !['ready', 'duplicate', 'cancelled'].includes(item.phase),
    };
  }

  async #persist(item: MutableUpload) {
    if (!this.#persistence || ['ready', 'duplicate', 'cancelled'].includes(item.phase)) {
      return;
    }
    try {
      await this.#persistence.save({
        id: item.id,
        fileName: item.fileName,
        sizeBytes: item.sizeBytes,
        contentType: item.contentType,
        lastModified: item.lastModified,
        phase: item.phase,
        progress: item.progress,
        sha256: item.sha256,
        uploadId: item.upload?.uploadId,
        strategy: item.upload?.strategy,
        serverState: item.upload?.state,
        version: item.upload?.version,
        transferComplete: item.transferComplete,
        completedParts: item.completedParts,
        createKey: item.createKey,
        commitKey: item.commitKey,
      });
    } catch {
      // Uploading continues if optional browser persistence is unavailable.
    }
  }

  async #removePersisted(id: string) {
    try {
      await this.#persistence?.remove(id);
    } catch {
      // A stale non-sensitive resume record is safe to ignore.
    }
  }
}

function normalizeCommit(
  result: UploadStatus | UploadCommitResult,
): UploadCommitResult {
  return 'status' in result
    ? result
    : { status: result, duplicate: false };
}

function safeErrorMessage(error: unknown) {
  if (error instanceof Error && error.message && !/^https?:/i.test(error.message)) {
    return error.message;
  }
  return 'The upload could not be completed. Try again.';
}

function isAbort(error: unknown) {
  return error instanceof DOMException && error.name === 'AbortError';
}

function isExpiredStoragePlan(error: unknown) {
  if (!error || typeof error !== 'object' || !('status' in error)) {
    return false;
  }
  return error.status === 401 || error.status === 403;
}

function requireFile(item: MutableUpload) {
  if (!item.file) {
    throw new Error('Choose the same file to resume this upload.');
  }
  return item.file;
}

async function sha256File(file: File) {
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
  return [...new Uint8Array(digest)]
    .map((value) => value.toString(16).padStart(2, '0'))
    .join('');
}

function randomId() {
  return crypto.randomUUID();
}

function formatBytes(bytes: number) {
  const gibibytes = bytes / (1_024 * 1_024 * 1_024);
  return `${gibibytes.toFixed(gibibytes >= 10 ? 0 : 1)} GiB`;
}

function transactionDone(request: IDBRequest) {
  return new Promise<void>((resolve, reject) => {
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error);
  });
}

function requestResult<T>(request: IDBRequest<T>) {
  return new Promise<T>((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}
