import { readFile } from 'node:fs/promises';
import { expect, test } from '@playwright/test';
import { fixturePath } from '../support/paths.js';
import { readRuntimeState } from '../support/state.js';

const runtime = readRuntimeState();

test.describe('integrated gallery workflows', () => {
  test('uploads, browses, organizes, shares, restores, and preserves navigation state', async ({
    browser,
    browserName,
    page,
    request,
  }) => {
    const seeded = runtime.browsers[browserName];
    if (!seeded) {
      throw new Error(`No E2E tenant was seeded for ${browserName}.`);
    }

    await page.setExtraHTTPHeaders({ 'X-API-Key': seeded.apiKey });

    const reserved = await request.get(`${runtime.baseUrl}/openapi/missing`);
    expect(reserved.status()).toBe(404);
    expect(await reserved.text()).not.toContain('<div id="root">');
    const missingApi = await request.get(`${runtime.baseUrl}/api/v1/missing`);
    expect(missingApi.status()).toBe(404);
    expect(await missingApi.text()).not.toContain('<div id="root">');
    const unauthenticatedAssets = await request.get(
      `${runtime.baseUrl}/api/v1/assets`,
    );
    expect(unauthenticatedAssets.status()).toBe(401);
    const statusFilter = await request.get(
      `${runtime.baseUrl}/api/v1/assets?statuses=ready`,
      { headers: { 'X-API-Key': seeded.apiKey } },
    );
    expect(statusFilter.status()).toBe(200);

    await page.goto(`${runtime.baseUrl}/missing-gallery-route`);
    await expect(
      page.getByRole('heading', { name: 'Page not found' }),
    ).toBeFocused();

    await page.goto(`${runtime.baseUrl}/uploads`);
    await expect(
      page.getByRole('heading', { name: 'Upload images' }),
    ).toBeVisible();
    await page.getByLabel('Choose images to upload').setInputFiles({
      name: `tiny-${browserName}.png`,
      mimeType: 'image/png',
      buffer: Buffer.concat([
        await readFile(fixturePath),
        Buffer.from(`-${browserName}`, 'utf8'),
      ]),
    });
    await expect(
      page.getByRole('heading', { name: `tiny-${browserName}.png` }),
    ).toBeVisible();
    await expect(
      page
        .getByRole('list', { name: 'Upload queue' })
        .locator('[data-phase="ready"]'),
    ).toBeVisible();

    await page.goto(`${runtime.baseUrl}/library`);
    const primaryTitle = `Mountain ${browserName}`;
    await page.getByLabel('Search library').fill('Mountain');
    await page.getByRole('button', { name: 'Apply search' }).click();
    await expect(page).toHaveURL(/q=Mountain/);
    await expect(page.getByRole('link', { name: new RegExp(primaryTitle) })).toBeVisible();
    await expect(
      page.getByRole('link', { name: /Gallery item/ }),
    ).toHaveCount(0);
    await page.getByLabel('Search library').fill('');
    await page.getByRole('button', { name: 'Apply search' }).click();
    await expect(page).not.toHaveURL(/q=/);

    const scroller = page.getByRole('region', { name: 'Library timeline' });
    await scroller.evaluate((element) => {
      element.scrollTop = 600;
      element.dispatchEvent(new Event('scroll'));
    });
    await expect.poll(() => scroller.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
    const assetLink = page.getByRole('link', { name: /Gallery item/ }).last();
    await expect(assetLink).toBeVisible();
    await assetLink.evaluate((element) =>
      (element as HTMLElement).focus({ preventScroll: true }),
    );
    const focusedAssetId = await assetLink.getAttribute('data-asset-link');
    const focusedTitle = (await assetLink.locator('strong').textContent())?.trim();
    expect(focusedAssetId).toBeTruthy();
    expect(focusedTitle).toBeTruthy();
    await expect.poll(() => scroller.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
    await expect
      .poll(() =>
        page.evaluate(() => {
          const value = sessionStorage.getItem(
            'vistara:route-restoration:/library',
          );
          return value ? (JSON.parse(value) as { scrollTop: number }).scrollTop : 0;
        }),
      )
      .toBeGreaterThan(0);
    await assetLink.evaluate((element) => (element as HTMLElement).click());
    const viewerHeading = page.getByRole('heading', { level: 1, name: focusedTitle! });
    await expect(viewerHeading).toBeFocused();
    await page.goBack();
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();
    await expect(page.locator(`[data-asset-link="${focusedAssetId}"]`)).toBeFocused();
    await expect
      .poll(() =>
        page.evaluate(() => {
          const value = sessionStorage.getItem(
            'vistara:route-restoration:/library',
          );
          return value ? (JSON.parse(value) as { scrollTop: number }).scrollTop : 0;
        }),
      )
      .toBeGreaterThan(0);
    await expect.poll(() => scroller.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);

    await page.getByRole('link', { name: 'Albums' }).click();
    const albumName = `Summer ${browserName}`;
    await page.getByLabel('Album name').fill(albumName);
    await page.getByRole('button', { name: 'Create album' }).click();
    await expect(page.getByRole('status')).toContainText(`${albumName} was created`);
    await expect(page.getByText(albumName, { exact: true })).toBeVisible();

    await page.getByRole('link', { name: 'Tags' }).click();
    const tagName = `Coast ${browserName}`;
    await page.getByLabel('New tag name').fill(tagName);
    await page.getByRole('button', { name: 'Create tag' }).click();
    await expect(page.getByRole('status')).toContainText(`${tagName} was created`);

    await page.getByRole('link', { name: 'Favorites' }).click();
    await expect(page.getByText(primaryTitle, { exact: true })).toBeVisible();
    await page
      .getByRole('button', { name: `Remove ${primaryTitle} from favorites` })
      .click();
    await expect(page.getByText(primaryTitle, { exact: true })).toHaveCount(0);

    await page.goto(
      `${runtime.baseUrl}/shared/links?assetId=${seeded.primaryAssetId}&version=1`,
    );
    const shareName = `E2E link ${browserName}`;
    await page.getByRole('button', { name: 'Create share link' }).click();
    await page.getByLabel('Link name').fill(shareName);
    await page.getByRole('button', { name: 'Create link' }).click();
    const shareUrl = await page.getByLabel('New share link').inputValue();
    await expect(page.getByRole('article', { name: shareName })).toBeVisible();

    const publicContext = await browser.newContext();
    const publicPage = await publicContext.newPage();
    await publicPage.goto(shareUrl);
    await expect(publicPage.getByRole('heading', { name: shareName })).toBeVisible();
    await expect(publicPage.getByText(primaryTitle, { exact: true })).toBeVisible();
    await publicContext.close();

    await page.getByRole('button', { name: 'Close without copying' }).click();
    await page
      .getByRole('article', { name: shareName })
      .getByRole('button', { name: 'Revoke' })
      .click();
    await page.getByRole('button', { name: 'Confirm revocation' }).click();
    await expect(page.getByRole('status')).toContainText('was revoked');

    await page.getByRole('link', { name: 'Trash' }).click();
    const trashTitle = `Deleted memory ${browserName}`;
    await expect(page.getByRole('article', { name: trashTitle })).toBeVisible();
    await page
      .getByRole('article', { name: trashTitle })
      .getByRole('button', { name: 'Undo deletion' })
      .click();
    await expect(page.getByRole('status')).toContainText('Restore queued for 1 item');
    await expect(page.getByRole('article', { name: trashTitle })).toHaveCount(0);
  });
});
