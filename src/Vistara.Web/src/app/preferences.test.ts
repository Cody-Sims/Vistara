import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import {
  applyDocumentPreferences,
  defaultPreferences,
  getPreferences,
  resetPreferences,
  setPreferences,
  useAppPreferences,
} from './preferences';

const baseCss = readFileSync(
  resolve(process.cwd(), 'src/styles/base.css'),
  'utf8',
);
const tokens = readFileSync(
  resolve(process.cwd(), 'src/styles/tokens.css'),
  'utf8',
);

afterEach(() => {
  resetPreferences();
  localStorage.clear();
  for (const attribute of ['density', 'reducedMotion', 'pagedMode']) {
    delete document.documentElement.dataset[attribute];
  }
});

describe('device preferences', () => {
  it('starts comfortable, animated, and continuous', () => {
    expect(getPreferences()).toEqual(defaultPreferences);
  });

  it('describes the active choices on the document element', () => {
    setPreferences({ density: 'compact', reducedMotion: true });
    applyDocumentPreferences();

    expect(document.documentElement.dataset.density).toBe('compact');
    expect(document.documentElement.dataset.reducedMotion).toBe('true');
    expect(document.documentElement.dataset.pagedMode).toBe('false');
  });

  it('remembers the choices across a reload', () => {
    setPreferences({ screenReaderPagedMode: true });
    expect(localStorage.getItem('vistara:preferences')).toContain(
      'screenReaderPagedMode',
    );

    // A reload starts from an empty module state and reads storage again.
    const stored = localStorage.getItem('vistara:preferences')!;
    resetPreferences();
    localStorage.setItem('vistara:preferences', stored);

    expect(getPreferences().screenReaderPagedMode).toBe(true);
  });

  it('ignores stored values it does not recognise', () => {
    localStorage.setItem(
      'vistara:preferences',
      JSON.stringify({ density: 'enormous', reducedMotion: 'yes' }),
    );

    expect(getPreferences()).toEqual(defaultPreferences);
  });

  it('publishes changes to every subscriber', () => {
    const { result } = renderHook(() => useAppPreferences());

    expect(result.current.density).toBe('comfortable');

    act(() => setPreferences({ density: 'compact' }));

    expect(result.current.density).toBe('compact');
    expect(document.documentElement.dataset.density).toBe('compact');
  });
});

describe('preference styling', () => {
  it('tightens the spacing scale for compact density', () => {
    expect(tokens).toContain(':root[data-density="compact"]');
    expect(tokens).toMatch(/\[data-density="compact"\][\s\S]*--space-4:/);
  });

  it('keeps the minimum touch target at compact density', () => {
    const compact = tokens.slice(tokens.indexOf(':root[data-density="compact"]'));
    const block = compact.slice(0, compact.indexOf('}'));

    expect(block).not.toContain('--target-min');
  });

  it('stops non-essential motion when reduced motion is chosen', () => {
    expect(baseCss).toContain(':root[data-reduced-motion="true"]');
    expect(baseCss).toMatch(
      /\[data-reduced-motion="true"\][\s\S]*transition-duration: 0\.01ms !important/,
    );
  });
});
