# Single-Container Deployment

This document describes the current single-container entry point with a SQLite volume. The image boundary follows [ADR-0003](../adr/0003-technology-and-single-image-deployment.md). PostgreSQL, MySQL, and MariaDB run as external services rather than inside the StructaDoc image.

## Image Contents

The root `Dockerfile` uses three build stages:

1. Node.js 24 builds the Vue workspace;
2. the official .NET 10 SDK Noble image restores and publishes `StructaDoc.Host`, including the compiled web assets;
3. the official ASP.NET Core 10 Noble runtime installs LibreOffice Writer, Calc, and Impress no-GUI components plus common Latin and CJK fonts.

The final image contains ASP.NET Core Runtime, the Host, web assets, four migration assemblies, LibreOffice no-GUI components, fonts, CA certificates, and `curl` for health checks. It does not contain the .NET SDK, Node.js, npm, Python, the UNO Python bridge, FastAPI, or a second resident service.

Ubuntu 24.04 Noble is explicit because the official .NET 10 image does not provide a Debian variant. The Dockerfile installs only the no-GUI components required for DOC/DOCX, XLS/XLSX, and PPT/PPTX conversion and verifies that Python, Node.js, and npm are absent from the runtime stage.

## Build

The default build uses official Microsoft Container Registry, Ubuntu, npm, and NuGet sources:

```bash
docker build --tag structadoc:local .
```

The repository also provides Bash and PowerShell wrappers with `official`, `china`, and `auto` modes. `auto` tests connectivity to the official package sources with a short timeout before invoking Docker; it does not use IP geolocation.

```bash
bash ./scripts/build-container.sh auto
bash ./scripts/build-container.sh official
bash ./scripts/build-container.sh china
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/build-container.ps1 -MirrorMode Auto
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/build-container.ps1 -MirrorMode Official
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/build-container.ps1 -MirrorMode China
```

`ExecutionPolicy Bypass` applies only to that Windows PowerShell process. Review repository scripts before executing them. PowerShell 7 users may run `pwsh -File ./scripts/build-container.ps1 -MirrorMode Auto` when local policy permits.

The `official` mode is appropriate for reproducible CI and release builds. The default `china` sources can be replaced with organization-managed proxies:

- `STRUCTADOC_CN_NUGET_SOURCE` — NuGet V3 service index;
- `STRUCTADOC_CN_APT_MIRROR` — Ubuntu repository for amd64 and similar architectures;
- `STRUCTADOC_CN_APT_PORTS_MIRROR` — Ubuntu Ports repository for arm64 and similar architectures.

The default tag is `structadoc:local`. Bash accepts `STRUCTADOC_IMAGE_TAG`; PowerShell accepts `-ImageTag`. Compose also exposes `STRUCTADOC_NUGET_SOURCE`, `STRUCTADOC_APT_MIRROR`, and `STRUCTADOC_APT_PORTS_MIRROR` as explicit build arguments.

Docker resolves `FROM` before container commands run, so package-source probing cannot replace the base-image registry. Configure a trusted Docker registry mirror, or set `STRUCTADOC_DOTNET_REGISTRY` to an internal path that contains both `sdk` and `aspnet` repositories.

Alternate NuGet endpoints and base-image proxies are additional supply-chain trust boundaries. Release builds should pin trusted sources, retain logs, and never place credentials in build arguments.

GitHub Actions builds and runs the production image, verifies its health and system endpoints under restricted container settings, and executes a Chromium user-workspace flow. This is the authoritative remote image verification; local operators should still test representative private document formats and fonts in their deployment environment.

Useful runtime checks include:

```bash
docker run --rm --entrypoint /usr/bin/libreoffice structadoc:local --headless --version
docker run --rm --entrypoint /bin/sh structadoc:local -c '! command -v python3 && ! command -v node && ! command -v dotnet-sdk'
```

## SQLite Compose Start

`compose.yaml` starts one StructaDoc application container. SQLite, original files, generated resources, and the Data Protection key ring all live under `/data` on one named volume. No database server is included in the image.

Set bootstrap credentials in the current shell:

```bash
export STRUCTADOC_ADMIN_EMAIL='admin@example.com'
export STRUCTADOC_ADMIN_PASSWORD='use-a-secret-manager-or-a-long-random-value'
docker compose up --build --detach
```

PowerShell:

```powershell
$env:STRUCTADOC_ADMIN_EMAIL = 'admin@example.com'
$env:STRUCTADOC_ADMIN_PASSWORD = 'use-a-secret-manager-or-a-long-random-value'
docker compose up --build --detach
```

If a wrapper already built the image, use `docker compose up --detach --no-build` so Compose does not rebuild with different source settings.

The examples are placeholders, not default credentials. Production environments must inject real values through deployment secrets, not repository files, Compose files, or shared `.env` files. Remove bootstrap variables after confirming that the first administrator can sign in; the stored account remains.

The default mapping is `http://localhost:8080`, and readiness is `/health/ready`. Compose uses a read-only root filesystem, drops Linux capabilities, prevents privilege escalation, gives `/tmp` a bounded `tmpfs`, and runs as the non-root UID from the official .NET image.

Real Parse Run execution remains disabled unless explicitly enabled:

```bash
export STRUCTADOC_EXECUTION_ENABLED=true
```

Enabling it permits the Worker to send documents to the selected Provider and to start LibreOffice when conversion is required.

## Persistence and Permissions

The image declares `/data` and prepares:

- `/data/structadoc.db` — SQLite database and sidecar files;
- `/data/storage` — originals, Provider archives, segments, Assets, and Artifacts;
- `/data/keys` — the Data Protection key ring for cookies, antiforgery, Provider credentials, and submission checkpoints;
- `/data/temp` — bounded temporary space for LibreOffice, ZIP intake, and normalization.

Named volumes inherit the image's non-root ownership. For bind mounts, grant the image `APP_UID` write access in advance; do not switch back to root to bypass permissions.

Backups must include the database, storage, and key ring as one consistent recovery set. Restoring the database without its objects breaks resource references; restoring encrypted Provider data without its key ring makes it unreadable.

## Runtime Limits

Application code bounds conversion concurrency, execution time, file size, archive expansion, and temporary disk. These limits do not replace platform CPU, memory, process, filesystem, and log-rotation quotas. Production deployments should configure those quotas and capacity alerts for `/data`.

The container has a one-minute graceful shutdown window after `SIGTERM`. Remote Provider work remains governed by Parse Run leases and recovery semantics.
