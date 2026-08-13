---
name: fluentui-v5-blazor
description: Fluent UI Blazor v5 component, CSS, and theming gotchas specific to this repo — component names that changed from v4, the FOUC boot-cloak mechanism in App.razor, why scoped .razor.css rules silently no-op on Fluent* component roots, FluentDataGrid row sizing, and theme-mode desync history. Use this whenever writing or debugging .razor markup or .razor.css files that touch any Fluent* component (FluentStack, FluentDataGrid, FluentCard, FluentTextInput, etc.), even if the user's request doesn't mention "Fluent UI" or "v5" by name — e.g. "the sidebar isn't full width", "my CSS class isn't applying", "add a numeric input", "the grid looks cramped", "dark mode looks wrong after refresh".
---

# Fluent UI Blazor v5 — Known Gotchas

This repo pins a Fluent UI Blazor v5 prerelease. Several of its behaviors differ from v4 or from what plain Blazor CSS isolation would lead you to expect — the details below were root-caused the hard way (Playwright DOM inspection, not guessing from source), so check here before re-debugging them from scratch.

Always verify current component names/parameters via the `fluent-ui-blazor` MCP (`search_components` / `get_component_details`) before writing markup — v5 renamed things v4 users expect. Example: the numeric input is `FluentNumberInput<TValue>`, not `FluentNumberField` (which doesn't exist). Generic components need a backtick suffix for `get_component_details`, e.g. `` FluentNumberInput`1 ``.

## The FOUC boot cloak in `App.razor`

Fluent UI Blazor v5 **computes its design tokens in JavaScript, not CSS** — `--colorNeutralBackground1` and friends are referenced by the component library's scoped CSS but defined nowhere in any `.css` file. They're computed from a brand ramp in the library's JS bundle and pushed into `document.adoptedStyleSheets` at the initializer's `afterStarted` hook. The body reset (margin, height, fonts) is a `<link id="default-fuib-css">` the same JS injects at runtime.

This means a Flash Of Unstyled Content on refresh can't be fixed by loading CSS faster or adding critical CSS — with prerendering on, the browser paints HTML whose style values are undefined until that JS runs. The only fix is to hide the intermediate state, which is what `Components/App.razor`'s cloak does: an inline `<style>`/`<script>` sets `data-app-booting` on `<html>` (hiding `body` via `visibility`), shows a splash element, and only reveals once **both** `customElements.get('fluent-button')` is defined **and** `document.getElementById('default-fuib-css').sheet` is non-null. Both conditions matter — custom elements are defined at `beforeStart` but styles apply at `afterStarted`, so checking only the custom element un-cloaks mid-flash. There's a timeout escape hatch so the page can't get stuck blank if scripting fails.

**On any Fluent package version bump**, re-verify the `default-fuib-css` element id and the `fluentui-blazor:theme-settings` localStorage key (`{mode:"dark"|"light"|"system"}`) still exist under those names — both are undocumented internals, not public API.

## Scoped CSS doesn't reach Fluent component roots by default

A `Class="my-class"` parameter passed directly to a **FluentUI component** (`FluentCard`, `FluentGrid`, `FluentStack`, `FluentSelect`, `FluentDataGrid`, ...) does **not** receive the file's `b-xxxxxxxx` Blazor scope attribute on its rendered root element. A plain-scoped selector (`.my-class { ... }`) compiles to `.my-class[b-xxxxxxxx]` and silently never matches — the class is visibly present in the DOM, everything looks correctly wired in source, and the rule just doesn't apply. A plain `<div class="my-class">` authored directly in the same file *does* get the scope attribute normally; this only affects component tags.

Fix:
1. Use `::deep .my-class { ... }` whenever `.my-class` targets a Fluent component instead of a plain element.
2. `::deep` still needs an **ancestor** in the same file that carries the scope attribute. If the Fluent component is the only element in the file, wrap it in a plain `<div class="some-wrapper">` first — that div reliably carries the scope attribute and gives `::deep` something to match against.
3. Don't trust "the rule exists in the stylesheet" — verify with devtools/Playwright (`getComputedStyle`, `element.matches(...)`).

A few concrete traps that follow from this and cost real debugging time:
- **`FluentStack`'s default `align-items` is `start`, not `stretch`.** A plain child `<div>` shrinks to its own content width instead of filling the stack's cross-axis, even though the stack itself is full-width. Give any child that needs to fill the row an explicit `width: 100%`.
- **`Class` can land on a different real element than expected, or an empty one.** e.g. `FluentRadioGroup`'s `Class` renders onto a wrapping `<fluent-field>`, while the actual `<fluent-radio-group>` element gets an empty `class=""`. A "fill the width" fix needs `::deep` applied at every layer of the real DOM chain, not just the component the `Class` was set on — inspect the rendered DOM rather than assuming the parameter lands where you'd guess.
- **`fluent-text-input` has a built-in `max-width: 400px`** that silently caps width even after `width: 100%` is applied. Pair it with an explicit `max-width: none` when a text input needs to fill a wide container.
- **`FluentDataGrid` with an explicit `GridTemplateColumns`** assigns each column a fixed `grid-column` line number. Hiding a column via `display:none` doesn't renumber siblings — you get a gap — unless the override also puts `0px` at that column's original position (e.g. 5 columns, hiding #3 → `Xpx Ypx 0px Zpx Wpx`). Grids using `Virtualize="true"` render with `grid-column: auto` instead and need the *opposite* fix: a compacted list of only the visible columns' sizes, no `0px` placeholders. Check `getComputedStyle(cell).gridColumn` (`"auto"` vs a number) to know which kind of grid you're dealing with before picking a fix.

## Two more width/sizing traps (found building the Training course editor)

- **`FluentRadioGroup` nests a `<fluent-field>` per option**, each containing a real `<label slot="label">`. A "radio row fills the width" fix needs `::deep width: 100%` applied at *every* layer of the actual chain: the plain-div host → outer `fluent-field` → `fluent-radio-group` → per-option `fluent-field` → and again for whatever's inside the label (e.g. a nested `FluentTextInput`, itself another `fluent-field` → `fluent-text-input`). Walk the real DOM in devtools/Playwright rather than guessing how many layers deep the fix needs to go.
- **A `Blazored.TextEditor` (Quill) wrapper's content-based auto-height reproducibly undercounts by exactly the toolbar's own height**, regardless of `display: block`, `flow-root`, or `height: max-content` — this was never fully root-caused (margins/padding confirmed zero at every level, no floats, no absolute positioning). Don't keep debugging a "size to content" approach if a Quill wrapper turns up mysteriously short — go straight to the reliable fix: give the wrapper an authoritative fixed `height` with `display: flex; flex-direction: column`, the toolbar `flex: 0 0 auto`, and the editor container `flex: 1 1 auto; min-height: 0`.

## `FluentDataGrid` row spacing

When a column renders a `FluentButton`/icon inside a `TemplateColumn`, the row can look cramped against its own top/bottom border. Set `RowSize="@DataGridRowSize.Medium"` (or `.Large`) on the grid itself — this is a first-class grid parameter, and it beats hand-rolled padding/CSS on the cell content, which fights the grid's own row-height calculation.

## Theme mode can desync from what FluentUI actually renders

`App.razor`'s boot-cloak script and FluentUI's own `IThemeService` are two independent mechanisms that both read/write the `fluentui-blazor:theme-settings` localStorage key. `MainLayout.razor` calls `IThemeService.SetThemeAsync` with an explicit `ThemeMode` (read from storage via `window.lanyardGetStoredThemeMode()`) — this exists specifically to guard against a previously-fixed bug where an ambiguous `SetThemeAsync(color, isExact)` overload silently re-derived "current effective mode" (racy on first render) and overwrote the saved dark/light choice on every page load.

**If theme/mode bugs resurface**, check first for a call site invoking a `SetThemeAsync` overload without an explicit `ThemeMode` — that's what caused it before. Don't reintroduce a custom `IThemeService`; the real one is already registered via `AddFluentUIComponents()` in `Program.cs` (v5 removed `<FluentDesignTheme>`/`<FluentDesignSystemProvider>` entirely — theming is CSS-variable-based now, not component-based).

## Charts

Fluent's own charting package has separate gotchas (including an MCP documentation gap) — see the `charting` skill rather than duplicating that here.
