---
name: dashboard-widgets
description: Checklist for adding a new dashboard widget type in LanyardApp — touches 6 separate places with no compiler enforcement tying them together, so missing one causes either a runtime EF error or a widget that saves fine but silently renders blank. Use whenever adding a new widget type to the dashboard system, or debugging a widget that isn't rendering/configuring correctly despite the data looking right.
---

# Adding a dashboard widget type

Widget type dispatch in this repo is pure manual `switch`/enum matching — there is no registry, factory, or interface contract enforcing that every widget type is wired up everywhere it needs to be. Adding a new type means touching all of the following, and missing any one of them fails in a way that's easy to misdiagnose:

1. **`Lanyard.Infrastructure/Enum/WidgetType.cs`** — add the new enum value.
2. **`Lanyard.Infrastructure/Models/DashboardModels.cs`** — add a subclass of `DashboardWidget` with a `[SetsRequiredMembers]` constructor that sets `Type` and a default grid size.
3. **`ApplicationDbContext.cs`'s `HasDiscriminator(x => x.Type).HasValue<...>()` chain** (TPH mapping) — add the new subclass here, and generate/apply an EF migration. **Miss this and you get a runtime EF discriminator error**, not a compile error — the new subclass looks fine in isolation.
4. **`EditDashboard.razor`** — add a `FluentMenuItem` that does `Dashboard.Widgets.Add(new XWidget())` so a user can actually create one.
5. **`DashboardGrid.razor`** — two *separate* `switch` blocks that must both be updated:
   - The render markup's switch (cast the widget, render `<RenderXWidget Widget="..." />`).
   - The `ShowWidgetConfigDialog` switch (cast the widget, open `ConfigureXDialog`).
   **Miss either one and the widget saves fine, appears in the dashboard's widget list, but renders blank (or won't open a config dialog) with no error anywhere** — this is the failure mode most likely to eat debugging time, since everything upstream (the enum, the model, the DB row) looks correct.
6. **New `RenderXWidget.razor` component** — informal contract, not an enforced interface: `[Parameter] public required XWidget Widget`, and usually `[Parameter] public bool AllowTrigger`. Plus a matching `ConfigureXDialog.razor` if the widget has configurable settings.

When debugging a widget that "saves but doesn't show up," check step 5 first (both switches in `DashboardGrid.razor`) before assuming the problem is in the data layer — the data almost always round-trips correctly since the EF discriminator (step 3) would have thrown loudly if that part were broken.
