import { existsSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

// publish-screenshots.sh expects a manifest of { file, viewport, caption } - it doesn't know
// anything about Playwright's own test-results layout, so this walks that layout (one
// <spec>-<test-title>-<project>/ folder per failed assertion, each holding
// <name>-expected.png/-actual.png/-diff.png) and turns every failure into a baseline/actual/diff
// triple, run from the CI workflow's "on failure" step. See the visual-regression-testing skill.
const here = path.dirname(fileURLToPath(import.meta.url));
const testResultsDir = path.resolve(here, "test-results");

const manifest = [];

function walk(dir) {
  for (const entry of readdirSync(dir)) {
    const full = path.join(dir, entry);
    if (statSync(full).isDirectory()) {
      walk(full);
    } else if (entry.endsWith("-diff.png")) {
      const base = entry.slice(0, -"-diff.png".length);
      const dirName = path.basename(dir);
      const viewport = dirName.endsWith("-phone") ? "phone" : "desktop";
      const label = dirName.replace(/-(phone|desktop)$/, "").replace(/-/g, " ");

      for (const [suffix, kind] of [
        ["-expected.png", "baseline"],
        ["-actual.png", "actual"],
        ["-diff.png", "diff"],
      ]) {
        const file = path.join(dir, `${base}${suffix}`);
        if (existsSync(file)) {
          manifest.push({ file, viewport, caption: `${label} - ${kind}` });
        }
      }
    }
  }
}

if (existsSync(testResultsDir)) {
  walk(testResultsDir);
}

process.stdout.write(JSON.stringify(manifest, null, 2));
