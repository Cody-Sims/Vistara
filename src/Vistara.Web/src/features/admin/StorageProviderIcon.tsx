import type { StorageProviderKind } from '../../api/platform';

interface ProviderIconProps {
  readonly provider: StorageProviderKind;
}

/**
 * Original marks in the same line style as the navigation icons: a stacked
 * disk for the filesystem, a blob container for Azure, and a bucket for
 * S3-compatible storage.
 */
export function StorageProviderIcon({ provider }: ProviderIconProps) {
  return (
    <svg
      aria-hidden="true"
      focusable="false"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth="1.7"
      width="24"
      height="24"
    >
      {provider === 'filesystem' ? (
        <>
          <rect x="3.5" y="4.5" width="17" height="6" rx="2" />
          <rect x="3.5" y="13.5" width="17" height="6" rx="2" />
          <path d="M7 7.5h.01M7 16.5h.01" />
        </>
      ) : null}
      {provider === 'azureBlob' ? (
        <>
          <path d="M7.5 17.5a4 4 0 0 1-.4-7.98 5.2 5.2 0 0 1 9.86-1.2A3.9 3.9 0 0 1 17.5 17.5Z" />
          <path d="M10 13.6h4.6" />
          <path d="m12.8 11.6 2 2-2 2" />
        </>
      ) : null}
      {provider === 's3' ? (
        <>
          <path d="M5 6.5h14l-1.6 12.2a1.6 1.6 0 0 1-1.6 1.3H8.2a1.6 1.6 0 0 1-1.6-1.3Z" />
          <path d="M9 6.5V5a1.5 1.5 0 0 1 1.5-1.5h3A1.5 1.5 0 0 1 15 5v1.5" />
          <path d="M9.5 11v5M14.5 11v5" />
        </>
      ) : null}
    </svg>
  );
}
