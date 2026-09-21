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
  await page.locator(".take-course-section-body").waitFor();

  await expect(page).toHaveScreenshot("training-course-step.png");
});
