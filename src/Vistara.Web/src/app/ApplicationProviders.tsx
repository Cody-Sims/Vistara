import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { RouterProvider, type RouterProviderProps } from 'react-router-dom';
import { SessionProvider, type SessionClient } from '../features/session';
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
  return (
    <QueryClientProvider client={queryClient}>
      <SessionProvider client={sessionClient} mode={sessionMode}>
        <RouterProvider router={router} />
      </SessionProvider>
    </QueryClientProvider>
  );
}
