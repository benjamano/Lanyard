# Contributing to Lanyard

Thanks for your interest in contributing! This document covers the basics of getting a local
environment running and submitting a change.

## Local Development Setup

1. **Prerequisites**: .NET 10 SDK and Docker (for the local Postgres database).

2. **Clone the repository and restore dependencies:**

   ```bash
   git clone https://github.com/benjamano/Lanyard.git
   cd Lanyard
   dotnet restore
   ```

3. **Start the local database** (a throwaway PostgreSQL container — no shared or remote
   credentials needed):

   ```bash
   docker compose up -d
   ```

4. **Apply migrations:**

   ```bash
   dotnet ef database update \
     --project src/Lanyard.Infrastructure \
     --startup-project src/Lanyard.Server/LanyardApp
   ```

5. **Run the server:**

   ```bash
   dotnet run --project src/Lanyard.Server/LanyardApp
   ```

   Or use the **Debug LanyardApp** launch config in VS Code, which starts the database
   container automatically.

See `README.md` for more detail on project structure and running the Lanyard Client, and
`AGENTS.md` for the full contributor/agent operating manual (architecture, coding conventions,
and testing expectations).

## Building and Testing

Before submitting a change, make sure it builds and the test suite passes:

```bash
dotnet build LanyardApp.sln
dotnet test src/Lanyard.Tests/Lanyard.Tests.csproj
```

## Submitting a Pull Request

1. Create a branch off `dev` (not `main`, which is release-only):
   - `F-Your-Feature-Name` for new features
   - `fix/your-bug-description` for bug fixes
2. Make your changes, following the conventions in `AGENTS.md`.
3. Add or update tests for any behavior change.
4. Ensure the build and test suite pass (see above).
5. Push your branch and open a pull request against `dev`, describing what changed and why.
6. Address any review feedback. A maintainer will merge once the PR is approved.

<!-- test: Claude GitHub App dev-merge capability test, safe to ignore -->
