import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { X509Certificate } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { apiCertificatePath, e2eDirectory } from '../support/paths.js';
import { oidcApiCertificatePath, oidcCertificatePath } from '../support/oidc.js';
import {
  isSuccess,
  pinnedGet,
  plainHttpAttempt,
  readPinnedCertificate,
} from '../support/tls.js';
import { openSignInPage, signInWithPassword } from '../support/session.js';
import { readRuntimeState } from '../support/state.js';

const runtime = readRuntimeState();
const oidc = runtime.oidc;

/**
 * The PEM label every private key encoding carries. It is assembled rather
 * than written out so that this file, which searches for it, is not itself a
 * match.
 */
const privateKeyLabel = ['PRIVATE', 'KEY'].join(' ');

/** Every host this run started, with a path each one is known to answer. */
const hosts = [
  { name: 'gallery API', url: runtime.baseUrl, health: '/health/live' },
  { name: 'hosted sign-in API', url: oidc.baseUrl, health: '/health/live' },
  {
    name: 'stub identity provider',
    url: oidc.identityProviderUrl,
    health: '/health',
  },
] as const;

/**
 * The transport the rest of this suite depends on.
 *
 * Vistara's browser session is carried by `__Host-vistara-session`, and hosted
 * sign-in binds one browser to one in-flight authorization with
 * `__Host-vistara-oidc`. Both are `Secure`, which is a product decision worth
 * keeping. Chromium and Gecko make an exception for loopback and keep such a
 * cookie delivered over `http://127.0.0.1`; WebKit does not, and drops it
 * silently, so a plain-HTTP harness signs in on two engines and fails on the
 * third for a reason that has nothing to do with the application.
 *
 * So the harness serves TLS, and this suite is what holds it there: the run's
 * hosts are HTTPS, they are not usable without it, a real browser keeps a
 * `Secure` host cookie across a real sign-in and across the hop to a provider,
 * and none of it costs the repository a committed key.
 */
test.describe('loopback transport', () => {
  test('serves every host over TLS', { tag: '@smoke' }, () => {
    for (const host of hosts) {
      expect(new URL(host.url).protocol, host.name).toBe('https:');
    }
  });

  test('answers only the certificate this run published', {
    tag: '@smoke',
  }, async () => {
    const gallery = readPinnedCertificate(apiCertificatePath);
    const hosted = readPinnedCertificate(oidcApiCertificatePath);

    expect(
      isSuccess((await pinnedGet(`${runtime.baseUrl}/health/live`, gallery)).status),
      'the gallery API refused a caller pinning its own certificate',
    ).toBe(true);
    expect(
      isSuccess((await pinnedGet(`${oidc.baseUrl}/health/live`, hosted)).status),
      'the hosted sign-in API refused a caller pinning its own certificate',
    ).toBe(true);

    // Each host generates its own certificate, so presenting a sibling's is a
    // failure. Were these probes trusting the transport rather than one
    // certificate, the two below would quietly succeed.
    expect(
      await attempt(() => pinnedGet(`${runtime.baseUrl}/health/live`, hosted)),
    ).toBe('rejected');
    expect(
      await attempt(() => pinnedGet(`${oidc.baseUrl}/health/live`, gallery)),
    ).toBe('rejected');
  });

  test('leaves no plain HTTP listener behind', { tag: '@smoke' }, async () => {
    for (const host of hosts) {
      const insecure = new URL(host.url);
      insecure.protocol = 'http:';
      const answer = await plainHttpAttempt(`${insecure.origin}${host.health}`);
      // A TLS port may answer a plaintext request with an error rather than
      // refuse the connection outright. What must never happen is a working
      // response, because that is a way into the application without the
      // transport its cookies require.
      expect(
        'status' in answer && isSuccess(answer.status),
        `${host.name} answered plain HTTP with ${JSON.stringify(answer)}`,
      ).toBe(false);
    }
  });

  test('keeps the Secure session cookie in this browser', {
    tag: '@smoke',
  }, async ({ browserName, page }) => {
    const seeded = runtime.browsers[browserName];
    if (!seeded) {
      throw new Error(`No E2E tenant was seeded for ${browserName}.`);
    }

    await signInWithPassword(page, runtime.baseUrl, seeded);
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();

    const session = (await page.context().cookies(runtime.baseUrl)).find(
      (cookie) => cookie.name === '__Host-vistara-session',
    );
    expect(session, `${browserName} kept no session cookie`).toBeDefined();
    expect(session).toMatchObject({
      secure: true,
      httpOnly: true,
      path: '/',
      sameSite: 'Lax',
    });
  });

  test('keeps the Secure login handle across the hop to the provider', async ({
    browserName,
    page,
  }) => {
    await openSignInPage(page, oidc.baseUrl);
    await page
      .getByRole('link', { name: `Sign in with ${oidc.providerDisplayName}` })
      .click();
    await page.waitForURL(`${oidc.identityProviderUrl}/**`);

    const handle = (await page.context().cookies(oidc.baseUrl)).find(
      (cookie) => cookie.name === '__Host-vistara-oidc',
    );
    expect(handle, `${browserName} kept no login handle`).toBeDefined();
    expect(handle).toMatchObject({ secure: true, httpOnly: true, path: '/' });
  });

  test('writes no key material anywhere in the suite', { tag: '@smoke' }, () => {
    for (const published of [
      apiCertificatePath,
      oidcApiCertificatePath,
      oidcCertificatePath,
    ]) {
      const bytes = readFileSync(published);
      // A certificate and nothing else: parseable as one, and carrying no
      // encoded private key for whoever finds the file.
      expect(new X509Certificate(bytes).subject).toContain('CN=127.0.0.1');
      expect(bytes.toString('binary')).not.toContain(privateKeyLabel);
    }

    expect(
      findKeyMaterial(e2eDirectory),
      'the run left private key material on disk',
    ).toEqual([]);

    // The published certificates live in the artifacts folder, which the
    // repository ignores, so no run can leave one staged either.
    expect(readFileSync(path.join(e2eDirectory, '.gitignore'), 'utf8')).toContain(
      '.artifacts/',
    );
  });
});

/** Whether a probe was answered or refused. */
async function attempt(probe: () => Promise<unknown>) {
  try {
    await probe();
    return 'resolved';
  } catch {
    return 'rejected';
  }
}

/**
 * Anything under the suite that looks like a private key or a key store.
 * Build output and installed packages are skipped: they belong to the
 * toolchain rather than to this run, and cryptography libraries carry PEM
 * labels of their own.
 */
function findKeyMaterial(directory: string): string[] {
  const keyStores = new Set(['.pfx', '.p12', '.pem', '.key', '.jks']);
  const skipped = new Set([
    'node_modules',
    '.git',
    'bin',
    'obj',
    'playwright-report',
    'test-results',
  ]);
  const found: string[] = [];
  const pending = [directory];
  while (pending.length > 0) {
    const current = pending.pop()!;
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const candidate = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (!skipped.has(entry.name)) {
          pending.push(candidate);
        }

        continue;
      }

      if (!entry.isFile()) {
        continue;
      }

      if (
        keyStores.has(path.extname(entry.name).toLowerCase()) ||
        (statSync(candidate).size < 4_000_000 &&
          readFileSync(candidate).toString('binary').includes(privateKeyLabel))
      ) {
        found.push(path.relative(e2eDirectory, candidate));
      }
    }
  }

  return found;
}
