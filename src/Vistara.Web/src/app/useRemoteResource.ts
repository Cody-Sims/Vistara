import { useCallback, useEffect, useRef, useState } from 'react';

export type RemoteState<T> =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly value: T }
  | { readonly kind: 'failed'; readonly error: unknown };

function runLoad<T>(
  load: () => Promise<T>,
  request: { current: number },
  apply: (state: RemoteState<T>) => void,
): Promise<void> {
  const id = ++request.current;

  return load().then(
    (value) => {
      if (request.current === id) {
        apply({ kind: 'ready', value });
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
 * Reads a resource once per loader identity, with a retry that repeats the
 * same request. Requests are ordered so a slow answer never replaces a newer
 * one. Callers pass a memoized loader; a new identity refetches.
 */
export function useRemoteResource<T>(load: () => Promise<T>): {
  state: RemoteState<T>;
  reload: () => void;
  refresh: () => Promise<void>;
} {
  const [state, setState] = useState<RemoteState<T>>({ kind: 'loading' });
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
