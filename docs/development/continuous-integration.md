# Continuous Integration

The repository [CI workflow](../../.github/workflows/ci.yml) runs on pushes, pull requests, and manual dispatch. It supplies Docker, real server databases, and Chromium when a development machine does not have them.

## Jobs

Six jobs run independently:

1. **Web dependency audit** runs `npm audit` against `web/package-lock.json`. It is its own job so that an advisory published upstream cannot end the run before anything has been built.
2. **Build and unit tests** installs .NET 10 and Node.js 24, restores and builds the backend and frontend, and runs tests that do not require Docker.
3. **LibreOffice legacy format integration** installs the same LibreOffice no-GUI components as the production image, enables the environment-gated integration test, and converts checked-in real DOC, XLS, and PPT fixtures through the production converter and process runner.
4. **Generated consumer SDK** starts the built Host, exports the consumer OpenAPI document, runs the pinned OpenAPI Generator C# client generator, and compiles the generated project. This catches schemas that are valid OpenAPI but unusable by the supported generator.
5. **PostgreSQL, MySQL, and MariaDB contracts** sets `STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS=1`; Testcontainers starts PostgreSQL 17, MySQL 8.4, and MariaDB 11.4 and runs the same migration, persistence, lease, recovery, and canonical-commit contracts.
6. **Production container and browser smoke test** builds the real Dockerfile, starts it with a read-only root filesystem and dropped capabilities, verifies health and system endpoints, checks that the running image reports the commit that built it, checks that a forwarded header from a peer nothing trusts is refused and reported, and uses Chromium to exercise administrator sign-in, Provider configuration, PDF upload, parsing, the administration area, and the API description page.

Two more publish, and not for the same events:

