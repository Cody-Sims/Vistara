import { expect, test, type Page } from '@playwright/test';
import { signInWithPassword } from '../support/session.js';
import { readRuntimeState } from '../support/state.js';

const runtime = readRuntimeState();
const oidc = runtime.oidc;
const providerControl = `Sign in with ${oidc.providerDisplayName}`;
const hostedFailure = 'Signing in with your identity provider did not finish.';

/**
 * Where signing out ends: the sign-in page, with or without the destination a
 * route guard remembered on the way.
 */
const signedOutLocation = new RegExp(
  `^${oidc.baseUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/login(\\?.*)?$`,
);

/**
 * Hosted sign-in as a visitor actually experiences it: leaving Vistara for
 * another origin, consenting there as a directory identity, and coming back.
 *
 * The suite runs against a dedicated API instance whose database has one
 * member with a directory identity already linked, a password, and no claimed
 * first-owner marker, so both an ordinary member sign-in and a first-owner
 * bootstrap can be observed without either one arranging the other.
 */
test.describe('hosted sign-in', () => {
  test.describe.configure({ mode: 'serial' });

  test('signs an existing member in and keeps every secret off this device', {
    tag: '@smoke',
  }, async ({ page }) => {
    // The guard sends an anonymous visitor to sign in with where they were
    // going, and that destination has to survive a round trip through another
    // origin.
    await page.goto(`${oidc.baseUrl}/albums`);
    await expect(page).toHaveURL(`${oidc.baseUrl}/login?returnTo=%2Falbums`);

    const control = page.getByRole('link', { name: providerControl });
    await expect(control).toHaveAttribute(
      'href',
      '/api/v1/auth/oidc/entra/start?returnTo=%2Falbums',
    );
    // The local way in is still on the page, unchanged.
    await expect(page.getByLabel('Password')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();

    await consentAs(page, { objectId: oidc.memberObjectId });

    await expect(page.getByRole('heading', { name: 'Albums' })).toBeVisible();
    expect(page.url()).toBe(`${oidc.baseUrl}/albums`);

    // Nothing the protocol produced may be left where a script, an extension,
    // or the next visitor to this device could read it.
    const address = new URL(page.url());
    expect(address.search).toBe('');
    const kept = await page.evaluate(() => ({
      local: JSON.stringify(localStorage),
      session: JSON.stringify(sessionStorage),
      cookies: document.cookie,
    }));
    for (const store of [kept.local, kept.session, kept.cookies]) {
      expect(store).not.toContain('eyJ');
      expect(store).not.toMatch(/code[-=]/);
      expect(store).not.toContain('vistara-session');
    }

    // The one thing this device remembers is which provider opened the
    // session, so signing out can end that session too.
    expect(kept.session).toContain('vistara:signInProvider');
    expect(kept.session).toContain(oidc.providerId);

    const me = await readSession(page);
    expect(me.status).toBe(200);
    expect(me.body).toMatchObject({
      email: oidc.login,
      authenticationKind: 'cookie',
    });
  });

  test('refuses an identity that is neither a member nor allowlisted', async ({
    page,
  }) => {
    await page.goto(`${oidc.baseUrl}/login`);
    await consentAs(page, { objectId: oidc.strangerObjectId });

    await expectRefusal(page);
  });

  test('refuses a member identity that comes from another directory', async ({
    page,
  }) => {
    await page.goto(`${oidc.baseUrl}/login`);
    await consentAs(page, {
      objectId: oidc.memberObjectId,
      directoryTenantId: oidc.foreignDirectoryTenantId,
    });

    await expectRefusal(page);
  });

  test('refuses a sign-in the visitor cancelled at the provider', async ({
    page,
  }) => {
    await page.goto(`${oidc.baseUrl}/login`);
    await consentAs(page, {
      objectId: oidc.memberObjectId,
      decision: 'deny',
    });

    await expectRefusal(page);
  });

  /**
   * A callback is answered once. Replaying it from a browser that no longer
   * holds the login handle must open nothing, and must say no more about why
   * than any other refusal does.
   */
  test('refuses a replayed callback', async ({ page }) => {
    await page.goto(`${oidc.baseUrl}/login`);
    const callback = page.waitForRequest(
      (request) =>
        request.url().startsWith(`${oidc.baseUrl}/api/v1/auth/oidc/entra/callback`),
    );
    await consentAs(page, { objectId: oidc.memberObjectId });
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();
    const replayed = (await callback).url();
    expect(replayed).toContain('code=');

    await page.context().clearCookies();
    await page.goto(replayed);

    await expectRefusal(page);
  });

  /**
   * The bootstrap marker is claimed once for a deployment, so this is asserted
   * in one browser rather than pretended in three.
   */
  test('lets only an allowlisted directory identity claim the first workspace', async ({
    browserName,
    page,
  }) => {
    test.skip(
      browserName !== 'chromium',
      'First-owner bootstrap can only be claimed once per deployment.',
    );

    await page.goto(`${oidc.baseUrl}/login`);
    await consentAs(page, { objectId: oidc.allowedOwnerObjectId });

    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();
    const me = await readSession(page);
    expect(me.status).toBe(200);
    const account = me.body as {
      tenants: { slug: string; role: string }[];
    };
    expect(account.tenants).toContainEqual(
      expect.objectContaining({
        slug: oidc.bootstrapTenantSlug,
        role: 'TenantOwner',
      }),
    );

    // The workspace now has an owner, so the same identity signing in again is
    // an ordinary member rather than a second bootstrap.
    await signOutFromAccountMenu(page);
    await page.goto(`${oidc.baseUrl}/login`);
    await consentAs(page, { objectId: oidc.allowedOwnerObjectId });
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();
  });

  /** Composing hosted sign-in must not take the local way in away. */
  test('signs the same member in with their Vistara password', async ({
    page,
  }) => {
    await signInWithPassword(page, oidc.baseUrl, oidc);

    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();
    expect(
      await page.evaluate(() => sessionStorage.getItem('vistara:signInProvider')),
    ).toBeNull();

    // A password session is not a provider session, so signing out of it stays
    // here rather than visiting a provider that never issued it.
    const visited = recordVisits(page);
    await signOutFromAccountMenu(page);
    await expect(page).toHaveURL(signedOutLocation);
    expect(visited.some(atTheProvider)).toBe(false);
    expect((await readSession(page)).status).toBe(401);
  });

  /**
   * Relying-party sign-out revokes the browser session, so it is a mutation
   * and the antiforgery contract applies to it exactly as it does to any
   * other.
   */
  test('refuses a sign-out the application did not send', async ({ page }) => {
    await page.goto(`${oidc.baseUrl}/login`);
    await consentAs(page, { objectId: oidc.memberObjectId });
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();

    const forged = await page.evaluate(async () => {
      const response = await fetch('/api/v1/auth/oidc/entra/sign-out', {
        method: 'POST',
        credentials: 'same-origin',
      });
      return { status: response.status, body: await response.json() };
    });

    expect(forged.status).toBe(403);
    expect(forged.body).toMatchObject({
      code: 'cookie_auth.antiforgery_required',
    });
    // The refused call revoked nothing.
    expect((await readSession(page)).status).toBe(200);
  });

  test('ends the Vistara session first and the provider session after', async ({
    page,
  }) => {
    await page.goto(`${oidc.baseUrl}/login`);
    await consentAs(page, { objectId: oidc.memberObjectId });
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();

    const visited = recordVisits(page);

    await signOutFromAccountMenu(page);

    // The browser is handed to the provider's end-session endpoint and comes
    // back through the reply URL the deployment registered. A redirect never
    // commits a document, so the hop is observed as a request rather than as
    // a navigation.
    await expect(page).toHaveURL(signedOutLocation);
    expect(visited.some(atTheProvider)).toBe(true);
    expect(
      visited.some((url) =>
        url.startsWith(`${oidc.baseUrl}/api/v1/auth/oidc/entra/signed-out`),
      ),
    ).toBe(true);
    expect((await readSession(page)).status).toBe(401);
    expect(
      await page.evaluate(() => sessionStorage.getItem('vistara:signInProvider')),
    ).toBeNull();
    await expect(page.getByLabel('Password')).toBeVisible();
  });

  /**
   * The provider control is a real control: it is reachable by keyboard, it is
   * large enough to hit on a phone, and it does not animate for a visitor who
   * asked for less motion.
   */
  test('offers the provider control on a phone, by keyboard, and without motion', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.goto(`${oidc.baseUrl}/login`);

    const control = page.getByRole('link', { name: providerControl });
    await expect(control).toBeVisible();
    const box = await control.boundingBox();
    expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
    expect(box?.width ?? 0).toBeLessThanOrEqual(390);
    // The deployment's reduced-motion rule collapses every transition to a
    // duration a visitor cannot perceive, so the control must not animate.
    const transition = await control.evaluate(
      (element) => getComputedStyle(element).transitionDuration,
    );
    expect(Number.parseFloat(transition)).toBeLessThan(0.001);

    await control.focus();
    await expect(control).toBeFocused();
  });
});

