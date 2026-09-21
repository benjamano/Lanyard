import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "../login";
import { getFixtureIds } from "../fixtureIds";

test("dashboard detail, populated", async ({ page }) => {
  await loginAsAdmin(page);

  const { dashboardId } = getFixtureIds();
  await page.goto(`/manage/dashboards/${dashboardId}`);

  // Loads in preview mode by default (EditDashboard.razor's IsPreviewing = true), so this is the
  // read-only "populated dashboard" view. .dashboard-widget-host only renders once
  // Dashboard.Widgets has actually loaded from the DB.
  await page.locator(".dashboard-widget-host").first().waitFor();

  await expect(page).toHaveScreenshot("dashboard-detail.png");
});
