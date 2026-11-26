---
name: dashboard-widgets
description: Checklist for adding a new dashboard widget type in LanyardApp — the enum, model subclass, EF discriminator, a DashboardWidgetCatalog entry, the renderer, settings and DashboardService's copy switches. Missing one causes a runtime EF error, a widget that saves fine but silently renders blank, or a widget whose whole dashboard save fails. Also covers the layout engine (DashboardLayoutEngine), the phone/fit-to-screen layout options, and who may edit dashboards. Use whenever adding a new widget type, debugging a widget that isn't rendering/configuring/saving correctly, or changing how dashboards are laid out.
---

# Adding a dashboard widget type

Every widget renders through one table, `Components/Dashboards/DashboardWidgetCatalog.cs`. `DashboardGrid` and the editor (and later the designer's palette) read it, so there are no per-type switches in the grid any more. A new type still touches several files that nothing links at compile time:

1. **`Lanyard.Infrastructure/Enum/WidgetType.cs`**: add the enum value. Check open PRs first: two stacks have clashed on the same number before.
2. **`Lanyard.Infrastructure/Models/DashboardModels.cs`**: add a `DashboardWidget` subclass with a `[SetsRequiredMembers]` constructor that sets `Type` and a default `GridW`/`GridH`.
3. **`ApplicationDbContext`'s `HasDiscriminator(...).HasValue<...>()` chain**: add the subclass, then generate and apply an EF migration. If you miss this you get a runtime EF discriminator error, not a compile error.
4. **`DashboardWidgetCatalog.All`**: add a `WidgetDescriptor` with name, one-line description, category, icon, a `Create` that returns the new subclass, the renderer type and the settings panel type (`null` only if there's nothing to configure).
5. **The renderer, `Components/Dashboards/Widgets/RenderXWidget.razor`**: it must have `[Parameter] public required XWidget Widget`. It may also declare any of these optional parameters. `DashboardWidgetCatalog.RenderParameters` only passes the ones a component declares, because `DynamicComponent` throws on unknown parameters.
   - `AllowTrigger`: true on a live dashboard. Buttons and controls must only act when this is true.
   - `PreviewMode`: true on a live dashboard.
   - `CompanyId`: the dashboard's company. Kiosks render signed out.
6. **Settings panel**: add `Components/Dashboards/Designer/Settings/XWidgetSettings.razor`, with `@inherits WidgetSettingsBase<XWidget>`. Edit through `SetAsync(() => W.Prop = v, "Short description")` so the canvas redraws and the change can be undone. Clamp numbers in the setter. Use `KioskPicker`, `LocationPicker` and `ScreenPicker` rather than hand-rolling selects. The panel shows in the designer's inspector; there are no settings dialogs.
7. **`DashboardService.cs`**: three places, all easy to forget:
   - `CreateWidgetCopy`'s switch expression needs a case that copies the type's settings. Its `_ => throw` fallback fails the **whole dashboard save** for an unhandled type, and that failure only shows as a toast. Types with no settings still need a bare case (`GreetingWidget => new GreetingWidget()`).
   - `UpdateTypeSpecificWidgetProperties` does the same copy for an existing widget.
   - `SaveWidgetAsync`'s switch statement.
8. **`Services/Features/CompanyFeatureCatalog.ForWidget`**: map the widget to its paid module, so the grid hides it for a location that doesn't have it switched on (switches are per location; see `CompanyFeatureService`). Leave it out for widgets everyone gets.

`src/Lanyard.Tests/Integration/WidgetDispatchCoverageTests.cs` checks steps 1, 3, 4, 5, 6 and 7 for every `WidgetType`:
- discriminator mapping
- a save through `CreateWidgetCopy`
- a catalog entry whose `Create()` builds the mapped subclass
- a renderer with a matching `Widget` parameter
- a configurator case when `HasSettings` is set

A missed step fails the tests rather than failing silently.

If the widget can show anything sensitive, remember a dashboard can be shown on an anonymous kiosk, which runs as the unfiltered "system" tenant. Gate it on a signed-in role, *and* have the service refuse system callers (see `ComplianceService`).

## Layout

- **`DashboardLayoutEngine`** (`LanyardServices/Services/Dashboards/`) is pure and unit tested (`DashboardLayoutEngineTests`). It owns all placement rules on the 12-column grid:
  - `Normalize`: legacy overlap repair, same behaviour as the old grid.
  - `Move` / `Resize`: the moved widget wins and anything it overlaps is pushed down. No compaction unless `compact: true`.
  - `Place`: a new widget, either at a position or in the first free spot.
  - `Compact`

  Don't put layout arithmetic in components; call the engine.
- Every widget also has these layout fields, copied by `UpdateCommonMutableWidgetProperties`. A new layout field must go there too, and **not** into `SaveWidgetAsync`, which saves settings only (it used to write the grid fields and saved unfinished layouts):
  - `ShowTitle` (shows `Title` as a header)
  - `HideOnPhone`
  - `PhoneOrder` (null means follow desktop order)
- Dashboards have two layout options, saved by `SaveDashboardAsync`:
  - `RowHeight`: 60–240px, default 120.
  - `FitToScreen`: rows stretch to fill the screen, applied on kiosks only.
- The grid collapses to one column using a **container** query (`@container (max-width: 768px)`), not a media query, so a narrow preview frame collapses the same way a phone does.

## Who can edit

Admins and Managers. `/manage/dashboards/{id}` is `[Authorize(Roles = "Admin,Manager")]`, and `DashboardService`'s write methods check the role themselves (`NotAllowedToEditMessage`), because dashboards also render on screens with nobody or anybody signed in.

## Debugging

- **A widget "saves but doesn't show up":** check its catalog entry and its renderer's `Widget` parameter type first.
- **A widget "doesn't save at all"** (the dashboard save fails with a generic toast): check `DashboardService.cs`'s three copy/update switches.
