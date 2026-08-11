# Continuous Integration

The repository [CI workflow](../../.github/workflows/ci.yml) runs on pushes, pull requests, and manual dispatch. It supplies Docker, real server databases, and Chromium when a development machine does not have them.

## Jobs

Three jobs run independently:

1. **Build and unit tests** installs .NET 10 and Node.js 24, restores and builds the backend and frontend, audits npm dependencies, and runs tests that do not require Docker.
2. **PostgreSQL, MySQL, and MariaDB contracts** sets `STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS=1`; Testcontainers starts PostgreSQL 17, MySQL 8.4, and MariaDB 11.4 and runs the same migration, persistence, lease, recovery, and canonical-commit contracts.
3. **Production container and browser smoke test** builds the real Dockerfile, starts it with a read-only root filesystem and dropped capabilities, verifies health and system endpoints, checks that the running image reports the commit that built it, checks that a forwarded header from a peer nothing trusts is refused and reported, and uses Chromium to exercise administrator sign-in, Provider configuration, PDF upload, parsing, and the administration area.

A fourth waits for all three:

4. **Publish image to GitHub Container Registry** builds the same Dockerfile and pushes it to `ghcr.io`. It runs only for pushes to `main` and `v*` tags, never for a pull request. See [Published Images](#published-images).

TRX results, Playwright HTML reports, screenshots, failure traces/videos, and container logs are uploaded as Actions artifacts. Temporary administrator credentials exist only in the isolated runner environment and are not repository secrets or production defaults.

## Published Images

`ghcr.io/philfanzhou/structadoc` receives `latest` from the default branch, `sha-<commit>` for every published commit, and `<version>` plus `<major>.<minor>` from a `v*` tag.

Releasing is one push. `git tag -a v0.1.0 && git push origin v0.1.0` is what turns the semver rules on; nothing else in the workflow distinguishes a release. The tag also supplies the `VERSION` build argument, so the version the registry advertises and the version the running service reports come from the same place and cannot drift. A push to the default branch leaves the placeholder `Directory.Build.props` declares.

Both image-building jobs pass `SOURCE_REVISION`, and the container job then asks the running image for it. This is checked rather than assumed because nothing inside the image can derive it: `.dockerignore` excludes `.git`, so the SourceLink the SDK ships finds no repository and stamps nothing. A build argument that quietly stopped being passed would leave every deployment unable to say which commit it is, while every health check still passed.

Publishing waits on all three test jobs, so a tag in the registry is an image that built, started under the production security flags, answered readiness, and served a browser flow. An image that failed any of that is never pushed, which is what keeps `latest` from being the least tested thing in the registry.

The push is authenticated with the workflow's own `GITHUB_TOKEN` rather than a stored credential; no registry secret exists in this repository. Each image carries a signed build provenance attestation naming the workflow, commit, and build parameters that produced it, and an SBOM. `gh attestation verify oci://ghcr.io/philfanzhou/structadoc:latest --owner philfanzhou` checks one against this repository.

The container job still builds the image itself rather than pulling this one. It runs in parallel with publishing, and a smoke test that depended on a registry would be testing a previous commit's artifact.

## What Covers Parsing

Parsing is the part of the product that is hardest to reach from a test, and the coverage is split deliberately:

- `ParseRunExecutorTests` substitutes the Provider and covers the orchestration around it: conversion, checkpoints, deadlines, and the failure classes.
- `ParseExecutionEndToEndTests` substitutes nothing below the public API. It starts a MinerU-shaped HTTP server on a real socket, configures a real `mineru-local` Provider against it, and lets the resident Worker carry a Parse Run from `queued` to `succeeded`. It then reads the Blocks, Pages, Assets, Artifacts, Markdown, and a ZIP export back through the API. This is what exercises the real HTTP adapter, the archive download, normalization, and the canonical commit together.
- The browser job runs the production image with `Worker__ExecutionEnabled=true` and points a Provider at an address the Host itself refuses. That covers what is specific to the image: the resident Worker starts, claims a queued run, calls out over HTTP, records a final status, and the workspace shows it without anyone pressing refresh.

What no test covers is a real MinerU service. Nothing in CI can supply one, so the first real parse remains a deployment step rather than a verified one; everything up to the Provider's own behaviour is verified.

## Current Remote Status

The latest `main` run at the time of this update, workflow run `31369943829` for commit `9fc05ce`, completed successfully across all four jobs and published the first image to the registry.

One preceding run remains visible as a failure in Actions history and is superseded rather than still active: run `31324328532` for commit `4fced2a` failed the container job on a read-only `/app/data`, because the image shipped its own defaults file and then ignored it. Commit `b2ea5c2` fixed the precedence and the next run passed.

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

Browser tests default to `http://127.0.0.1:8080`. Override it with `STRUCTADOC_E2E_BASE_URL`; inject the test administrator through `STRUCTADOC_E2E_ADMIN_USERNAME` and `STRUCTADOC_E2E_ADMIN_PASSWORD`.

## Interpretation Rules

- Local build success does not substitute for the real database or production-container jobs.
- A workflow definition is not proof that its jobs passed; use the run associated with the current commit.
- Do not weaken tests or suppress package audits to make CI green.
- Preserve failure artifacts long enough to diagnose database, browser, and container regressions.
