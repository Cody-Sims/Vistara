import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createAppQueryClient } from './api/queryClient';
import { ApplicationProviders } from './app/ApplicationProviders';
import { createAppRouter } from './app/router';
import './styles/tokens.css';
import './styles/base.css';

const rootElement = document.getElementById('root');

if (!rootElement) {
  throw new Error('Vistara could not find its root element.');
}

createRoot(rootElement).render(
  <StrictMode>
    <ApplicationProviders
      queryClient={createAppQueryClient()}
      router={createAppRouter()}
      sessionMode={import.meta.env.MODE === 'pages' ? 'preview' : 'live'}
    />
  </StrictMode>,
);