/**
 * Plays the part of the visitor at the provider: leaves Vistara through the
 * published control, chooses which directory identity to be, and consents or
 * refuses.
 */
async function consentAs(
  page: Page,
  identity: {
    readonly objectId: string;
    readonly directoryTenantId?: string;
    readonly decision?: 'allow' | 'deny';
  },
) {
  await page.getByRole('link', { name: providerControl }).click();
  await page.waitForURL(`${oidc.identityProviderUrl}/**`);
  await expect(
    page.getByRole('heading', { name: 'Stub identity provider' }),
  ).toBeVisible();

  await page.getByLabel('Object identifier').fill(identity.objectId);
  if (identity.directoryTenantId) {
    await page.getByLabel('Directory tenant').fill(identity.directoryTenantId);
  }

  await page
    .getByRole('button', {
      name: identity.decision === 'deny' ? 'Cancel' : 'Continue',
    })
    .click();
}

/**
 * Every refusal looks the same to the browser: back to sign-in, one announced
 * message, no session, and no code left in the address bar to repeat it.
 */
async function expectRefusal(page: Page) {
  await expect(page.getByRole('alert')).toContainText(hostedFailure);
  await expect(page).toHaveURL(`${oidc.baseUrl}/login`);
  await expect(page.getByLabel('Password')).toBeVisible();
  expect((await readSession(page)).status).toBe(401);
}

