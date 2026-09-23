import { test, expect } from "@playwright/test";
import { loginAsAdmin, waitForToastToClear } from "../login";

// The DMX desk's "connected" state (live channel values, scene tiles) is driven entirely by an
// in-memory SignalR connection id set that only the Windows-only WPF kiosk client populates -
// there is no DB-only or Linux-CI-feasible way to simulate a real connection (see the
// kiosk-client-dev-stack skill). This test screenshots the desk's default no-client-selected
// state instead, which is still a real, deterministic screen worth regression-testing (layout,
// client selector, default fader state) even without live data. See the
// visual-regression-testing skill for the full rationale and what it would take to extend
// coverage to a connected state later.
test("DMX desk, default (no client selected) state", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/dmx");

  // The "Scenes" card renders regardless of whether a client is selected - it just shows
  // "Select a client to view scenes." until one is. That's still a real post-load signal that
  // ClientSelector etc. have all finished their initial data fetch.
  await page.getByText("Scenes").first().waitFor();
  await waitForToastToClear(page);

  await expect(page).toHaveScreenshot("dmx-desk-default.png");
});
