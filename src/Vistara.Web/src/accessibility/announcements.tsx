import {
  useCallback,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import {
  LiveAnnouncerContext,
  type Announce,
} from './liveAnnouncer';
import { VisuallyHidden } from './VisuallyHidden';

interface Announcement {
  id: number;
  message: string;
}

export function LiveAnnouncerProvider({ children }: { children: ReactNode }) {
  const [polite, setPolite] = useState<Announcement>({
    id: 0,
    message: '',
  });
  const [assertive, setAssertive] = useState<Announcement>({
    id: 0,
    message: '',
  });

  const announce = useCallback<Announce>((message, options) => {
    const update = (current: Announcement): Announcement => ({
      id: current.id + 1,
      message,
    });

    if (options?.priority === 'assertive') {
      setAssertive(update);
    } else {
      setPolite(update);
    }
  }, []);

  const contextValue = useMemo(() => announce, [announce]);

  return (
    <LiveAnnouncerContext.Provider value={contextValue}>
      {children}
      <VisuallyHidden
        role="status"
        aria-label="Notifications"
        aria-live="polite"
        aria-atomic="true"
      >
        <span key={polite.id}>{polite.message}</span>
      </VisuallyHidden>
      <VisuallyHidden
        role="alert"
        aria-label="Urgent notifications"
        aria-live="assertive"
        aria-atomic="true"
      >
        <span key={assertive.id}>{assertive.message}</span>
      </VisuallyHidden>
    </LiveAnnouncerContext.Provider>
  );
}

export interface LiveStatusProps {
  message: ReactNode;
  busy?: boolean;
  visuallyHidden?: boolean;
  label?: string;
}

export function LiveStatus({
  message,
  busy = false,
  visuallyHidden = false,
  label,
}: LiveStatusProps) {
  const props = {
    role: 'status',
    'aria-atomic': true,
    'aria-busy': busy,
    'aria-label': label,
    children: message,
  } as const;

  return visuallyHidden ? <VisuallyHidden {...props} /> : <span {...props} />;
}
