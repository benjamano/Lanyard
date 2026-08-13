---
name: client-build-troubleshooting
description: Diagnoses Lanyard.Client failing to launch with a ".NET runtime not found" / "No frameworks were found" error, even when the correct .NET runtime is genuinely installed. Root cause is stray host DLLs left in the shared build\ output folder by Velopack packaging, not a missing runtime — use this whenever that specific error appears, before spending time reinstalling or repairing the .NET runtime.
---

# Client "No frameworks were found" — not actually a missing runtime

`Lanyard.Client` failing to launch with an error like:

```
Framework 'Microsoft.NETCore.App' 10.0.0 (x64)
.NET location: ...\build\
No frameworks were found
```

is **not** a missing-runtime problem, even though the error message reads exactly like one. Don't jump to reinstalling or repairing the .NET runtime — check the diagnostic tell first.

## Diagnostic tell

Look at the error's `.NET location:` line. If it points at the app's own `build\` output folder instead of `C:\Program Files\dotnet\`, this is the known issue below, not an actual missing install.

## Root cause

Stray self-contained runtime files — `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`, `clrjit.dll` — get left in the shared `build\` output folder. `Lanyard.Client`'s `OutputPath` is that shared `build\` folder (at the repo root), and the Velopack packaging step does a self-contained publish into it, which drops those host DLLs there. A later plain `dotnet build`/run inherits the leftovers: the framework-dependent apphost sees `hostfxr.dll` sitting right next to the exe and resolves the runtime *locally* from `build\` — which has no shared framework directory — instead of consulting the real install at `C:\Program Files\dotnet\`.

`Lanyard.App`'s own output folder is unaffected by this — it never receives those files, since it isn't part of the Velopack self-contained publish step.

## Fix

Delete the four host DLLs (`hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`, `clrjit.dll`) from `build\`, or just clean the whole `build\` folder and rebuild.
