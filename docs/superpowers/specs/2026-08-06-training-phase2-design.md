# Training System Phase 2 — Assignment Service & Submitter Flow

## Context

Phase 1 (data model + Manager/Editor course-builder page) is built and committed. This is Phase 2 of the whole-system plan at `C:\Users\benme\.claude\plans\i-ve-been-asked-by-glimmering-gadget.md`: the service that assigns courses to learners, and the learner-facing flow to actually take a course and its quiz.

The following decisions from the whole-system plan are treated as settled and are not revisited here: existing `UserProfile` accounts only (no invite flow), unlimited retakes, per-course pass-mark percentage, full attempt history kept in `CourseQuizAttempt`, `FluentWizard` with one step per content section plus a quiz step, status derived rather than stored.

Phase 3 (Manager dashboard — By Course / By Person tabs, full bulk-assign dialog, auto-assign-on-user-creation hook) is out of scope here.

## Data Model

Three new entities added to `src/Lanyard.Infrastructure/Models/TrainingModels.cs`, following the same `Guid Id` + `IsActive` soft-delete conventions as the Phase 1 models:

```csharp
public class CourseAssignment
{
    public Guid Id { get; set; }
    public required Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public required string UserId { get; set; }       // FK to UserProfile.Id (string)
    public string? AssignedByUserId { get; set; }      // null = auto-assigned (Phase 3)
    public DateTime AssignedDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public bool IsActive { get; set; }

    public virtual List<CourseQuizAttempt> Attempts { get; set; } = [];
}

public class CourseQuizAttempt
{
    public Guid Id { get; set; }
    public required Guid AssignmentId { get; set; }
    public CourseAssignment? Assignment { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime SubmittedDate { get; set; }
    public int ScorePercent { get; set; }
    public bool Passed { get; set; }

    public virtual List<CourseQuizAttemptAnswer> Answers { get; set; } = [];
}

public class CourseQuizAttemptAnswer
{
    public Guid Id { get; set; }
    public required Guid AttemptId { get; set; }
    public required Guid QuestionId { get; set; }
    public required Guid SelectedOptionId { get; set; }
    public bool WasCorrect { get; set; }
}
```

