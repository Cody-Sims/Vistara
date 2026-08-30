import {
  useCallback,
  useEffect,
  useState,
  type FormEvent,
} from 'react';
import {
  VistaraApiError,
  type VistaraApiClient,
} from '../../api/generated/client';
import type { EntityTag, Tag } from '../../api/generated/models';
import styles from './tags.module.css';
import { Skeleton } from '../../components';

type TagsClient = Pick<
  VistaraApiClient,
  'listTags' | 'createTag' | 'updateTag' | 'deleteTag'
>;

interface TagsViewProps {
  client: TagsClient;
  selectedTagIds?: readonly string[];
  onFilterChange?: (tagIds: readonly string[]) => void;
}

interface Draft {
  id?: string;
  name: string;
  color: string;
  version?: number;
}

const emptyDraft: Draft = { name: '', color: '#82d4bb' };

export function TagsView({
  client,
  selectedTagIds = [],
  onFilterChange,
}: TagsViewProps) {
  const [tags, setTags] = useState<readonly Tag[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [search, setSearch] = useState('');
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [editing, setEditing] = useState(false);
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState('');
  const [alert, setAlert] = useState('');

  const load = useCallback(
    async (query = search) => {
      setState('loading');
      setAlert('');
      try {
        const response = await client.listTags(
          query.trim() ? { search: query.trim() } : {},
        );
        setTags(response.data.items);
        setState('ready');
      } catch {
        setState('error');
      }
    },
    [client, search],
  );

  useEffect(() => {
    let active = true;
    void client.listTags({}).then(
      (response) => {
        if (!active) return;
        setTags(response.data.items);
        setState('ready');
      },
      () => {
        if (active) setState('error');
      },
    );
    return () => {
      active = false;
    };
  }, [client]);

  async function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await load(search);
  }

  async function saveTag(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const name = draft.name.trim();
    if (!name) {
      setAlert('Enter a tag name.');
      return;
    }

    setPending(true);
    setAlert('');
    setMessage('');

    if (!draft.id || draft.version === undefined) {
      try {
        const response = await client.createTag(
          { name, color: draft.color },
          { idempotencyKey: createIdempotencyKey() },
        );
        setTags((current) => [...current, response.data]);
        setDraft(emptyDraft);
        setEditing(false);
        setMessage(`${response.data.name} was created.`);
      } catch {
        setAlert('The tag could not be created.');
      } finally {
        setPending(false);
      }
      return;
    }

    const previous = tags;
    const id = draft.id;
    setTags((current) =>
      current.map((tag) =>
        tag.id === id ? { ...tag, name, color: draft.color } : tag,
      ),
    );
    setEditing(false);
    try {
      const response = await client.updateTag(
        id,
        { name, color: draft.color },
        {
          ifMatch: versionTag(draft.version),
          idempotencyKey: createIdempotencyKey(),
        },
      );
      setTags((current) =>
        current.map((tag) => (tag.id === id ? response.data : tag)),
      );
      setDraft(emptyDraft);
      setMessage(`${response.data.name} was saved.`);
    } catch (error) {
      if (isConflict(error)) {
        await reconcile();
      } else {
        setTags(previous);
        setEditing(true);
        setAlert('The tag could not be saved.');
      }
    } finally {
      setPending(false);
    }
  }

  async function removeTag(tag: Tag) {
    const previous = tags;
    setTags((current) => current.filter((item) => item.id !== tag.id));
    setPending(true);
    setAlert('');
    try {
      await client.deleteTag(tag.id, {
        ifMatch: versionTag(tag.version),
        idempotencyKey: createIdempotencyKey(),
      });
      setMessage(`${tag.name} was deleted.`);
    } catch (error) {
      if (isConflict(error)) {
        await reconcile();
      } else {
        setTags(previous);
        setAlert('The tag could not be deleted.');
      }
    } finally {
      setPending(false);
    }
  }

  async function reconcile() {
    try {
      const response = await client.listTags(
        search.trim() ? { search: search.trim() } : {},
      );
      setTags(response.data.items);
      setDraft(emptyDraft);
      setEditing(false);
      setAlert('Tags changed elsewhere. The latest list was restored.');
    } catch {
      setAlert('Tags changed elsewhere, but the latest list could not be loaded.');
    }
  }

  function toggleFilter(id: string, checked: boolean) {
    const next = checked
      ? [...selectedTagIds, id]
      : selectedTagIds.filter((selectedId) => selectedId !== id);
    onFilterChange?.(next);
  }

  return (
    <section className={styles.page} aria-labelledby="tags-heading">
      <header className={styles.header}>
        <div>
          <p className={styles.eyebrow}>Flat taxonomy</p>
          <h1 id="tags-heading">Tags</h1>
        </div>
        <p>Filter the gallery or maintain the shared flat tag list.</p>
      </header>

      {alert ? (
        <p className={styles.error} role="alert">
          {alert}
        </p>
      ) : null}
      {message ? (
        <p className={styles.notice} role="status">
          {message}
        </p>
      ) : null}

      <form className={styles.search} role="search" onSubmit={submitSearch}>
        <label>
          Search tags
          <input
            type="search"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
        </label>
        <button type="submit">Search</button>
      </form>

      {state === 'loading' ? (
        <div aria-busy="true">
          <p role="status">Loading tags…</p>
          <Skeleton count={5} shape="row" />
        </div>
      ) : null}
      {state === 'error' ? (
        <div className={styles.error} role="alert">
          <p>Tags could not be loaded.</p>
          <button type="button" onClick={() => void load()}>
            Try again
          </button>
        </div>
      ) : null}

      {state === 'ready' ? (
        <div className={styles.layout}>
          <fieldset className={styles.filters}>
            <legend>Filter by tags</legend>
            {tags.length === 0 ? (
              <p>No matching tags.</p>
            ) : (
              tags.map((tag) => (
                <label key={tag.id} className={styles.filter}>
                  <input
                    type="checkbox"
                    checked={selectedTagIds.includes(tag.id)}
                    onChange={(event) =>
                      toggleFilter(tag.id, event.target.checked)
                    }
                    aria-label={`Filter by ${tag.name}`}
                  />
                  <span
                    className={styles.swatch}
                    style={{ backgroundColor: tag.color ?? 'transparent' }}
                    aria-hidden="true"
                  />
                  <span>{tag.name}</span>
                  <small>{tag.assetCount}</small>
                </label>
              ))
            )}
          </fieldset>

          <section className={styles.editorPanel} aria-labelledby="tag-editor">
            <div className={styles.editorHeading}>
              <h2 id="tag-editor">{draft.id ? 'Edit tag' : 'Create tag'}</h2>
              {draft.id ? (
                <button
                  type="button"
                  className={styles.secondary}
                  onClick={() => {
                    setDraft(emptyDraft);
                    setEditing(false);
                  }}
                >
                  Cancel edit
                </button>
              ) : null}
            </div>

            <form className={styles.editor} onSubmit={saveTag}>
              <label>
                {draft.id ? 'Tag name' : 'New tag name'}
                <input
                  value={draft.name}
                  onChange={(event) =>
                    setDraft((current) => ({
                      ...current,
                      name: event.target.value,
                    }))
                  }
                  maxLength={255}
                  required
                />
              </label>
              <label>
                Tag color
                <input
                  type="color"
                  value={draft.color}
                  onChange={(event) =>
                    setDraft((current) => ({
                      ...current,
                      color: event.target.value,
                    }))
                  }
                />
              </label>
              <button type="submit" disabled={pending}>
                {draft.id ? 'Save tag' : 'Create tag'}
              </button>
            </form>

            <ul className={styles.tagList}>
              {tags.map((tag) => (
                <li key={tag.id}>
                  <span>
                    <span
                      className={styles.swatch}
                      style={{ backgroundColor: tag.color ?? 'transparent' }}
                      aria-hidden="true"
                    />
                    {tag.name}
                  </span>
                  <span className={styles.actions}>
                    <button
                      type="button"
                      className={styles.secondary}
                      disabled={pending || editing}
                      onClick={() => {
                        setDraft({
                          id: tag.id,
                          name: tag.name,
                          color: tag.color ?? '#82d4bb',
                          version: tag.version,
                        });
                        setEditing(true);
                      }}
                      aria-label={`Edit ${tag.name}`}
                    >
                      Edit
                    </button>
                    <button
                      type="button"
                      className={styles.danger}
                      disabled={pending || editing}
                      onClick={() => void removeTag(tag)}
                      aria-label={`Delete ${tag.name}`}
                    >
                      Delete
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          </section>
        </div>
      ) : null}
    </section>
  );
}

function versionTag(version: number): EntityTag {
  return `"v${version}"`;
}

function isConflict(error: unknown): boolean {
  return error instanceof VistaraApiError && error.status === 412;
}

function createIdempotencyKey(): string {
  return globalThis.crypto?.randomUUID?.() ?? `web-${Date.now()}`;
}
