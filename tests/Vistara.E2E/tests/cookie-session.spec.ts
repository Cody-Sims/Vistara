import { readFile } from 'node:fs/promises';
import { expect, test } from '@playwright/test';
import { fixturePath } from '../support/paths.js';
import { readRuntimeState } from '../support/state.js';

const runtime = readRuntimeState();

/**
 * The antiforgery contract only applies to a cookie session, so this suite
 * signs in through the browser exactly as a visitor does. No header is set by
 * the test: every mutation must be accepted because the application itself
 * sent the antiforgery header the session published.
 */
test.describe('cookie session mutations', () => {
  test('uploads and creates an album with no header the test supplied', {
    tag: '@smoke',
  }, async ({ browserName, page }) => {
    const seeded = runtime.browsers[browserName];
    if (!seeded) {
      throw new Error(`No E2E tenant was seeded for ${browserName}.`);
    }

    const mutations: {
      method: string;
      path: string;
      status: number;
      antiforgery: string | null;
    }[] = [];
    const refusals: string[] = [];
    page.on('response', (response) => {
      const request = response.request();
      const path = new URL(response.url()).pathname;
      if (!path.startsWith('/api/v1/') || request.method() === 'GET') {
        return;
      }

      void request.headerValue('x-vistara-csrf').then((antiforgery) => {
        mutations.push({
          method: request.method(),
          path,
          status: response.status(),
          antiforgery,
        });
      });
      if (response.status() === 403) {
        refusals.push(`${request.method()} ${path}`);
      }
    });

    await page.goto(`${runtime.baseUrl}/login`);
    await page
      .getByLabel('Email address or user name')
      .fill(seeded.login);
    await page.getByLabel('Password').fill(seeded.password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();

    await page.goto(`${runtime.baseUrl}/uploads`);
    await expect(
      page.getByRole('heading', { name: 'Upload images' }),
    ).toBeVisible();
    const fileName = `cookie-${browserName}.png`;
    await page.getByLabel('Choose images to upload').setInputFiles({
      name: fileName,
      mimeType: 'image/png',
      buffer: Buffer.concat([
        await readFile(fixturePath),
        Buffer.from(`-cookie-${browserName}`, 'utf8'),
      ]),
    });
    await expect(page.getByRole('heading', { name: fileName })).toBeVisible();
    await expect
      .poll(() => mutations.find((call) => call.path === '/api/v1/uploads'))
      .toMatchObject({ method: 'POST', status: 201 });

    await page.goto(`${runtime.baseUrl}/albums`);
    const albumsRegion = page.getByRole('region', { name: 'Albums' });
    const albumName = `Cookie ${browserName}`;
    await page.getByLabel('Album name').fill(albumName);
    await page.getByRole('button', { name: 'Create album' }).click();
    await expect(albumsRegion.getByRole('status')).toHaveText(
      `${albumName} was created.`,
    );
    await expect
      .poll(() => mutations.find((call) => call.path === '/api/v1/albums'))
      .toMatchObject({ method: 'POST', status: 201 });

    expect(refusals).toEqual([]);
    const created = mutations.filter(
      (call) =>
        call.path === '/api/v1/albums' || call.path === '/api/v1/uploads',
    );
    expect(created.length).toBeGreaterThanOrEqual(2);
    for (const call of created) {
      expect(call.antiforgery, `${call.method} ${call.path}`).toBeTruthy();
    }

    // The same mutation, sent by the same page and the same cookie session,
    // is refused when the header is missing: the application is what makes the
    // difference, not the credential.
    const withoutHeader = await page.evaluate(async (name: string) => {
      const response = await fetch('/api/v1/albums', {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'Idempotency-Key': `${name}-no-header`,
        },
        body: JSON.stringify({ name: `Refused ${name}` }),
      });
      return { status: response.status, body: await response.json() };
    }, `cookie-${browserName}`);

    expect(withoutHeader.status).toBe(403);
    expect(withoutHeader.body).toMatchObject({
      code: 'cookie_auth.antiforgery_required',
    });
  });
});
