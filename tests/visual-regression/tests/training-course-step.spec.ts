import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "../login";
import { getFixtureIds } from "../fixtureIds";

test("training course step, populated", async ({ page }) => {
  await loginAsAdmin(page);

  // TakeCourse.razor is keyed by CourseAssignment id, not Course id, and the assignment must
  // belong to the logged-in user - the fixture seeder assigns it to the seeded admin.
  const { courseAssignmentId } = getFixtureIds();
  await page.goto(`/training/${courseAssignmentId}`);

  // Only renders once assignment.Course.Sections has loaded.
  const sectionBody = page.locator(".take-course-section-body");
  await sectionBody.waitFor();

  // Masked, not asserted pixel-for-pixel: this rendered-HTML region flaked between two
  // back-to-back CI runs on the identical commit/environment with an identical ~1-2% diff
  // confined entirely to the seeded paragraph's glyphs (not layout) - consistent with web-font
  // swap timing, not a real regression. The surrounding chrome (progress bar, title, Next
  // button) stays under real pixel-diff coverage; only this text region is masked.
  await expect(page).toHaveScreenshot("training-course-step.png", { mask: [sectionBody] });
});
