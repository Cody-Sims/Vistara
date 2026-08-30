import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated';
import type {
  CreatedShare,
  PublicShare,
  ShareSummary,
} from '../../api/generated/models';
import { PublicShareView } from './PublicShareView';
import { ShareManager } from './ShareManager';

const NOW = new Date('2026-08-29T17:00:00.000Z');

function share(overrides: Partial<ShareSummary> = {}): ShareSummary {
  return {
    id: 'share-1',
    name: 'Family album',
    status: 'active',
    target: { kind: 'album', albumId: 'album-1', assetCount: 12 },
    permissions: {
      view: true,
      downloadRenditions: true,
      downloadOriginal: false,
    },
    metadataExposure: 'none',
    passwordProtected: false,
    createdAt: '2026-08-20T12:00:00.000Z',
    version: 4,
    ...overrides,
  };
}

function createdShare(publicToken = 'secret-bearer-token'): CreatedShare {
  return {
    share: { share: share(), snapshotAssets: [] },
    publicToken,
  };
}

describe('managed shares', () => {
  it('shows policy before creation and forgets the one-time token after copying', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    const client = {
      listShares: vi.fn().mockResolvedValue({ data: { items: [] } }),
      createShare: vi.fn().mockResolvedValue({ data: createdShare() }),
      revokeShare: vi.fn(),
    };

    render(
      <ShareManager
        client={client}
        target={{ kind: 'album', albumId: 'album-1' }}
        now={() => NOW}
        createIdempotencyKey={() => 'create-share-key'}
        buildPublicUrl={(token) => `https://photos.test/s/${token}`}
      />,
    );

    await screen.findByText('No share links yet.');
    await user.click(screen.getByRole('button', { name: 'Create share link' }));
    await user.type(screen.getByLabelText('Link name'), 'Summer trip');
    await user.click(screen.getByLabelText('Allow original downloads'));
    await user.click(screen.getByLabelText('Expose basic metadata'));
    await user.type(
      screen.getByLabelText('Password (optional)'),
      'correct horse battery staple',
    );

    const policy = screen.getByRole('note', { name: 'Share policy summary' });
    expect(policy).toHaveTextContent('Original downloads: allowed');
    expect(policy).toHaveTextContent('Basic metadata: exposed');
    expect(policy).toHaveTextContent('Password: required');

    await user.click(screen.getByRole('button', { name: 'Create link' }));

    expect(client.createShare).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'Summer trip',
        targetKind: 'album',
        albumId: 'album-1',
        metadataExposure: 'basic',
        password: 'correct horse battery staple',
        permissions: {
          view: true,
          downloadRenditions: true,
          downloadOriginal: true,
        },
      }),
      { idempotencyKey: 'create-share-key' },
    );
    expect(
      await screen.findByDisplayValue(
        'https://photos.test/s/secret-bearer-token',
      ),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Copy share link' }));

    expect(writeText).toHaveBeenCalledWith(
      'https://photos.test/s/secret-bearer-token',
    );
    expect(
      screen.queryByText(/secret-bearer-token/),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent(
      'Link copied. Vistara no longer displays this token.',
    );
  });

  it('labels active, expired, and revoked links and recovers from an ETag conflict', async () => {
    const user = userEvent.setup();
    const summaries = [
      share(),
      share({
        id: 'expired',
        name: 'Expired link',
        status: 'expired',
        expiresAt: '2026-08-01T00:00:00.000Z',
      }),
      share({
        id: 'revoked',
        name: 'Revoked link',
        status: 'revoked',
        revokedAt: '2026-08-22T00:00:00.000Z',
      }),
    ];
    const client = {
      listShares: vi
        .fn()
        .mockResolvedValue({ data: { items: summaries } }),
      createShare: vi.fn(),
      revokeShare: vi.fn().mockRejectedValue(
        new VistaraApiError(412, {
          type: 'about:blank',
          title: 'Precondition failed',
          status: 412,
          code: 'etag_mismatch',
          errors: {},
        }),
      ),
    };

    render(
      <ShareManager
        client={client}
        target={{ kind: 'album', albumId: 'album-1' }}
        now={() => NOW}
        createIdempotencyKey={() => 'revoke-key'}
      />,
    );

    expect(await screen.findByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Expired')).toBeInTheDocument();
    expect(screen.getByText('Revoked')).toBeInTheDocument();

    const activeCard = screen.getByRole('article', { name: 'Family album' });
    await user.click(within(activeCard).getByRole('button', { name: 'Revoke' }));
    const dialog = screen.getByRole('dialog', { name: 'Revoke this link?' });
    expect(
      within(dialog).getByRole('button', { name: 'Confirm revocation' }),
    ).toHaveFocus();
    await user.tab({ shift: true });
    expect(within(dialog).getByRole('button', { name: 'Keep link' })).toHaveFocus();
    await user.click(
      screen.getByRole('button', { name: 'Confirm revocation' }),
    );

    expect(client.revokeShare).toHaveBeenCalledWith('share-1', {
      idempotencyKey: 'revoke-key',
      ifMatch: '"v4"',
    });
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'This link changed elsewhere. The latest version has been loaded.',
    );
    expect(client.listShares).toHaveBeenCalledTimes(2);
  });
});

