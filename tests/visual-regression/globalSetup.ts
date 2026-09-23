import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));

// Runs once before the whole suite. Reuses the real Lanyard.Infrastructure EF models (via a
// standalone .NET console utility) to seed the deterministic fixture data the "populated" screens
// need, rather than duplicating table/column knowledge in raw SQL from the JS side. See
// src/Lanyard.VisualTestFixtures/Program.cs and the visual-regression-testing skill.
export default function globalSetup(): void {
  const fixtureProjectPath = path.resolve(
    here,
    "../../src/Lanyard.VisualTestFixtures/Lanyard.VisualTestFixtures.csproj",
  );
  const fixtureIdsOutputPath = path.resolve(here, ".fixture-ids.generated.json");

  execFileSync(
    "dotnet",
    ["run", "--project", fixtureProjectPath, "--", fixtureIdsOutputPath],
    { stdio: "inherit" },
  );
}
