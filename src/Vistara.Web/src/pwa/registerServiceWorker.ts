/**
 * Registers the generated service worker. It only precaches the versioned
 * shell and brand assets of the deployed build, so an installed Vistara opens
 * its interface without the network; images and API responses still require a
 * live connection.
 */
export function registerServiceWorker(
  base = import.meta.env.BASE_URL,
  enabled = import.meta.env.PROD,
): void {
  if (!enabled || typeof navigator === 'undefined') {
    return;
  }

  const container = navigator.serviceWorker as
    | ServiceWorkerContainer
    | undefined;
  if (!container) {
    return;
  }

  window.addEventListener('load', () => {
    void container.register(`${base}sw.js`, { scope: base }).catch(() => {
      // An unavailable service worker never blocks the application.
    });
  });
}
