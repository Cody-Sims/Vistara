import { useCallback, useEffect, useRef, useState } from 'react';

export type ResourceState<T> =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly value: T; readonly etag?: string }
  | { readonly kind: 'failed'; readonly error: unknown };

interface Loaded<T> {
  readonly data: T;
  readonly etag?: string;
}

type Setter<T> = (state: ResourceState<T>) => void;

function runLoad<T>(
  load: () => Promise<Loaded<T>>,
  request: { current: number },
  apply: Setter<T>,
): Promise<void> {
  const id = ++request.current;

  return load().then(
    (response) => {
      if (request.current === id) {
        apply({
          kind: 'ready',
          value: response.data,
          ...(response.etag ? { etag: response.etag } : {}),
        });
      }
    },
    (error: unknown) => {
      if (request.current === id) {
        apply({ kind: 'failed', error });
      }
    },
  );
}

/**
 * Loads an administrative resource with a retry that repeats the same request.
 * Requests are ordered so a slow answer never replaces a newer one. Callers
 * pass a memoized loader; a new loader identity refetches.
 */
export function useAdminResource<T>(load: () => Promise<Loaded<T>>): {
  state: ResourceState<T>;
  reload: () => void;
  refresh: () => Promise<void>;
} {
  const [state, setState] = useState<ResourceState<T>>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const request = useRef(0);

  useEffect(() => {
    void runLoad(load, request, setState);
    return () => {
      request.current += 1;
    };
  }, [attempt, load]);

  const refresh = useCallback(
    () => runLoad(load, request, setState),
    [load],
  );

  return {
    state,
    reload: () => {
      setState({ kind: 'loading' });
      setAttempt((value) => value + 1);
    },
    refresh,
  };
}
