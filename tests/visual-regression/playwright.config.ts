import { defineConfig, devices } from "@playwright/test";

// CLAUDE.md mandates phone (390x844) + desktop (1440x900) for any UI screenshot - the two
// projects below give every spec both viewports automatically (Playwright names baseline files
// per-project, e.g. dashboards-list-phone-linux.png / dashboards-list-desktop-linux.png).
export default defineConfig({
  testDir: "./tests",
  globalSetup: "./globalSetup.ts",
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["html", { open: "never" }], ["list"]] : "list",

  use: {
    baseURL: process.env.BASE_URL ?? "http://localhost:5096",
    trace: "retain-on-failure",
  },

  expect: {
    toHaveScreenshot: {
      // Tight default - a loose global tolerance risks masking small real regressions on
      // static, content-heavy screens. Individual specs can override per-call for pages with
      // more inherent noise.
      maxDiffPixelRatio: 0.005,
      animations: "disabled",
    },
  },

  projects: [
    {
      name: "phone",
      use: { ...devices["Desktop Chrome"], viewport: { width: 390, height: 844 } },
    },
    {
      name: "desktop",
      use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } },
    },
  ],
});
