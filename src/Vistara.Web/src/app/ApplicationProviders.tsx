import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { RouterProvider, type RouterProviderProps } from 'react-router-dom';

interface ApplicationProvidersProps {
  queryClient: QueryClient;
  router: RouterProviderProps['router'];
}

export function ApplicationProviders({
  queryClient,
  router,
}: ApplicationProvidersProps) {
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}
