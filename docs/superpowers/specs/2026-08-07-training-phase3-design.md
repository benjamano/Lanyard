# Training System Phase 3 — Manager Dashboard, Bulk Assign & Auto-Assign

## Context

Phase 1 (data model, `CourseService`, the Manager's course-builder Editor page) and Phase 2 (`CourseAssignmentService`, a stopgap single-user Assign dialog on the Editor page, the learner-facing `/training` list + course-taking wizard) are built, tested, reviewed, and committed on `F-Training-System`. This is Phase 3 of the whole-system plan at `C:\Users\benme\.claude\plans\i-ve-been-asked-by-glimmering-gadget.md`: the Manager-facing dashboard that makes assignment a real workflow instead of Phase 2's one-user-at-a-time stopgap, plus wiring up automatic assignment on new-user creation.

Settled by the whole-system plan and not revisited here: a tabbed dashboard (By Course / By Person / Auto-assign rules), a bulk-assign dialog with a role-vs-individual toggle and an optional due date, and auto-assign firing on new `UserProfile` creation for courses flagged `AutoAssignOnUserCreation`.

## Data Model

No new tables. Everything rides on Phase 1/2's existing `CourseAssignment`, `CourseQuizAttempt`, and `Course.AutoAssignOnUserCreation`.

## `CourseAssignmentService` — new methods

Added to the existing `ICourseAssignmentService`/`CourseAssignmentService` (same `Result<T>` / `IDbContextFactory<ApplicationDbContext>` / `.AsNoTracking().TagWithCallSite()` conventions as every method already in this service):

- `Task<Result<List<CourseAssignment>>> GetAssignmentsForCourseAsync(Guid courseId)` — all active assignments for a course, including `Course`... no, including the assigned `UserProfile`'s name (join or a second lookup) and `Attempts`, for the By Course tab.
- `Task<Result<BulkAssignResult>> AssignCourseToUsersAsync(Guid courseId, List<string> userIds, string assignedByUserId, DateTime? dueDate)` — the single method behind both bulk-assign dialog modes. Loops `userIds`; for each, checks for an existing active assignment of that course to that user (the same duplicate check Phase 2 added to `AssignCourseAsync`) and skips it if found rather than failing the whole batch. Returns `BulkAssignResult(int AssignedCount, int SkippedDuplicateCount)` — a new record in `Lanyard.Infrastructure.DTO.Training`, alongside the existing `QuizGradeResult`.
- `Task<Result<CourseAssignment>> UpdateAssignmentDueDateAsync(Guid assignmentId, DateTime? newDueDate)` — edits an existing assignment's due date (normalized to end-of-day UTC, matching Phase 2's `AssignCourseAsync` fix). No ownership check needed here — this is a Manager-only operation reached only from the role-gated dashboard, not from any learner-facing route.
- `Task<Result<bool>> UnassignAsync(Guid assignmentId)` — soft-deletes an assignment (`IsActive = false`).

`GetAssignmentsForUserAsync` (Phase 2) is reused as-is for the By Person tab — no new method.

**Role resolution stays out of this service.** `CourseAssignmentService` gets no dependency on Identity/roles. The "assign by role" dialog mode resolves role membership to a plain `List<string>` of user IDs itself (via a new `IApplicationRolesService.GetUsersInRoleAsync(string roleId)` — `ApplicationRolesService` already calls the underlying `UserManager.GetUsersInRoleAsync` internally inside `DeleteRoleAsync`, so this is a small public wrapper around an existing call, not new integration surface) and then calls the same `AssignCourseToUsersAsync`. "Role" is a UI-level resolution step, not a concept the training service needs to know about.

Duplicate-skip in `AssignCourseAsync` (single-assign, Phase 2) is unchanged — it still fails hard on a duplicate, since that's a deliberate single-user action. Only the new bulk path skips-and-counts.

## Manager Dashboard — `/manage/training`

`[Authorize(Roles = "Admin,Manager")]`. A `FluentTabs` page (same component already used in `ClientSettingsDialog.razor`) with three tabs:

