import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));

interface FixtureIds {
  dashboardId: string;
  playlistId: string;
  courseAssignmentId: string;
  dmxClientId: string;
}

let cached: FixtureIds | undefined;

/**
 * Reads the ids globalSetup.ts's .NET fixture seeder wrote out, lazily (not at module-load time)
 * so it's safe to import this from a spec file regardless of whether globalSetup has run yet -
 * Playwright guarantees globalSetup completes before any test body executes.
 */
export function getFixtureIds(): FixtureIds {
  cached ??= JSON.parse(
    readFileSync(path.resolve(here, ".fixture-ids.generated.json"), "utf-8"),
  ) as FixtureIds;
  return cached;
}
