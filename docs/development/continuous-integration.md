# Continuous Integration

The repository [CI workflow](../../.github/workflows/ci.yml) runs on pushes, pull requests, and manual dispatch. It supplies Docker, real server databases, and Chromium when a development machine does not have them.

## Jobs

The workflow has three independent jobs:

1. **Build and unit tests** installs .NET 10 and Node.js 24, restores and builds the backend and frontend, audits npm dependencies, and runs tests that do not require Docker.
2. **PostgreSQL, MySQL, and MariaDB contracts** sets `STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS=1`; Testcontainers starts PostgreSQL 17, MySQL 8.4, and MariaDB 11.4 and runs the same migration, persistence, lease, recovery, and canonical-commit contracts.
3. **Production container and browser smoke test** builds the real Dockerfile, starts it with a read-only root filesystem and dropped capabilities, verifies health and system endpoints, and uses Chromium to exercise administrator sign-in, PDF upload, the user workspace, and the administration area.

TRX results, Playwright HTML reports, screenshots, failure traces/videos, and container logs are uploaded as Actions artifacts. Temporary administrator credentials exist only in the isolated runner environment and are not repository secrets or production defaults.

## Current Remote Status

The latest `main` run at the time of this update, workflow run `31185614899` for commit `5ef2523`, completed successfully across all three jobs.

Two preceding runs remain visible as failures in Actions history. They are superseded rather than still-active failures:

- run `31183755280` exposed real server-database contract and browser-workspace defects;
- run `31185224410` confirmed the image and ordinary tests but still failed the database job and the post-login browser flow;
- commit `5ef2523` fixed the remaining browser issue by refreshing the antiforgery token after the authenticated principal changed, and the next run passed.

Historical red runs should not be mistaken for the current branch status. Always compare the run's head SHA with `origin/main` and inspect the newest run.

## Local Reproduction

Without Docker:

```bash
cd web
npm ci
npm run build
npm run test:e2e -- --list
cd ..
dotnet test StructaDoc.slnx
```

With a Docker-compatible engine, run the real database contracts:

```bash
STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS=1 \
dotnet test tests/StructaDoc.DatabaseContractTests/StructaDoc.DatabaseContractTests.csproj
```

PowerShell:

```powershell
$env:STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS = '1'
dotnet test tests/StructaDoc.DatabaseContractTests/StructaDoc.DatabaseContractTests.csproj
```

Browser tests default to `http://127.0.0.1:8080`. Override it with `STRUCTADOC_E2E_BASE_URL`; inject the test administrator through `STRUCTADOC_E2E_ADMIN_EMAIL` and `STRUCTADOC_E2E_ADMIN_PASSWORD`.

## Interpretation Rules

- Local build success does not substitute for the real database or production-container jobs.
- A workflow definition is not proof that its jobs passed; use the run associated with the current commit.
- Do not weaken tests or suppress package audits to make CI green.
- Preserve failure artifacts long enough to diagnose database, browser, and container regressions.
