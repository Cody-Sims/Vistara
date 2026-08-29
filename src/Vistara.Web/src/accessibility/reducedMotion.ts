import { useEffect, useState } from 'react';

export const reducedMotionQuery = '(prefers-reduced-motion: reduce)';

function readReducedMotionPreference(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia(reducedMotionQuery).matches
  );
}

export function useReducedMotion(): boolean {
  const [reducedMotion, setReducedMotion] = useState(
    readReducedMotionPreference,
  );

  useEffect(() => {
    if (
      typeof window === 'undefined' ||
      typeof window.matchMedia !== 'function'
    ) {
      return;
    }

    const query = window.matchMedia(reducedMotionQuery);
    const updatePreference = (event: MediaQueryListEvent) => {
      setReducedMotion(event.matches);
    };
    query.addEventListener('change', updatePreference);
    return () => query.removeEventListener('change', updatePreference);
  }, []);

  return reducedMotion;
}

export function getMotionDuration(
  duration: number,
  reducedMotion: boolean,
  reducedDuration = 0,
): number {
  return reducedMotion ? reducedDuration : duration;
}