**By Course** — a course picker (`FluentSelect`, same pattern as other pickers in this codebase) followed by a table of every active assignment for the selected course: person's name, status badge (reusing `CourseAssignment.GetStatus()` and the same status→color mapping already built for `/training`), latest attempt's score if any, due date. Each row has a due-date edit action (opens a small dialog with a single `FluentDatePicker` and a Save button, calling `UpdateAssignmentDueDateAsync`) and an "Unassign" action (confirmed via the existing `ConfirmDeleteUserDialog` pattern, calling `UnassignAsync`). A "Bulk assign…" button opens the bulk-assign dialog scoped to the selected course.

**By Person** — a user picker followed by that person's full training record via `GetAssignmentsForUserAsync`: every assigned course with status/score/due date. Read-only — due-date edits and unassignment happen only from By Course, so there's exactly one place each of those actions lives, not two competing paths to the same mutation.

**Auto-assign rules** — a plain list of active courses (via existing `CourseService.GetCoursesAsync`), each row with a toggle bound to `Course.AutoAssignOnUserCreation`, saved immediately on change via the existing `CourseService.SaveCourseAsync`. No new storage, no new service method.

## Bulk-Assign Dialog

Replaces Phase 2's stopgap entirely: `CourseEditor.razor`'s "Assign" button, its handler, and `AssignCourseDialog.razor` are deleted. The new dialog is opened only from the By Course tab's "Bulk assign…" button, so `CourseId` is already known/fixed for the dialog.

A small segmented toggle switches between two mutually exclusive modes (not a `FluentTabs`, since these aren't independent panels of content — picking one clears the other):
- **By role** — a `FluentSelect` over `ApplicationRolesService.GetAllApplicationRolesAsync()`.
- **Individually** — a multi-select over `ISecurityService.GetAllUsersAsync()`. Exact Fluent component (`FluentCombobox` with `Multiple="true"`, or an alternative) to be confirmed against the live v5 component API via the fluent-ui-blazor MCP during implementation, per this project's established habit of verifying rather than assuming Fluent component parameters.

Plus the existing optional due-date picker (reused from Phase 2's stopgap dialog, including its end-of-day-UTC normalization). Submitting resolves the mode to a `List<string>` of user IDs (either the role's members or the multi-select's picks) and calls `AssignCourseToUsersAsync`, then shows a summary toast: `"Assigned to {AssignedCount} people ({SkippedDuplicateCount} already had this course)."` when `SkippedDuplicateCount > 0`, or just `"Assigned to {AssignedCount} people."` otherwise.

## Auto-Assign Hook

In `SecurityService.CreateUserAsync`, immediately after `IdentityResult.Succeeded` is confirmed true and before returning `Result<UserCreationResult>.Ok(...)`: fetch active courses where `AutoAssignOnUserCreation == true`, and for each call `ICourseAssignmentService.AssignCourseToUsersAsync(courseId, [newUser.Id], assignedByUserId: null, dueDate: null)` — `assignedByUserId: null` matches the model's existing "null = auto-assigned" convention (`CourseAssignment.AssignedByUserId` was already designed nullable for exactly this in Phase 2's data model).

This whole block is wrapped in its own try/catch that only logs on failure (`Information`/`Warning` level per this project's SignalR/service logging conventions) and never affects the `Result<UserCreationResult>` returned by `CreateUserAsync` — a training-assignment failure must never block account creation, since a new hire needs their login regardless of whether their induction course could be auto-assigned.

`SecurityService` gains a dependency on `ICourseAssignmentService` for this call.

## Testing & Verification

- MSTest coverage for all four new `CourseAssignmentService` methods: course-scoped listing (active-only filtering, matches the soft-deleted-course fix from Phase 2's final review), bulk assign (duplicate-skip counting, both empty and partial-overlap cases), due-date update, unassign (soft-delete, excluded from subsequent reads). Plus a test on the auto-assign hook confirming a failure in course assignment doesn't fail `CreateUserAsync`.
- `dotnet build LanyardApp.sln` and `dotnet test src/Lanyard.Tests/Lanyard.Tests.csproj` passing, per `CLAUDE.md`.
- Manual Playwright verification: as Admin, open `/manage/training`, use By Course to bulk-assign a course to a role and confirm the right people get assignments (skipping anyone who already had it), edit a due date, unassign someone, switch to By Person and confirm the same data reads correctly from the other side, toggle a course's auto-assign rule on, create a new user and confirm they're auto-assigned that course, and confirm the Editor page's old stopgap Assign button/dialog no longer exists.
