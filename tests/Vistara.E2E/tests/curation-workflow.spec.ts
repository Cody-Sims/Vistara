import { readFile } from 'node:fs/promises';
import { expect, test } from '@playwright/test';
import { fixturePath } from '../support/paths.js';
import { signInWithPassword } from '../support/session.js';
import { readRuntimeState } from '../support/state.js';

const runtime = readRuntimeState();

/**
 * The curation walkthrough a person actually performs, in a real cookie
 * session. Nothing here supplies an API credential or an antiforgery header:
 * every mutation has to be accepted because the application sent it.
 */
test.describe('cookie session curation', () => {
  test('uploads, curates, trashes, and restores one image', {
    tag: '@smoke',
  }, async ({ browserName, page }) => {
    const seeded = runtime.browsers[browserName];
    if (!seeded) {
      throw new Error(`No E2E tenant was seeded for ${browserName}.`);
    }

    const mutations: { method: string; path: string; status: number }[] = [];
    const unauthenticated: string[] = [];
    page.on('response', (response) => {
      const request = response.request();
      const path = new URL(response.url()).pathname;
      if (!path.startsWith('/api/v1/') || request.method() === 'GET') {
        return;
      }

      mutations.push({
        method: request.method(),
        path,
        status: response.status(),
      });
      if (response.status() === 401 || response.status() === 403) {
        unauthenticated.push(`${request.method()} ${path}`);
      }
    });

    await signInWithPassword(page, runtime.baseUrl, seeded);
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();

    const title = `curate-${browserName}.png`;
    await page.goto(`${runtime.baseUrl}/uploads`);
    await page.getByLabel('Choose images to upload').setInputFiles({
      name: title,
      mimeType: 'image/png',
      buffer: Buffer.concat([
        await readFile(fixturePath),
        Buffer.from(`-curate-${browserName}`, 'utf8'),
      ]),
    });
    await expect(page.getByRole('heading', { name: title })).toBeVisible();
    await expect(
      page.getByRole('list', { name: 'Upload queue' }).locator('[data-phase="ready"]'),
    ).toBeVisible();

    /**
     * Reads the gallery the way the application does: the page's own cookie
     * session, with no header this test supplied.
     */
    const readRenditionCount = () =>
      page.evaluate(async (name: string) => {
        const response = await fetch(
          '/api/v1/assets?statuses=ready&limit=200',
          { credentials: 'same-origin' },
        );
        if (!response.ok) {
          return { status: response.status, renditions: 0 };
        }

        const body = (await response.json()) as {
          items: readonly {
            title: string;
            renditions: readonly unknown[];
          }[];
        };
        return {
          status: response.status,
          renditions:
            body.items.find((item) => item.title === name)?.renditions.length ??
            0,
        };
      }, title);

    // The cover an album takes comes from a delivered rendition, so the walk
    // waits for the pipeline before it organizes anything.
    await expect
      .poll(readRenditionCount, { timeout: 120_000 })
      .toMatchObject({ status: 200 });
    await expect
      .poll(async () => (await readRenditionCount()).renditions, {
        timeout: 120_000,
      })
      .toBeGreaterThan(0);

    const findInLibrary = async () => {
      await page.goto(`${runtime.baseUrl}/library`);
      await page.getByLabel('Search library').fill(title);
      await page.getByRole('button', { name: 'Apply search' }).click();
      await expect(
        page.getByRole('link', { name: new RegExp(title) }),
      ).toBeVisible();
    };

    await findInLibrary();
    await page.getByLabel(`Select ${title}`).click();
    const actions = page.getByRole('group', { name: 'Curation actions' });
    await expect(actions).toContainText('1 image selected');

    await actions.getByRole('button', { name: 'Favorite' }).click();
    await expect(actions.getByRole('status', { name: 'Curation result' })).toHaveText(
      'Added to favorites: 1 image.',
    );
    await expect(
      actions.getByRole('button', { name: 'Favorited' }),
    ).toHaveAttribute('aria-pressed', 'true');

    const tagName = `Curated ${browserName}`;
    await actions.getByRole('button', { name: 'Tags' }).click();
    const tagsPanel = actions.getByRole('group', { name: 'Tags' });
    await tagsPanel.getByLabel('New tag name').fill(tagName);
    await tagsPanel.getByRole('button', { name: 'Create and add' }).click();
    await expect(actions.getByRole('status', { name: 'Curation result' })).toHaveText(
      `Tagged ${tagName}: 1 image.`,
    );

    const albumName = `Curated album ${browserName}`;
    await actions.getByRole('button', { name: 'Albums' }).click();
    const albumsPanel = actions.getByRole('group', { name: 'Albums' });
    await albumsPanel.getByLabel('New album name').fill(albumName);
    await albumsPanel.getByRole('button', { name: 'Create and add' }).click();
    await expect(actions.getByRole('status', { name: 'Curation result' })).toHaveText(
      `Added to ${albumName}: 1 image.`,
    );

    // The album the image was added to now shows it, which is the cover an
    // empty album never had before.
    await page.goto(`${runtime.baseUrl}/albums`);
    const albumCard = page.locator('a', { hasText: albumName }).first();
    await expect(albumCard).toBeVisible();
    await expect(albumCard.locator('img')).toBeVisible();

    // A cover is only proof if the browser can actually fetch its bytes from
    // the same cookie session, so the delivered rendition is read back.
    const coverSource = await albumCard.locator('img').getAttribute('src');
    expect(coverSource).toBeTruthy();
    const cover = await page.evaluate(async (source: string) => {
      const response = await fetch(source, { credentials: 'same-origin' });
      return {
        status: response.status,
        bytes: (await response.arrayBuffer()).byteLength,
      };
    }, coverSource!);
    expect(cover.status).toBe(200);
    expect(cover.bytes).toBeGreaterThan(0);

    await findInLibrary();
    await page.getByLabel(`Select ${title}`).click();
    await actions.getByRole('button', { name: 'Move to trash' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await dialog.getByLabel('Reason (optional)').fill('curation walkthrough');
    await dialog.getByRole('button', { name: 'Move to trash' }).click();
    await expect(actions.getByRole('status', { name: 'Curation result' })).toHaveText(
      'Moved to trash: 1 image.',
    );

    await page.goto(`${runtime.baseUrl}/trash`);
    const trashRegion = page.getByRole('region', { name: 'Trash' });
    const trashed = page.getByRole('article', { name: title });
    await expect(trashed).toBeVisible();
    await trashed.getByRole('button', { name: 'Undo deletion' }).click();
    // The region announces its own loading through a live status of its
    // own, so the notice is asked for by what it says rather than by being
    // the region's only status.
    await expect(
      trashRegion.getByRole('status').filter({ hasText: 'Restore queued' }),
    ).toHaveText('Restore queued for 1 item.');

    // The restore is a job, so the library is read again until the image is
    // back where the walk started.
    await expect
      .poll(async () => (await readRenditionCount()).renditions, {
        timeout: 120_000,
      })
      .toBeGreaterThan(0);
    await findInLibrary();

    expect(unauthenticated).toEqual([]);
    const paths = mutations.map((call) => `${call.method} ${call.path}`);
    expect(paths.some((call) => call.includes('/favorite'))).toBe(true);
    expect(paths.some((call) => call.includes('/tags/'))).toBe(true);
    expect(paths.some((call) => call.includes('/items'))).toBe(true);
    expect(paths).toContain('POST /api/v1/assets/bulk');
    expect(paths).toContain('POST /api/v1/trash/restore');
    expect(
      mutations.filter((call) => call.status >= 400),
      JSON.stringify(mutations.filter((call) => call.status >= 400)),
    ).toEqual([]);
  });
});
