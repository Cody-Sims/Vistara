import {
  useLayoutEffect,
  useRef,
  type RefObject,
} from 'react';

const focusableSelector = [
  'a[href]',
  'area[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  'summary',
  '[contenteditable="true"]',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

type FocusTarget = HTMLElement | SVGElement;

function canReceiveProgrammaticFocus(
  element: Element,
): element is FocusTarget {
  return (
    (element instanceof HTMLElement || element instanceof SVGElement) &&
    !element.closest('[hidden], [inert], [aria-hidden="true"]') &&
    !element.matches(':disabled')
  );
}

export function getFocusableElements(container: HTMLElement): FocusTarget[] {
  return Array.from(container.querySelectorAll(focusableSelector)).filter(
    (element): element is FocusTarget =>
      canReceiveProgrammaticFocus(element) && element.tabIndex >= 0,
  );
}

export function captureFocusForRestore(
  source?: Element | null,
): () => boolean {
  const captured =
    source ??
    (typeof document === 'undefined' ? null : document.activeElement);

  return () => {
    if (
      !captured?.isConnected ||
      !canReceiveProgrammaticFocus(captured)
    ) {
      return false;
    }

    captured.focus();
    return captured.ownerDocument.activeElement === captured;
  };
}

export interface DialogFocusTrapOptions {
  dialogRef: RefObject<HTMLElement | null>;
  open: boolean;
  onDismiss: () => void;
  initialFocusRef?: RefObject<HTMLElement | null>;
  restoreFocus?: boolean;
}

export function useDialogFocusTrap({
  dialogRef,
  open,
  onDismiss,
  initialFocusRef,
  restoreFocus = true,
}: DialogFocusTrapOptions): void {
  const onDismissRef = useRef(onDismiss);

  useLayoutEffect(() => {
    onDismissRef.current = onDismiss;
  }, [onDismiss]);

  useLayoutEffect(() => {
    const dialog = dialogRef.current;
    if (!open || !dialog) {
      return;
    }

    const ownerDocument = dialog.ownerDocument;
    const restore = captureFocusForRestore(ownerDocument.activeElement);
    const initialTarget =
      initialFocusRef?.current ?? getFocusableElements(dialog)[0] ?? dialog;
    initialTarget.focus();

    const focusFirst = () => {
      (getFocusableElements(dialog)[0] ?? dialog).focus();
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        onDismissRef.current();
        return;
      }

      if (event.key !== 'Tab') {
        return;
      }

      const focusable = getFocusableElements(dialog);
      if (focusable.length === 0) {
        event.preventDefault();
        dialog.focus();
        return;
      }

      const first = focusable[0];
      const last = focusable.at(-1);
      const active = ownerDocument.activeElement;

      if (
        event.shiftKey &&
        (active === first || !dialog.contains(active))
      ) {
        event.preventDefault();
        last?.focus();
      } else if (
        !event.shiftKey &&
        (active === last || !dialog.contains(active))
      ) {
        event.preventDefault();
        first?.focus();
      }
    };

    const handleFocusIn = (event: FocusEvent) => {
      if (
        event.target instanceof Node &&
        !dialog.contains(event.target)
      ) {
        focusFirst();
      }
    };

    ownerDocument.addEventListener('keydown', handleKeyDown, true);
    ownerDocument.addEventListener('focusin', handleFocusIn, true);
    return () => {
      ownerDocument.removeEventListener('keydown', handleKeyDown, true);
      ownerDocument.removeEventListener('focusin', handleFocusIn, true);
      if (restoreFocus) {
        restore();
      }
    };
  }, [dialogRef, initialFocusRef, open, restoreFocus]);
}
