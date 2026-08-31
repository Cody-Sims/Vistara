import { afterEach, describe, expect, it, vi } from 'vitest';
import { registerServiceWorker } from './registerServiceWorker';

const original = Object.getOwnPropertyDescriptor(
  window.navigator,
  'serviceWorker',
);

function stubContainer(register = vi.fn(async () => ({}) as never)) {
  Object.defineProperty(window.navigator, 'serviceWorker', {
    configurable: true,
    value: { register },
  });
  return register;
}

afterEach(() => {
  if (original) {
    Object.defineProperty(window.navigator, 'serviceWorker', original);
  } else {
    Reflect.deleteProperty(window.navigator, 'serviceWorker');
  }
});

describe('service worker registration', () => {
  it('registers the worker for the configured base after load', async () => {
    const register = stubContainer();

    registerServiceWorker('/Vistara/', true);
    window.dispatchEvent(new Event('load'));

    expect(register).toHaveBeenCalledWith('/Vistara/sw.js', {
      scope: '/Vistara/',
    });
  });

  it('does nothing in a development build', () => {
    const register = stubContainer();

    registerServiceWorker('/', false);
    window.dispatchEvent(new Event('load'));

    expect(register).not.toHaveBeenCalled();
  });

  it('never fails the application when registration is rejected', () => {
    const register = stubContainer(
      vi.fn(async () => {
        throw new Error('unsupported');
      }),
    );

    expect(() => {
      registerServiceWorker('/', true);
      window.dispatchEvent(new Event('load'));
    }).not.toThrow();
    expect(register).toHaveBeenCalled();
  });

  it('is inert where service workers are unavailable', () => {
    Reflect.deleteProperty(window.navigator, 'serviceWorker');

    expect(() => registerServiceWorker('/', true)).not.toThrow();
  });
});
