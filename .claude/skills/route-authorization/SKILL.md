---
name: route-authorization
description: History and edge cases behind LanyardApp's default-deny route authorization gate (RouteAuthorizationGate.razor / Routes.razor), including why AuthorizeRouteView matters, the StaffNotFound exception, and App.razor head-script fragility precedent. Use whenever adding a new @page route, reviewing whether a page is correctly protected, or debugging why a page is (or isn't) reachable without logging in.
---

# Route Authorization

**The rule** (also stated in AGENTS.md/CLAUDE.md — repeating here since it's the one thing this skill must never let you forget): every new `@page` route needs either `@attribute [Authorize]` / `[Authorize(Roles = "...")]` (protected) or `@attribute [Microsoft.AspNetCore.Authorization.AllowAnonymous]` (intentionally public). There is no third option — an omitted attribute is not "public by default," it's a page that will redirect to login.

## Why this is enforced this way

`Components/Layout/RouteAuthorizationGate.razor` wraps `AuthorizeRouteView` and default-denies any page lacking an explicit `[AllowAnonymous]`, redirecting unauthenticated users to `/HandleLogin?ReturnUrl=...`.

This exists because of a real, previously-shipped bug: `Routes.razor` used to render a plain `<RouteView>` directly. Blazor's `[Authorize]` attribute only takes effect when rendered through `AuthorizeRouteView` — with a plain `RouteView`, every `@attribute [Authorize(...)]` in the app was silently dead code, and any page (including `/manage/users`, `/manage/roles`) was reachable by an anonymous visitor who simply navigated to the URL. `RouteAuthorizationGate` is the fix, and it flips the default from allow to deny so a *missing* attribute fails safe instead of failing open.

## Edge case to know about

`StaffNotFound.razor` (`/not-found`) must stay `[AllowAnonymous]`. `UseStatusCodePagesWithReExecute("/not-found")` re-executes ordinary 404s through this same route — including benign ones like a missing `aspnetcore-browser-refresh.js` request during non-watch dev runs — so gating it behind auth would turn routine 404s into login redirects.

## Related fragility precedent

`App.razor`'s head scripts have broken things before in adjacent ways: a previously-loaded FontAwesome kit script and its CSP allowances (`kit.fontawesome.com`/`ka-f.fontawesome.com` in `script-src`, `font-src`, `connect-src`) were removed after turning out to be dead weight — loaded app-wide but never referenced by any `.razor` file, since everything actually uses FluentUI's own `Icons.Regular/Filled.SizeXX` icon set. If FontAwesome icons are ever genuinely needed again, both the kit script and all three CSP host allowances need to come back together, or icons will silently fail to load (CSP violations are easy to miss without checking the browser console). This is the same general lesson as the boot-cloak coupling in the `fluentui-v5-blazor` skill: `App.razor`'s head is a small, easy-to-break shared surface — check what depends on a script before removing it, and check the console after adding one.