/**
 * Reads the session the way the application does: a same-origin request from
 * the page itself, carrying whatever the browser holds and nothing a test put
 * there.
 */
async function readSession(page: Page) {
  return page.evaluate(async () => {
    const response = await fetch('/api/v1/me', { credentials: 'same-origin' });
    return {
      status: response.status,
      body: (await response.json()) as unknown,
    };
  });
}

/** Every document this browser asked for, redirects included. */
function recordVisits(page: Page): string[] {
  const visited: string[] = [];
  page.on('request', (request) => {
    if (request.resourceType() === 'document') {
      visited.push(request.url());
    }
  });
  return visited;
}

const atTheProvider = (url: string) =>
  url.startsWith(`${oidc.identityProviderUrl}/`);

/** Signs out the way a visitor does: from the account menu in the navigation. */
async function signOutFromAccountMenu(page: Page) {
  const account = page
    .getByRole('button', { name: /Owner|Member|Admin/ })
    .filter({ visible: true })
    .first();
  await account.click();
  const signOut = page
    .getByRole('button', { name: 'Sign out' })
    .filter({ visible: true })
    .first();
  await expect(signOut).toBeVisible();
  await signOut.click();
  // Signing out is a chain rather than a click: the session is revoked, this
  // device is cleared, and a hosted session additionally leaves for the
  // provider and comes back. The test waits for the end of it, because
  // navigating away in the middle would leave a session it never revoked.
  await page.waitForURL(signedOutLocation);
}
