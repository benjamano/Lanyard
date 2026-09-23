import { test, expect } from "@playwright/test";
import { loginAsAdmin, waitForToastToClear } from "../login";

test("music page, populated", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/music");

  // Music.razor auto-loads the first playlist on init and goes through the DB-backed
  // PlaylistService - deterministic, unlike the "All Songs" view which reads the local machine's
  // MyMusic folder from disk in Development. .song-list-grid only renders once Songs is non-empty.
  await page.locator("table.song-list-grid").waitFor();
  await waitForToastToClear(page);

  await expect(page).toHaveScreenshot("music.png");
});
