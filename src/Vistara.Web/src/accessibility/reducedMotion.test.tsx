import { act, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { getMotionDuration, useReducedMotion } from './reducedMotion';

interface MatchMediaController {
  setMatches(matches: boolean): void;
}

function installMatchMedia(initialMatches: boolean): MatchMediaController {
  let matches = initialMatches;
  const listeners = new Set<(event: MediaQueryListEvent) => void>();

  vi.stubGlobal(
    'matchMedia',
    vi.fn(() => ({
      get matches() {
        return matches;
      },
      media: '(prefers-reduced-motion: reduce)',
      onchange: null,
      addEventListener: (
        _type: string,
        listener: (event: MediaQueryListEvent) => void,
      ) => listeners.add(listener),
      removeEventListener: (
        _type: string,
        listener: (event: MediaQueryListEvent) => void,
      ) => listeners.delete(listener),
      addListener: () => undefined,
      removeListener: () => undefined,
      dispatchEvent: () => true,
    })),
  );

  return {
    setMatches(nextMatches) {
      matches = nextMatches;
      const event = { matches } as MediaQueryListEvent;
      listeners.forEach((listener) => listener(event));
    },
  };
}

function Preference() {
  return <output>{useReducedMotion() ? 'reduced' : 'full'}</output>;
}

describe('reduced motion foundations', () => {
  beforeEach(() => {
    vi.unstubAllGlobals();
  });

  it('tracks operating-system motion preference changes', () => {
    const media = installMatchMedia(false);
    render(<Preference />);
    expect(screen.getByText('full')).toBeInTheDocument();

    act(() => media.setMatches(true));
    expect(screen.getByText('reduced')).toBeInTheDocument();
  });

  it('selects a zero-duration alternative when motion is reduced', () => {
    expect(getMotionDuration(240, true)).toBe(0);
    expect(getMotionDuration(240, false)).toBe(240);
  });
});
