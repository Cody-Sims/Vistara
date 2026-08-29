import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import {
  LiveAnnouncerProvider,
  LiveStatus,
} from './announcements';
import { useLiveAnnouncer } from './liveAnnouncer';

function AnnouncementControls() {
  const announce = useLiveAnnouncer();

  return (
    <>
      <button onClick={() => announce('Album saved')}>Save album</button>
      <button
        onClick={() =>
          announce('Upload failed', {
            priority: 'assertive',
          })
        }
      >
        Fail upload
      </button>
    </>
  );
}

describe('announcement foundations', () => {
  it('routes polite and assertive messages to stable live regions', async () => {
    const user = userEvent.setup();
    render(
      <LiveAnnouncerProvider>
        <AnnouncementControls />
      </LiveAnnouncerProvider>,
    );

    const status = screen.getByRole('status', { name: 'Notifications' });
    const alert = screen.getByRole('alert', { name: 'Urgent notifications' });

    await user.click(screen.getByRole('button', { name: 'Save album' }));
    expect(status).toHaveTextContent('Album saved');

    await user.click(screen.getByRole('button', { name: 'Fail upload' }));
    expect(alert).toHaveTextContent('Upload failed');
  });

  it('exposes persistent busy status without making visual text mandatory', () => {
    render(<LiveStatus message="Processing 2 files" busy visuallyHidden />);

    const status = screen.getByRole('status');
    expect(status).toHaveAttribute('aria-busy', 'true');
    expect(status).toHaveTextContent('Processing 2 files');
  });
});