7. **Publish image to GitHub Container Registry** waits for all six, builds the same Dockerfile, and pushes it to `ghcr.io`. A `v*` tag publishes the release names; a push to `main` publishes `edge` and nothing else. See [Published Images](#published-images).
8. **Publish GitHub release** runs for a `v*` tag alone. It waits for the image, then creates the release the tag names. Its notes are the ones GitHub generates from the pull requests merged since the previous tag, with the `docker pull` line placed above them.

TRX results, Playwright HTML reports, screenshots, failure traces/videos, and container logs are uploaded as Actions artifacts. Temporary administrator credentials exist only in the isolated runner environment and are not repository secrets or production defaults.

## Published Images

`ghcr.io/philfanzhou/structadoc` receives `latest`, `<version>`, and `<major>.<minor>` from a `v*` tag, and `edge` from a push to `main`. Nothing else publishes.

`latest` names the newest release. Whoever pulls it did not choose a tag, and a `latest` reporting `0.0.0-dev` would be a development build handed to someone who never asked for one, so the rule that produces it is gated on the tag rather than given to whichever build published most recently.

`edge` is whatever `main` currently is, and it is meant to move. It exists so that a change can be pulled and tried before anyone has decided on a version for it: the next push to the default branch overwrites it, and it reports `0.0.0-dev` because no release named a version for it. That is what makes it usable for trying something and what makes it unusable for a deployment, which names a version or a digest.

`sha-<commit>` is not published. The branch build produces `edge` instead: one name that moves, rather than one permanent name per push, none of which was pulled twice. A deployment that named a commit names a version instead, or a digest, which is the only immutable name in either case. Images already published under a commit name stay in the registry; no new ones appear.

Releasing is one push. `git tag -a v0.1.0 && git push origin v0.1.0` is what produces every name a deployment can be pinned to, rather than only what turns the semver rules on. The tag also supplies the `VERSION` build argument, so the version the registry advertises and the version the running service reports come from the same place and cannot drift. A build from the default branch passes no version and keeps the placeholder `Directory.Build.props` declares, which is why `edge` reports `0.0.0-dev` and a release never does.

Both image-building jobs pass `SOURCE_REVISION`, and the container job then asks the running image for it. This is checked rather than assumed because nothing inside the image can derive it: `.dockerignore` excludes `.git`, so the SourceLink the SDK ships finds no repository and stamps nothing. A build argument that quietly stopped being passed would leave every deployment unable to say which commit it is, while every health check still passed.

Publishing waits on all six jobs above it, so a tag in the registry is an image whose legacy Office fixtures converted through real LibreOffice, whose consumer SDK generated and compiled, and which built, started under the production security flags, answered readiness, and served a browser flow. An image that failed any of that is never pushed, which is what keeps `latest` from being the least tested thing in the registry. The release job waits on publishing in turn, so a release never names an image nobody can pull.

A tag whose run failed that way is not moved onto the fix. A name that has been pushed stays where it is even when it published nothing, and the next tag carries the release instead. A tag that can be repointed is not something a deployment can be pinned to, and the run that failed stays legible as what it was rather than being overwritten by a later attempt.

Note that a release builds rather than promotes: the tag run compiles the source again and stamps the version the tag names into it. No image from an earlier run is retagged, and none is kept waiting for a release to claim it.

The push is authenticated with the workflow's own `GITHUB_TOKEN` rather than a stored credential; no registry secret exists in this repository. Each image carries a signed build provenance attestation naming the workflow, commit, and build parameters that produced it, and an SBOM. `gh attestation verify oci://ghcr.io/philfanzhou/structadoc:latest --owner philfanzhou` checks one against this repository.

The container job still builds the image itself rather than pulling this one. It runs in parallel with publishing, and a smoke test that depended on a registry would be testing a previous commit's artifact.

## What Covers Parsing

Parsing is the part of the product that is hardest to reach from a test, and the coverage is split deliberately:

- `ParseRunExecutorTests` substitutes the Provider and covers the orchestration around it: conversion, checkpoints, deadlines, and the failure classes.
- `ParseExecutionEndToEndTests` substitutes nothing below the public API. It starts a MinerU-shaped HTTP server on a real socket, configures a real `mineru-local` Provider against it, and lets the resident Worker carry a Parse Run from `queued` to `succeeded`. It then reads the Blocks, Pages, Assets, Artifacts, Markdown, and a ZIP export back through the API. This is what exercises the real HTTP adapter, the archive download, normalization, and the canonical commit together.
- The browser job runs the production image as shipped and points a Provider at an address the Host itself refuses. That covers what is specific to the image: the resident Worker starts, claims a queued run, calls out over HTTP, records a final status, and the workspace shows it without anyone pressing refresh. It passes no `Worker__` variable, so it also holds that a configured Provider is the only thing standing between an upload and an execution attempt.

What no test covers is a real MinerU service. Nothing in CI can supply one, so the first real parse remains a deployment step rather than a verified one; everything up to the Provider's own behaviour is verified.

## Test Runner

Tests run on Microsoft.Testing.Platform rather than VSTest. `global.json` selects it for the whole repository, so the commands below need no extra argument and no project opts in on its own. Each test project builds as its own executable, and xunit v3 supplies the runner directly instead of a separate VSTest adapter.

The difference shows up in reporting: a TRX file comes from `--report-trx` rather than `--logger "trx;..."`. The .NET 10 SDK cannot drive Microsoft.Testing.Platform through VSTest at all, so the old form is an error rather than a slower path to the same result.

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

With LibreOffice installed, run the real legacy Office conversions:

```bash
STRUCTADOC_RUN_LIBREOFFICE_INTEGRATION_TESTS=1 \
dotnet test tests/StructaDoc.Persistence.Tests/StructaDoc.Persistence.Tests.csproj
```

PowerShell:

```powershell
$env:STRUCTADOC_RUN_LIBREOFFICE_INTEGRATION_TESTS = '1'
dotnet test tests/StructaDoc.Persistence.Tests/StructaDoc.Persistence.Tests.csproj
```

Set `STRUCTADOC_LIBREOFFICE_EXECUTABLE` only when the executable is not available as `libreoffice` on `PATH`.

Browser tests default to `http://127.0.0.1:8080`. Override it with `STRUCTADOC_E2E_BASE_URL`; inject the test administrator through `STRUCTADOC_E2E_ADMIN_USERNAME` and `STRUCTADOC_E2E_ADMIN_PASSWORD`.

## Interpretation Rules

- Local build success does not substitute for the real database or production-container jobs.
- A workflow definition is not proof that its jobs passed; use the run associated with the current commit.
- A red run in the history is not the current status. Compare a run's head SHA with `origin/main` and read the newest one; an older failure is usually one a later commit already fixed.
- Do not weaken tests or suppress package audits to make CI green.
- Preserve failure artifacts long enough to diagnose database, browser, and container regressions.