Status is derived, never stored:
- **NotStarted** — `StartedDate` is null.
- **InProgress** — `StartedDate` set, no attempt with `Passed == true` yet.
- **Completed** — at least one attempt with `Passed == true`. `CompletedDate` is set the first time this happens and is never overwritten by later attempts.
- **Overdue** — `DueDate` has passed and the assignment is not `Completed`. (Overrides InProgress/NotStarted for display purposes; it's a view on top of the other three, not a fourth stored state.)

One additive EF Core migration (3 `CreateTable`), 3 new `DbSet<T>` properties registered on `ApplicationDbContext`. No changes to existing tables.

## `CourseAssignmentService`

New `src/Lanyard.Server/LanyardServices/Services/Training/ICourseAssignmentService.cs` / `CourseAssignmentService.cs`. Same conventions as `CourseService`: `Result<T>` returns, `IDbContextFactory<ApplicationDbContext>` injected (never a scoped `DbContext` directly), `.AsNoTracking().TagWithCallSite()` on every read query.

Methods:

- `Task<Result<CourseAssignment>> AssignCourseAsync(Guid courseId, string userId, string assignedByUserId, DateTime? dueDate)`
  Creates a `CourseAssignment` row. `AssignedDate` = now. Used by both the Phase 2 stopgap assign dialog and Phase 3's real bulk-assign dialog.

- `Task<Result<IEnumerable<CourseAssignment>>> GetAssignmentsForUserAsync(string userId)`
  Loads the current user's assignments with `Course` and `Attempts` included, for `/training`.

- `Task<Result<CourseAssignment>> GetAssignmentAsync(Guid assignmentId, string requestingUserId)`
  Loads a single assignment with `Course.Sections`, `Course.Questions.Options`, and `Attempts.Answers`. Fails with `Result<T>.Fail(...)` if `requestingUserId` doesn't match the assignment's `UserId` — this is the ownership check that keeps one learner from opening another's assignment by guessing a GUID in the URL.

- `Task<Result<CourseAssignment>> StartAssignmentAsync(Guid assignmentId, string requestingUserId)`
  Sets `StartedDate = now` if it's currently null. Idempotent — safe to call every time the wizard page loads. Same ownership check as above.

- `Task<Result<QuizGradeResult>> SubmitQuizAttemptAsync(Guid assignmentId, string requestingUserId, Dictionary<Guid, Guid> answers)`
  `answers` maps `QuestionId -> SelectedOptionId`. Grades against `CourseQuestionOption.IsCorrect`, writes a new `CourseQuizAttempt` (`AttemptNumber` = existing count + 1) and its `CourseQuizAttemptAnswer` rows, sets `CompletedDate` on the assignment if this is the first passing attempt. Returns a `QuizGradeResult` DTO: overall `ScorePercent`, `Passed`, and a per-question `WasCorrect` map, so the UI can render the retry-with-feedback screen without a second round trip.

Registered as a scoped service in `Program.cs` next to `ICourseService`.

## Submitter Pages

### `/training` — `[Authorize]`, any logged-in user

Lists the current user's assignments via `GetAssignmentsForUserAsync`. Each row shows course name, a status badge (Not Started / In Progress / Completed / Overdue), and due date if set — no score on this list, that detail lives inside the course. Empty state: a plain "No training assigned yet" message, no empty table shell. Clicking a row navigates to `/training/{assignmentId}`.

### `/training/{assignmentId}` — `[Authorize]`

The course-taking page. On load: calls `StartAssignmentAsync` (idempotent), loads the assignment via `GetAssignmentAsync`. If the ownership check fails, redirect away with a toast, matching the existing error-navigation pattern used elsewhere in the app.

`FluentWizard` structure:
- One `FluentWizardStep` per `CourseSection`, ordered by `SortOrder`, rendering `BodyHtml` read-only inside a styled container (no Quill toolbar — this is display-only).
- One final "Quiz" step: every `CourseQuestion` (ordered by `SortOrder`) with its options as a `FluentRadioGroup`, all answered together and submitted as a single batch via the wizard's `OnFinish`.
- Always reopens at step 0 — no per-section resume tracking is persisted, per the decision to keep this simple since course content is short.
- **Mobile**: `StepperPosition` switches from `Left` to `Top` under a viewport breakpoint via a resize listener, so phone viewports get full-width content instead of a squeezed two-column layout. Desktop keeps the left-hand stepper.

**Quiz result / retry**: `OnFinish` calls `SubmitQuizAttemptAsync`. The quiz step's content is replaced with a result view:
- **Pass**: score %, a plain success state. No retry control.
- **Fail**: score %, a "you didn't pass, try again" banner, each question marked ✓/✗ (right/wrong only — the correct answer text is never revealed, so the quiz can't be memorized off a single failed attempt), and a Retry button that clears all answers on the quiz step so the learner can resubmit. Retries are unlimited and every attempt is kept in `CourseQuizAttempt` history.

## Minimal Assign UI (Editor page stopgap)

A new "Assign" button on `CourseEditor.razor` (Phase 1's page) opens a small dialog:
- `FluentSelect<UserProfile, string>` user picker, same pattern as `UserRolesManager.razor` (`GetAllUsersAsync()` + `.GetName()` for the option text).
- Optional due date picker.
- Assign button calling `AssignCourseAsync`.

This is intentionally a stopgap: Phase 3 replaces it with the real bulk-assign dialog (role-vs-individual toggle, multi-select) on the Manager dashboard. It isn't removed until that ships, since it's the only way to create assignments before then.

## Testing & Verification

- MSTest coverage for `CourseAssignmentService`, mirroring the structure of `CourseServiceTests.cs`: status derivation for all four states, grading correctness against `IsCorrect`, `CompletedDate` only ever set on first pass (never overwritten by a later attempt), `AttemptNumber` incrementing correctly across retries, ownership check rejecting a mismatched `requestingUserId`.
- `dotnet build LanyardApp.sln` and `dotnet test src/Lanyard.Tests/Lanyard.Tests.csproj` must pass before any task is considered done, per `CLAUDE.md`.
- Live Playwright verification against the running dev server: as Admin, assign the existing Play2Day course to a second test user via the new Editor-page dialog; log in as that user; walk the wizard through its sections; fail the quiz once and confirm the per-question ✓/✗ feedback plus working Retry; pass on retry and confirm the assignment shows Completed back on `/training`; resize to a phone viewport and confirm the stepper repositions to the top.