describe('public shares', () => {
  it('challenges for a password before revealing permitted assets', async () => {
    const user = userEvent.setup();
    const locked: PublicShare = {
      name: 'Protected gallery',
      status: 'active',
      permissions: {
        view: true,
        downloadRenditions: true,
        downloadOriginal: false,
      },
      metadataExposure: 'basic',
      passwordRequired: true,
      expiresAt: '2026-09-01T00:00:00.000Z',
    };
    const unlocked: PublicShare = {
      ...locked,
      passwordRequired: false,
      assets: {
        items: [
          {
            id: 'asset-1',
            title: 'Mountain',
            description: 'Sunrise',
            capturedAt: '2026-07-01T12:00:00.000Z',
            width: 1200,
            height: 800,
            renditions: [
              {
                kind: 'thumbnail',
                path: '/media/share-480.webp',
                width: 480,
                height: 320,
                contentType: 'image/webp',
              },
              {
                kind: 'display',
                path: '/media/share-1200.webp',
                width: 1200,
                height: 800,
                contentType: 'image/webp',
              },
            ],
          },
        ],
      },
    };
    const client = {
      getPublicShare: vi
        .fn()
        .mockResolvedValueOnce({ data: locked })
        .mockResolvedValueOnce({ data: unlocked }),
      challengePublicShare: vi.fn().mockResolvedValue({
        data: {
          authenticated: true,
          expiresAt: '2026-08-29T17:15:00.000Z',
        },
      }),
    };

    render(
      <PublicShareView client={client} token="private-token" now={() => NOW} />,
    );

    expect(await screen.findByText('Password required')).toBeInTheDocument();
    expect(screen.queryByText('Mountain')).not.toBeInTheDocument();
    expect(screen.getByText('Original downloads are disabled.')).toBeInTheDocument();
    expect(screen.getByText('Basic metadata is visible.')).toBeInTheDocument();
    expect(screen.queryByText('private-token')).not.toBeInTheDocument();

    await user.type(screen.getByLabelText('Share password'), 'let me in');
    await user.click(screen.getByRole('button', { name: 'Unlock gallery' }));

    expect(client.challengePublicShare).toHaveBeenCalledWith('private-token', {
      password: 'let me in',
    });
    expect(await screen.findByText('Mountain')).toBeInTheDocument();
    expect(screen.getByText('Sunrise')).toBeInTheDocument();
  });

  it.each(['expired', 'revoked'] as const)(
    'renders a non-disclosing %s state',
    async (status) => {
      const client = {
        getPublicShare: vi.fn().mockResolvedValue({
          data: {
            name: 'Unavailable gallery',
            status,
            permissions: {
              view: true,
              downloadRenditions: false,
              downloadOriginal: false,
            },
            metadataExposure: 'none',
            passwordRequired: false,
          } satisfies PublicShare,
        }),
        challengePublicShare: vi.fn(),
      };

      render(<PublicShareView client={client} token="never-render-me" />);

      expect(
        await screen.findByRole('heading', { name: 'Share unavailable' }),
      ).toBeInTheDocument();
      expect(screen.getByText(new RegExp(status, 'i'))).toBeInTheDocument();
      expect(screen.queryByText('never-render-me')).not.toBeInTheDocument();
    },
  );

  it('does not render optional metadata when the policy hides it', async () => {
    const client = {
      getPublicShare: vi.fn().mockResolvedValue({
        data: {
          name: 'Minimal gallery',
          status: 'active',
          permissions: {
            view: true,
            downloadRenditions: false,
            downloadOriginal: false,
          },
          metadataExposure: 'none',
          passwordRequired: false,
          assets: {
            items: [
              {
                id: 'asset-1',
                title: 'Mountain',
                description: 'Private location details',
                capturedAt: '2026-07-01T12:00:00.000Z',
                width: 1200,
                height: 800,
                renditions: [],
              },
            ],
          },
        } satisfies PublicShare,
      }),
      challengePublicShare: vi.fn(),
    };

    render(<PublicShareView client={client} token="policy-token" />);

    expect(await screen.findByText('Mountain')).toBeInTheDocument();
    expect(screen.queryByText('Private location details')).not.toBeInTheDocument();
    expect(screen.queryByText('Dimensions')).not.toBeInTheDocument();
    expect(screen.queryByText('Captured')).not.toBeInTheDocument();
  });

  it('serves shared images responsively without leaking provider URLs', async () => {
    const share: PublicShare = {
      name: 'Open gallery',
      status: 'active',
      permissions: {
        view: true,
        downloadRenditions: true,
        downloadOriginal: false,
      },
      metadataExposure: 'basic',
      passwordRequired: false,
      assets: {
        items: [
          {
            id: 'asset-1',
            title: 'Mountain',
            description: 'Sunrise over the ridge',
            width: 1200,
            height: 800,
            renditions: [
              {
                kind: 'thumbnail',
                path: '/media/share-480.webp',
                width: 480,
                height: 320,
                contentType: 'image/webp',
              },
              {
                kind: 'display',
                path: '/media/share-1200.webp',
                width: 1200,
                height: 800,
                contentType: 'image/webp',
              },
            ],
          },
        ],
      },
    };
    const client = {
      getPublicShare: vi.fn().mockResolvedValue({ data: share }),
      challengePublicShare: vi.fn(),
    };

    render(<PublicShareView client={client} token="open-token" />);

    const image = await screen.findByAltText('Sunrise over the ridge');
    expect(image).toHaveAttribute(
      'srcset',
      '/media/share-480.webp 480w, /media/share-1200.webp 1200w',
    );
    expect(image).toHaveAttribute(
      'sizes',
      '(max-width: 40rem) 100vw, (max-width: 70rem) 50vw, 33vw',
    );
    expect(image).toHaveAttribute('fetchpriority', 'high');
    expect(image.getAttribute('src')).toMatch(/^\/media\//);
  });
});
