import { createContext, useContext } from 'react';

export type AnnouncementPriority = 'polite' | 'assertive';

export interface AnnouncementOptions {
  priority?: AnnouncementPriority;
}

export type Announce = (
  message: string,
  options?: AnnouncementOptions,
) => void;

export const LiveAnnouncerContext = createContext<Announce | null>(null);

export function useLiveAnnouncer(): Announce {
  const announce = useContext(LiveAnnouncerContext);
  if (!announce) {
    throw new Error(
      'useLiveAnnouncer must be used within LiveAnnouncerProvider.',
    );
  }

  return announce;
}
