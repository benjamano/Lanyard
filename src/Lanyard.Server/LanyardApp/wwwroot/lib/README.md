# Vendored libraries

Third-party files served from Lanyard itself rather than a CDN, so a page makes no requests to another
origin and the Content-Security-Policy (Program.cs) only allows `'self'` for scripts and styles.
Each file is the unmodified copy from the library's npm package; the version is in the folder name so a
browser never keeps an old copy after an upgrade.

| Folder | Package | Files | Used by |
|---|---|---|---|
| `quill/2.0.3` | [quill@2.0.3](https://www.npmjs.com/package/quill/v/2.0.3) `dist/` | `quill.js`, `quill.core.css`, `quill.snow.css` | Rich-text editors (Blazored.TextEditor) and the chat composer. `quill.core.css` is also needed wherever saved Quill HTML is shown read-only (`.ql-editor`), e.g. Quill 2 bullet lists are `<ol><li data-list="bullet">` |
| `imask/7.6.1` | [imask@7.6.1](https://www.npmjs.com/package/imask/v/7.6.1) `dist/` | `imask.min.js` | Fluent UI's masked inputs (FluentNumberInput, date/time inputs) read `window.IMask`; without it they try unpkg.com, which the CSP blocks |
| `bootstrap` | Bootstrap | `dist/css` | Global stylesheet |

SHA-256 of the vendored files:

```
f6157c72ac9b3f51cdead426335688a027b12405d9d6a4daadd38a676b2d7ff2  quill/2.0.3/quill.js
1ad491e90881eaae64cdbbc87e82613c98350907e89c00e77faa4316d92098e5  quill/2.0.3/quill.core.css
1c7948cd13aa92fac6390319bc1e5e461823da171519d3a768db56164f871636  quill/2.0.3/quill.snow.css
7fcd7b0428e446cb8fc10a9acde63838a2af5a1cf5e1d90596e326431e489610  imask/7.6.1/imask.min.js
```

To upgrade: download the package tarball (`https://registry.npmjs.org/<name>/-/<name>-<version>.tgz`),
copy the same files from `package/dist/` (plus `LICENSE`) into a new version folder, update the paths in
App.razor and the hashes above, then delete the old folder. Fluent pins the IMask version it
expects in its own bundle (`unpkg.com/imask@x.y.z`), so check that before changing IMask.
