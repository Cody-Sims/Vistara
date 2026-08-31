import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import { RouterProvider, type RouterProviderProps } from 'react-router-dom';
// Imported from the module rather than the feature barrel, so the entry does
// not pull the administration screens in behind it.
import { clearStorageDraft } from '../features/admin/storageDraft';
import {
  clearAccountScopedData,
  SessionProvider,
  type SessionClient,
} from '../features/session';
import { platformClient } from './apiClients';

interface ApplicationProvidersProps {
  queryClient: QueryClient;
  router: RouterProviderProps['router'];
  sessionClient?: SessionClient;
  sessionMode?: 'live' | 'preview';
}

export function ApplicationProviders({
  queryClient,
  router,
  sessionClient = platformClient,
  sessionMode = 'live',
}: ApplicationProvidersProps) {
  // Signing out must leave nothing of the previous account behind: the shared
  // query cache, gallery session storage, and resumable upload database all go.
  const onSessionEnd = useCallback(
    () =>
      clearAccountScopedData({
        queryCache: queryClient,
        inMemory: [clearStorageDraft],
      }),
    [queryClient],
  );

  return (
    <QueryClientProvider client={queryClient}>
      <SessionProvider
        client={sessionClient}
        mode={sessionMode}
        onSessionEnd={onSessionEnd}
      >
        <RouterProvider router={router} />
      </SessionProvider>
    </QueryClientProvider>
  );
}
