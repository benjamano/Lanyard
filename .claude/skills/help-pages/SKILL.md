---
name: help-pages
description: How LanyardApp's in-app help works. Covers the /help hub, HelpRegistry, the HelpPage/HelpTab/HelpSection/HelpNote/HelpButton components, where help routes live and how they're authorised, how to write help content, and the HelpCoverageTests that fail when a page has no help. Use whenever adding or changing a page or any user-visible behaviour, so the area's help gets updated too, and whenever writing, restyling or debugging a help page or Help button.
---

# Help pages

**The rule:** every area of the app has a help page. When you add a page, or change what users see or do in an area, update that area's help **in the same piece of work**.
- `HelpCoverageTests` fails the build when a page isn't covered by any help area.
- No test can tell whether the help *text* is still accurate. That part is on you: when you change a feature, reread its help tabs.

## Moving parts

| Piece | Where | What it does |
|---|---|---|
| `HelpRegistry` | `src/Lanyard.Server/LanyardApp/Help/HelpRegistry.cs` | One `HelpArea` per help page. It is the single source of truth for the hub, the Help buttons and the tab shell. |
| `/help` hub | `Components/Pages/Help/HelpIndex.razor` | Shows a card for each area the user can open, filtered with `HelpArea.IsVisibleTo` (roles plus `GetHiddenFeaturesForCurrentUserAsync`), grouped by audience. Search covers titles, summaries and topic titles. |
| `HelpPage` | `Components/Help/HelpPage.razor` | The shell: title, summary, an "All help" link back to the hub, and the topic tabs. Opens the tab named by `?topic=` once, then leaves tab clicks alone. |
| `HelpTab` / `HelpSection` / `HelpNote` | `Components/Help/` | Layout helpers for tab bodies: a 16px-gap stack, a titled H5 block, and a `FluentMessageBar` tip (Info by default, `Intent=` for Warning). |
| `HelpButton` | `Components/Help/HelpButton.razor` | The Outline "Help" button for an area's pages. `<HelpButton AreaKey="@HelpRegistry.X" Topic="..." />`. An unknown topic opens the first tab. |
| Nav | `Layout/Staff/NavMenu.razor` | The "Help" item next to the account link. Phones reach it through the hamburger menu. |
| Tests | `src/Lanyard.Tests/Components/HelpCoverageTests.cs` | Coverage, matching access rules, unique keys and topics, and visibility filtering. |

## Adding help for an area

1. **Register it.** Add a key constant and a `HelpArea` to `HelpRegistry.Areas`:
   - `Route`: `<area-prefix>/help`, e.g. `/manage/rota/help` or `/rota/help`. Keeping it under the area's prefix means `RouteAuthorizationCoverageTests.PaidModulePages_CarryRequiresFeature` also checks it.
   - `Roles` and `Feature`: **exactly** what the page will carry in `[Authorize(Roles=...)]` and `[RequiresFeature(...)]`. The test compares them as sets.
   - `Covers`: the route prefixes this help explains. Every routed page must match some area's `Covers` or the `Exempt` list in the test.
   - `Topics`: one `HelpTopic(key, title)` per tab. The first topic is the default tab.
   - `Audience` picks the hub group: Everyone, Managers or Admins.
2. **Write the tabs** as components in `Components/Help/<Area>/<Topic>HelpTab.razor`, each wrapped in `<HelpTab>`. Training's manager help predates this layout and lives in `Components/Manager/Training/Help/`.
3. **Add the page** next to the area's other pages:
   ```razor
   @page "/manage/rota/help"
   @attribute [Authorize(Roles = "Admin,Manager")]
   @attribute [RequiresFeature(CompanyFeature.StaffScheduling)]
   @using Microsoft.AspNetCore.Authorization
   @using Lanyard.App.Components.Help.Rota

   <HelpPage AreaKey="@HelpRegistry.ManageRota">
       <FluentTab Id="help-overview" Header="Overview" IconStart="@(new Icons.Regular.Size16.Info())">
           <RotaOverviewHelpTab />
       </FluentTab>
       <FluentTab Id="help-timesheets" Header="Timesheets" ... DeferredLoading="true">...</FluentTab>
   </HelpPage>
   ```
   Each tab `Id` must be `help-<topic key>`, which is what `HelpTopic.TabId` produces. Every tab after the first gets `DeferredLoading="true"`.
4. **Link it.** Put a `HelpButton` in the header row of the area's main pages, usually in a `FluentStack` with `HorizontalAlignment.SpaceBetween` and `Wrap="true"`. On pages with tabs, choose `Topic` from the active tab, the way `TrainingDashboard.HelpTopicForActiveTab` does.
5. Add a release-notes entry and version bump, then screenshots and a walkthrough, as for any UI change.

## Writing help content

- Use UK English and speak to the reader as "you". Keep it task-led: what the area is for, then numbered steps for each job people do in it.
- Name buttons, tabs and fields exactly as they appear on screen, in **bold**, and link to the real pages with plain `<a href="/...">`.
- **Write from the code, not from memory.** Read the page and its service before you describe a behaviour. Never invent limits, emails, notifications or permissions. If the code doesn't send an email, say so, as `TrackingCompletionHelpTab` does.
- Explain who can do what (Staff, Manager, Admin, and permission roles such as `CanManageDmxSystems`) and any per-location rules (see `location-scoping`).
- Keep sections short. Use `HelpNote` for gotchas, and show statuses with `FluentBadge` in the same colours the app uses.
- Don't use inline styles. If a tab needs CSS, give it a scoped `.razor.css` with `::deep` from a plain wrapper element (see `fluentui-v5-blazor`).

## Exemptions

The `Exempt` dictionary in `HelpCoverageTests` is only for pages with nothing to explain, each with a reason: sign-in flow, legal and error pages, kiosk-rendered screens. While the help rollout was stacked across PRs, it also held "Pending help PR n/6" lines, and each PR removed its own. Don't add pending lines for new work. Write the help instead.
