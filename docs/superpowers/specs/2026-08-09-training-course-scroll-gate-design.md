# Training Course Scroll-to-Unlock — Design

## Context

`TakeCourse.razor` (`/training/{AssignmentId}`) presents a course as a `FluentWizard`: one step per `CourseSection` (rendered HTML reading material) followed by a Quiz step. A user can currently click **Next** on a content step without having read any of it, even if the section's content is taller than the visible area.

On desktop (`StepperPosition.Left`), the wizard has a fixed height and its content row (`.fluent-wizard-content`) scrolls internally (`overflow-y: auto`, see `TakeCourse.razor.css`). On mobile (`StepperPosition.Top`), the wizard height is `auto` instead, so the content row never scrolls internally — the page itself scrolls (see the code comments on `wizardHeight`/`cardHeight` in `TakeCourse.razor`).

## Goal

Require the user to scroll a content section to its bottom before its **Next** button becomes usable, with a tooltip explaining why it's disabled. The quiz step is unaffected — it's already gated by answering correctly.

## Design

### Scope

- Applies only to the `CourseSection` steps (indices `0..Sections.Count-1`). The Quiz step (index `Sections.Count`) keeps its existing Next/Retry/Done logic untouched.
- If a section's content already fits within the visible area (nothing to scroll), its Next button is enabled immediately.
- Once a section has been scrolled to the bottom at least once, it's remembered as "read" for the rest of the page session (a `HashSet<int>` of step indices, held in component state). Navigating away and back via Previous/stepper never re-locks a section that's already been read.

### UI

- The content-step branch of `FluentWizard`'s `ButtonTemplate` (`TakeCourse.razor`, currently the plain `Next` button around line 137) gets:
  - `DisabledFocusable="@(!readSections.Contains(stepIndex))"` — not `Disabled`, so the button still receives hover/focus while locked (`Disabled` removes it from the accessibility tree, which would prevent the tooltip from ever showing).
  - `Tooltip="@(readSections.Contains(stepIndex) ? null : "Scroll to the bottom of this section to continue.")"` — uses `FluentButton`'s built-in `Tooltip` parameter. No new provider wiring needed: `MainLayout.razor` already renders `<FluentProviders />`, which bundles tooltip support.
- The click handler itself also short-circuits if the section isn't in `readSections`, so the button can't be triggered even if something bypasses the visual disabled state.

### Scroll detection (new `trainingScrollGate.js`)

A new JS module, `wwwroot/js/trainingScrollGate.js`, alongside the existing `viewportWatcher.js`, mirroring its structure (an IIFE on `window`, `init`/`attach`-style lifecycle, disposed from `DisposeAsync`).

Responsibilities:
- Given the wizard's host element, resolve whichever element is actually the scrolling one right now: prefer `.fluent-wizard-content` if it's internally overflowing (`scrollHeight - clientHeight` past a small threshold); otherwise fall back to the document/window (covers the mobile `auto`-height case).
- Listen for `scroll` (on whichever element is scrolling) and `resize` (window), and report "is the content scrolled to its bottom" back to Blazor via `dotNetRef.invokeMethodAsync('OnScrollGateChanged', isAtBottom)`.
- Expose a `resetForStep()` call that scrolls the resolved container back to the top and re-reports — invoked whenever the wizard's active step changes, so a freshly-entered section starts from the top rather than inheriting scroll position from whatever the previous step left behind.
- A small bottom threshold (e.g. 24px) so "close enough to the bottom" counts, matching typical scroll-affordance UX.

### Blazor wiring (`TakeCourse.razor` code-behind)

- `FluentWizard` gets `@bind-Value="currentWizardStepIndex"` (new `private int currentWizardStepIndex` field) so the component always knows the active step regardless of how the user navigated there (Next/Previous buttons or clicking a step bubble directly).
- `OnAfterRenderAsync`:
  - First render: alongside the existing `viewportWatcher.init` call, call `trainingScrollGate.attach(hostElementRef, dotNetRef)`.
  - Any render where `currentWizardStepIndex` has changed since last checked: call `trainingScrollGate.resetForStep()`.
- New `[JSInvokable] OnScrollGateChanged(bool isAtBottom)`: if `isAtBottom` and the current step is a content step, add it to `readSections` and call `StateHasChanged` (mirrors the existing `OnViewportChanged` pattern).
- `DisposeAsync` also disposes `trainingScrollGate` alongside the existing `viewportWatcher.dispose`.
- The wizard host div (`<div class="take-course-wizard-host">`) gets an `@ref` so it can be passed into the JS `attach` call.

## Out of scope

- Quiz step navigation/gating logic — unchanged.
- Persisting "read" state beyond the current page session (e.g. across reloads or devices) — not required by this feature.
- Any change to `Program.cs` or `MainLayout.razor` — `FluentProviders` already covers tooltip rendering.
