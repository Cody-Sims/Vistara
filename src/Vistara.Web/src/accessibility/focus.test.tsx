import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useRef, useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { captureFocusForRestore, useDialogFocusTrap } from './focus';

function DialogHarness() {
  const [open, setOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);

  useDialogFocusTrap({
    dialogRef,
    open,
    onDismiss: () => setOpen(false),
  });

  return (
    <>
      <button ref={triggerRef} onClick={() => setOpen(true)}>
        Open settings
      </button>
      <button>Background action</button>
      {open ? (
        <div
          ref={dialogRef}
          role="dialog"
          aria-modal="true"
          aria-label="Settings"
          tabIndex={-1}
        >
          <button>Save</button>
          <button>Cancel</button>
        </div>
      ) : null}
    </>
  );
}

describe('focus foundations', () => {
  it('captures and restores focus without retaining a detached element', () => {
    const first = document.createElement('button');
    const second = document.createElement('button');
    document.body.append(first, second);
    first.focus();

    const restore = captureFocusForRestore();
    second.focus();

    expect(restore()).toBe(true);
    expect(first).toHaveFocus();

    first.remove();
    expect(restore()).toBe(false);
    second.remove();
  });

  it('traps dialog focus, closes on Escape, and restores the trigger', async () => {
    const user = userEvent.setup();
    const onConsoleError = vi
      .spyOn(console, 'error')
      .mockImplementation(() => undefined);

    render(<DialogHarness />);
    const trigger = screen.getByRole('button', { name: 'Open settings' });
    await user.click(trigger);

    const save = screen.getByRole('button', { name: 'Save' });
    const cancel = screen.getByRole('button', { name: 'Cancel' });
    expect(save).toHaveFocus();

    cancel.focus();
    await user.tab();
    expect(save).toHaveFocus();

    await user.tab({ shift: true });
    expect(cancel).toHaveFocus();

    screen.getByRole('button', { name: 'Background action' }).focus();
    expect(save).toHaveFocus();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
    expect(onConsoleError).not.toHaveBeenCalled();
    onConsoleError.mockRestore();
  });
});
