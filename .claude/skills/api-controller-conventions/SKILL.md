---
name: api-controller-conventions
description: Real inconsistencies across src/Lanyard.Server/LanyardAPI/Controllers that a new controller would silently inherit if copy-pasted from the wrong example — a namespace mismatch, three different coexisting auth mechanisms with no doc on when to use which, and no shared Result<T>-to-HTTP-status helper. Use whenever adding a new API controller/endpoint, or deciding which existing controller to use as a template.
---

# API controller conventions (and inconsistencies to know about)

`src/Lanyard.Server/LanyardAPI/Controllers` doesn't have a single settled convention — the existing controllers disagree with each other in ways worth knowing before picking one to copy as a template.

## Namespace mismatch — likely a bug, don't copy it

`AuthController.cs` is `namespace Lanyard.App.Controllers`. `FilesController.cs`, `MusicController.cs`, and `CompanyBrandingController.cs` — all in the same folder — are `namespace Lanyard.API.Controllers`. This looks like an oversight from whenever `AuthController` was created or moved, not an intentional distinction. **Use `Lanyard.API.Controllers` for any new controller** — copying `AuthController` as a template would silently propagate the wrong namespace.

## Route convention

Most controllers use `[Route("api/[controller]")]` (the default convention-based route). `CompanyBrandingController` deviates with a hardcoded `[Route("api/companies")]` — this is intentional (see below), not a mistake to fix.

## Three different auth mechanisms coexist — know which applies

1. **Declarative role-based**: `[Authorize(Roles = "Admin")]` on individual actions (`FilesController`'s admin-only actions). Use this for staff/admin-only endpoints reached from the authenticated Blazor app.
2. **`[AllowAnonymous]` with an explicit written rationale**: `CompanyBrandingController.GetLogo` is deliberately anonymous (public logo asset), but the code comments explicitly document *why* it's safe — it only accepts a `companyId` (never a raw file id) and resolves the actual file server-side, so it can only ever serve what an admin explicitly designated as that company's public logo. It also restricts content-type to a raster allowlist (`image/png`/`jpeg`/`gif`/`webp`) specifically because serving an SVG anonymously would let embedded `<script>` execute if the URL is navigated to directly — the client-side `Accept="image/*"` hint on the uploader isn't a real gate. **If you add another anonymous endpoint, write the same kind of explicit rationale comment** — don't add `[AllowAnonymous]` without one.
3. **Bespoke in-body check**: `ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator)`, called manually inside action bodies (`MusicController`, `FilesController`'s list/download routes). This is for the **kiosk client**, not staff users — it validates the shared-secret query param, not a cookie/JWT. Use this pattern specifically for endpoints the kiosk client itself calls, not for staff-facing endpoints (those should use `[Authorize]`).

There's no single doc tying these three together — when adding an endpoint, identify which caller it's for (staff via the Blazor app, the anonymous public, or the kiosk client) and pick the matching mechanism above rather than defaulting to whichever one the nearest existing action happens to use.

## No shared `Result<T>` → HTTP status helper

Every controller hand-rolls the mapping from `Result<T>` to an `IActionResult` — there's no `.ToActionResult()` extension or similar. Patterns seen: `if (!result.Success) return BadRequest(result); return Ok(result);` (returning the raw `Result<T>` as the response body), and elsewhere ad hoc anonymous objects like `new { message = ... }` instead of the `Result<T>` wrapper (`AuthController`). There's no single correct answer documented here — but be aware the response *shape* isn't consistent across endpoints, so don't assume every API response looks like `{ isSuccess, data, error }` when writing a client against one of these endpoints; check the specific controller.

No API versioning exists anywhere in the project — consistent, at least, in its absence; don't introduce versioning for a single new endpoint without a broader decision to do so.
