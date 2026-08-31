import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createAppRouter } from './router';

const themeStorageKey = 'vistara:theme';

afterEach(() => {
  localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
  document.documentElement.removeAttribute('data-theme-preference');
  document.documentElement.style.removeProperty('color-scheme');
  vi.unstubAllGlobals();
});

describe('shell theme', () => {
  it('persists an explicit theme and applies it to the document', async () => {
    const user = userEvent.setup();
    const router = createAppRouter({ initialEntries: ['/library'] });

    render(<RouterProvider router={router} />);

    await user.selectOptions(
      screen.getByRole('combobox', { name: 'Theme' }),
      'light',
    );

    expect(localStorage.getItem(themeStorageKey)).toBe('light');
    expect(document.documentElement).toHaveAttribute('data-theme', 'light');
    expect(document.documentElement).toHaveAttribute(
      'data-theme-preference',
      'light',
    );
  });

  it('tracks operating-system changes while the system theme is selected', async () => {
    const media = createMediaQuery(false);
    vi.stubGlobal('matchMedia', () => media);
    localStorage.setItem(themeStorageKey, 'system');
    const router = createAppRouter({ initialEntries: ['/library'] });

    render(<RouterProvider router={router} />);

    expect(document.documentElement).toHaveAttribute('data-theme', 'light');

    media.matches = true;
    media.dispatchEvent(new Event('change'));

    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
    expect(localStorage.getItem(themeStorageKey)).toBe('system');
  });

  it('initializes the saved theme before the application module loads', () => {
    const html = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8');
    const initializer = html.indexOf(themeStorageKey);
    const applicationModule = html.indexOf('/src/main.tsx');

    expect(initializer).toBeGreaterThan(-1);
    expect(initializer).toBeLessThan(applicationModule);
    expect(html).toContain('root.dataset.theme');
    expect(html).toContain('prefers-color-scheme: dark');
  });
});

describe('shell connectivity', () => {
  it('announces connectivity changes without claiming synchronization state', async () => {
    const router = createAppRouter({ initialEntries: ['/library'] });

    render(<RouterProvider router={router} />);

    expect(
      screen.getByRole('status', { name: 'Connection status' }),
    ).toHaveTextContent('Online');

    await act(async () => {
      window.dispatchEvent(new Event('offline'));
    });

    expect(
      screen.getByRole('status', { name: 'Connection status' }),
    ).toHaveTextContent('Offline');

    await act(async () => {
      window.dispatchEvent(new Event('online'));
    });

    expect(
      screen.getByRole('status', { name: 'Connection status' }),
    ).toHaveTextContent('Online');
  });
});

function createMediaQuery(initialMatches: boolean) {
  const target = new EventTarget();
  return Object.assign(target, {
    matches: initialMatches,
    media: '(prefers-color-scheme: dark)',
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: target.addEventListener.bind(target),
    removeEventListener: target.removeEventListener.bind(target),
    dispatchEvent: target.dispatchEvent.bind(target),
  }) as MediaQueryList & { matches: boolean };
}
