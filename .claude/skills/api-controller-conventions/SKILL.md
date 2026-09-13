---
name: api-controller-conventions
description: Real inconsistencies across src/Lanyard.Server/LanyardAPI/Controllers that a new controller could silently get wrong if copied from the wrong example — three different coexisting auth mechanisms with no doc on when to use which, and which controllers should (and should not) use the shared Result<T>-to-HTTP-status helper. Use whenever adding a new API controller/endpoint, or deciding which existing controller to use as a template.
---

# API controller conventions (and inconsistencies to know about)

`src/Lanyard.Server/LanyardAPI/Controllers` doesn't have a single settled convention — the existing controllers disagree with each other in ways worth knowing before picking one to copy as a template.

## Namespace mismatch — resolved

`AuthController.cs` used to declare `namespace Lanyard.App.Controllers`, inconsistent with every other controller in the same folder (`Lanyard.API.Controllers`). This has been fixed — all controllers now use `Lanyard.API.Controllers`. **Use `Lanyard.API.Controllers` for any new controller.**

## Route convention

Most controllers use `[Route("api/[controller]")]` (the default convention-based route). `CompanyBrandingController` deviates with a hardcoded `[Route("api/companies")]` — this is intentional (see below), not a mistake to fix.

## Three different auth mechanisms coexist — know which applies

1. **Declarative role-based**: `[Authorize(Roles = "Admin")]` on individual actions (`FilesController`'s admin-only actions). Use this for staff/admin-only endpoints reached from the authenticated Blazor app.
2. **`[AllowAnonymous]` with an explicit written rationale**: `CompanyBrandingController.GetLogo` is deliberately anonymous (public logo asset), but the code comments explicitly document *why* it's safe — it only accepts a `companyId` (never a raw file id) and resolves the actual file server-side, so it can only ever serve what an admin explicitly designated as that company's public logo. It also restricts content-type to a raster allowlist (`image/png`/`jpeg`/`gif`/`webp`) specifically because serving an SVG anonymously would let embedded `<script>` execute if the URL is navigated to directly — the client-side `Accept="image/*"` hint on the uploader isn't a real gate. **If you add another anonymous endpoint, write the same kind of explicit rationale comment** — don't add `[AllowAnonymous]` without one.
3. **Bespoke in-body check**: `ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator)`, called manually inside action bodies (`MusicController`, `FilesController`'s list/download routes). This is for the **kiosk client**, not staff users — it validates the shared-secret query param, not a cookie/JWT. Use this pattern specifically for endpoints the kiosk client itself calls, not for staff-facing endpoints (those should use `[Authorize]`).

There's no single doc tying these three together — when adding an endpoint, identify which caller it's for (staff via the Blazor app, the anonymous public, or the kiosk client) and pick the matching mechanism above rather than defaulting to whichever one the nearest existing action happens to use.

## `Result<T>` → HTTP status: use `ToActionResult()` for the plain Ok/Fail case

For the common case — success returns `200` with the `Result<T>` as the body, failure returns `400` with the same `Result<T>` as the body — use `result.ToActionResult()` (`Lanyard.API.Extensions.ResultActionResultExtensions`, `src/Lanyard.Server/LanyardAPI/Extensions/ResultActionResultExtensions.cs`) instead of hand-rolling `if (!result.Success) return BadRequest(result); return Ok(result);`. `FilesController` uses this for all of its actions.

This helper is **not** for endpoints with a deliberate "don't leak error details" posture. `CompanyBrandingController` and `TrainingCertificatesController` collapse every failure to a bare `NotFound()` regardless of what `Result.Error` says, each with an explicit comment explaining why (not found / not yours / not finished yet must stay indistinguishable). `MusicController` uses a related but distinct variant, `NotFound("Audio file not found.")` — a fixed literal, not the `Result.Error` text either. Keep hand-rolling `NotFound()` (or a fixed literal) for endpoints like these — `ToActionResult()` would leak the real error and undo an intentional decision.

`AuthController` also stays out of scope — its login/logout flows return ad hoc anonymous objects (`new { message = ... }`) rather than a `Result<T>` wrapper at all.

So the response *shape* still isn't fully uniform across every endpoint — check the specific controller before assuming any given endpoint returns `{ isSuccess, data, error }`, but for a *new* endpoint with no reason to hide error detail, prefer `ToActionResult()` over reinventing the mapping.

No API versioning exists anywhere in the project — consistent, at least, in its absence; don't introduce versioning for a single new endpoint without a broader decision to do so.
