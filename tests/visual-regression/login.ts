import type { Page } from "@playwright/test";

export const ADMIN_USERNAME = "admin";
export const ADMIN_PASSWORD = "Dev-Admin-Pw1!";

/**
 * Replicates the verify skill's documented two-step company-picker + credentials flow, but waits
 * on the post-login redirect instead of a fixed sleep - a real load-state signal rather than a
 * guessed timeout, which is what a screenshot suite needs to avoid flaking on a loaded CI runner.
 */
export async function loginAsAdmin(page: Page): Promise<void> {
  await page.goto("/login");

  // The company-picker button is present in the static prerendered HTML before Blazor Server's
  // SignalR circuit actually connects - clicking it that early is a no-op (the DOM event has
  // nothing wired to it yet), which is exactly the kind of "prerendered but not interactive"
  // Blazor Server trap that makes plain element-visibility waits unreliable here. Waiting for
  // the network to go quiet after the initial negotiate/connect round-trip is a real signal that
  // the circuit has connected, not a guessed timeout.
  await page.waitForLoadState("networkidle");
  await page.locator(".company-picker-button").first().click();

  const usernameField = page.getByRole("textbox", { name: /Username/ });
  await usernameField.waitFor();
  await usernameField.fill(ADMIN_USERNAME);
  await page.getByRole("textbox", { name: /Password/ }).fill(ADMIN_PASSWORD);

  await page.locator("fluent-dropdown#locationId").click();
  await page.locator("fluent-option:visible").first().click();

  await page.locator(".fluent-stack-horizontal.login-submit-row").click();

  await page.waitForURL((url) => !url.pathname.includes("/login"), { timeout: 15_000 });
}

/**
 * ClientSelector fires a "Successfully loaded N connected clients" toast on every load of the
 * Music and DMX desk pages. It auto-dismisses; waiting it out avoids a screenshot landing
 * mid-fade. Swallows the timeout if no toast ever appeared.
 */
export async function waitForToastToClear(page: Page): Promise<void> {
  const toast = page.locator("fluent-toast, .fluent-toast").first();
  await toast.waitFor({ state: "hidden", timeout: 10_000 }).catch(() => undefined);
}
