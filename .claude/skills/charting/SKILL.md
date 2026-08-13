---
name: charting
description: Charting rules for this repo — Fluent UI Charts (Microsoft.FluentUI.AspNetCore.Components.Charts) is the ONLY charting library to use. Radzen.Blazor was fully migrated away from and must never be reintroduced. Covers the real components in use (FluentHorizontalBarChart, FluentDonutChart), how data colors are sourced from ChartPalette/BrandConstants via DataVizPalette.Custom, and where to find docs since the fluent-ui-blazor MCP server doesn't index the Charts package. Use whenever adding, fixing, or restyling any chart or dashboard visualization — bar, donut, funnel, gantt — regardless of which library a user or old doc mentions.
---

# Charting: Fluent UI Charts only

**This repo uses `Microsoft.FluentUI.AspNetCore.Components.Charts` exclusively.** Radzen.Blazor was fully migrated away from — there are zero references to Radzen left in `src/` (confirmed by grep), and it must not be reintroduced. If a task, an old doc, a memory note, or search results mention Radzen, `.rz-*` classes, or `radzen-charts-overrides.css`, treat that as historical/stale — the current and only correct approach is Fluent Charts.

## Package

`Microsoft.FluentUI.AspNetCore.Components.Charts`, referenced in `src/Lanyard.Server/LanyardApp/Lanyard.App.csproj` at the same version as the core `Microsoft.FluentUI.AspNetCore.Components` package (currently `5.0.0-rc.5-26219.1` — keep them in sync on upgrades). Available chart types include donut, funnel, gantt, and horizontal bar (with an axis variant). If a dashboard needs a type outside that set (e.g. line, area, vertical bar), check the package's current docs (see below) before assuming it's unsupported — the set has grown before.

## Components actually in use

- `Components/Charts/RankedBarChart.razor` → `<FluentHorizontalBarChart ChartData="@SeriesData" Variant="HorizontalBarChartVariant.SingleBar" />`, built from `HorizontalBarChartSeries` / `HorizontalBarChartDataPoint`.
- `Components/Charts/CategoryDonutChart.razor` → `<FluentDonutChart ChartData="@PlottableData" InnerRadius="55" />`, built from `DonutDataPoint`.

Both follow the same shape: filter zero-value entries before binding (a 0-value bar/slice isn't visible anyway), map each data point to the chart's `DataPoint`/`Series` type, and size with `Style="width:100%;height:100%;"` on the component itself rather than a wrapping CSS file.

## Data colors

Chart colors are never left to the library's default palette — they're passed explicitly via `Color = DataVizPalette.Custom` plus `CustomColor = <hex>`, where the hex values come from `Components/Charts/ChartPalette.cs`, which in turn sources every value from `Lanyard.Infrastructure.Branding.BrandConstants` (the app's single validated, colorblind-safe palette). When adding a new chart, follow this same chain — pull colors from `ChartPalette` (adding a new named constant there if needed), don't hardcode hex values in the chart component and don't rely on Fluent's built-in categorical palette directly.

Unlike Radzen, Fluent Charts automatically follow the app's Fluent theme tokens (palette, dark/light, density) for everything *except* these explicit custom data colors — no manual theme CSS file is needed or should be added.

## Where to find docs

**The `fluent-ui-blazor` MCP server does not index this package** — `search_components`/`search_documentation` return nothing for "chart"/"donut"/"funnel" regardless of the MCP server's version (re-confirmed current), because it only documents the core component package. Don't waste time re-querying it for Charts. Use **Context7** with library ID `/websites/fluentui-blazor-v5_azurewebsites_net` instead — that source documents the Charts package with working parameter tables and examples. Before relying on its docs, confirm the project's `Microsoft.FluentUI.AspNetCore.Components` PackageReference version matches what's actually installed via `check_project_version`, since the two can drift.
