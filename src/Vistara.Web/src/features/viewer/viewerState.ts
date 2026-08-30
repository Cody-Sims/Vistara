interface ViewerLocationState {
  libraryAddress?: unknown;
}

export function getViewerReturnAddress(state: unknown) {
  const address = (state as ViewerLocationState | null)?.libraryAddress;
  return typeof address === 'string' &&
    /^\/library(?:[/?#]|$)/u.test(address) &&
    !address.startsWith('//')
    ? address
    : '/library';
}

export function captureFocusRestorer() {
  const focused = document.activeElement;
  return () => {
    if (focused instanceof HTMLElement && focused.isConnected) {
      focused.focus({ preventScroll: true });
    }
  };
}
