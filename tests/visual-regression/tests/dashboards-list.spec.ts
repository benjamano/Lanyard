import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "../login";

test("dashboards list, populated", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/manage/dashboards");

  // The grid only renders the seeded row once DashboardService.GetDashboardsAsync returns - a
  // real "loaded and populated" signal, not just "page navigated".
  await page.getByRole("row", { name: /Visual Test Dashboard/ }).waitFor();

  await expect(page).toHaveScreenshot("dashboards-list.png");
});
