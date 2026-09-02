import type { Page } from '@playwright/test';

/**
 * Opening the sign-in page the way a visitor arrives at it.
 *
 * The page resolves two things as it loads: whether this browser already has a
 * session, and what this deployment offers to sign in with. An anonymous
 * visitor's answer to the first is `401`, and the application ends a session
 * on any refused request — which is what a visitor whose cookie expired needs
 * to happen, and which will just as happily end a session that a test opened
 * in the few milliseconds since. The second answer rewrites the form's
 * surroundings, so a click aimed at the button before it lands can be aimed at
 * a node React is about to replace.
 *
 * Neither is a race a person can lose and both are races an automated run can,
 * so the harness lets the page finish arriving before it types. This is the
 * suite behaving like a visitor rather than the application being given an
 * allowance.
 */
export async function openSignInPage(page: Page, baseUrl: string) {
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' });
}

/** Opens the sign-in page and signs in with a Vistara password. */
export async function signInWithPassword(
  page: Page,
  baseUrl: string,
  credentials: { readonly login: string; readonly password: string },
) {
  await openSignInPage(page, baseUrl);
  await page.getByLabel('Email address or user name').fill(credentials.login);
  await page.getByLabel('Password').fill(credentials.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}
