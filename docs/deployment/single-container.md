# Single-Container Deployment

This document describes the current single-container entry point with a SQLite volume. The image boundary follows [ADR-0003](../adr/0003-technology-and-single-image-deployment.md). PostgreSQL, MySQL, and MariaDB run as external services rather than inside the StructaDoc image.

## Image Contents

The root `Dockerfile` uses three build stages:

1. Node.js 24 builds the Vue workspace;
2. the official .NET 10 SDK Noble image restores and publishes `StructaDoc.Host`, including the compiled web assets;
3. the official ASP.NET Core 10 Noble runtime installs LibreOffice Writer, Calc, and Impress no-GUI components plus common Latin and CJK fonts.

The final image contains ASP.NET Core Runtime, the Host, web assets, four migration assemblies, LibreOffice no-GUI components, fonts, CA certificates, and `curl` for health checks. It does not contain the .NET SDK, Node.js, npm, Python, the UNO Python bridge, FastAPI, or a second resident service.

Ubuntu 24.04 Noble is explicit because the official .NET 10 image does not provide a Debian variant. The Dockerfile installs only the no-GUI components required for DOC/DOCX, XLS/XLSX, and PPT/PPTX conversion and verifies that Python, Node.js, and npm are absent from the runtime stage.

## Published Image

CI publishes the image to GitHub Container Registry for every `v*` tag and every push to `main`, and only after all four test jobs pass, so an available tag is one that built, started under the security flags below, answered readiness, and served a browser flow:

```bash
docker pull ghcr.io/philfanzhou/structadoc:latest
```

The repository is public and so is the package, so this needs no registry sign-in:

| Tag | Points at |
|---|---|
| `latest` | the newest release |
| `<version>`, `<major>.<minor>` | that release |
| `edge` | the current `main`, which is not a release |

`latest` is produced by a release tag alone, so no development build can take it. It still moves under a deployment that restarts, so name a version tag in production, or a digest to pin exactly.

`edge` is for trying `main` before a release names it. Every push to the default branch overwrites it and it reports `0.0.0-dev`, so it answers whether a change works and nothing a deployment should rest on.

`sha-<commit>` is not published. A deployment that followed one follows a version tag instead; images published under a commit name before this rule remain in the registry, but no new ones appear.

Each release is also listed under [Releases](https://github.com/philfanzhou/StructaDoc/releases), with the same pull command and the changes since the previous tag.

The registry also holds tags of the form `sha256-<digest>`. Those are not images. They are the provenance attestations described below, stored under the fallback name the OCI referrers specification gives them, and the package page lists them beside the real tags. One pulls in about eleven kilobytes and produces something with no platform, no layers, and no entrypoint. Anything beginning `sha256-` is an attestation rather than an image.

Each image carries a signed build provenance attestation and an SBOM. Verify one before a deployment trusts it:

```bash
gh attestation verify oci://ghcr.io/philfanzhou/structadoc:latest --owner philfanzhou
```

Substitute `ghcr.io/philfanzhou/structadoc:latest` for `structadoc:local` in the commands below to run it. Building locally remains supported and is what the following sections describe; it is the path for a private fork, an air-gapped site, or a build against internal package mirrors.

### Which Build Is Running

A deployment names itself over HTTP, so the question can be answered without reaching the machine:

```bash
curl --silent http://127.0.0.1:8080/api/v1/system/info
```

```json
{"name":"StructaDoc","version":"0.1.0+9fc05ce62bbaa17e0aac4de712c66ba0a53dcb22"}
```

The part after `+` is the commit the image was built from. The version before it comes from the `v*` release tag that built it; a build made outside a release, locally or on the default branch, reports `0.0.0-dev`, which is honest about a build no release named rather than a version number that separates nothing. The administration area shows the same string in its header, for an administrator who does not use a terminal.

Nothing inside the image can work this out for itself: `.dockerignore` excludes `.git`, so the SourceLink the .NET SDK ships finds no repository. The `SOURCE_REVISION` build argument carries it, and both the CI workflow and the build wrappers below pass it. An image built from a working copy with uncommitted changes reports the commit with `-dirty` appended, because an image that names a commit it was not built from is worse than one that admits it.

## Build

The default build uses official Microsoft Container Registry, Ubuntu, npm, and NuGet sources:

```bash
docker build --tag structadoc:local .
```

That image reports `0.0.0-dev` with no commit, because nothing told it which one. Two optional arguments fill that in; the wrappers below supply the second on their own:

```bash
docker build --tag structadoc:local \
    --build-arg SOURCE_REVISION="$(git rev-parse HEAD)" \
    --build-arg VERSION=0.1.0 \
    .
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

The default tag is `structadoc:local`. Bash accepts `STRUCTADOC_IMAGE_TAG`; PowerShell accepts `-ImageTag`. Both scripts forward `DOTNET_REGISTRY`, `NUGET_SOURCE`, `APT_MIRROR`, and `APT_PORTS_MIRROR` to `docker build` as explicit build arguments.

Docker resolves `FROM` before container commands run, so package-source probing cannot replace the base-image registry. Configure a trusted Docker registry mirror, or set `STRUCTADOC_DOTNET_REGISTRY` to an internal path that contains both `sdk` and `aspnet` repositories.

Alternate NuGet endpoints and base-image proxies are additional supply-chain trust boundaries. Release builds should pin trusted sources, retain logs, and never place credentials in build arguments.

GitHub Actions builds and runs the production image, verifies its health and system endpoints under restricted container settings, and executes a Chromium user-workspace flow. This is the authoritative remote image verification; local operators should still test representative private document formats and fonts in their deployment environment.

Useful runtime checks include:

```bash
docker run --rm --entrypoint /usr/bin/libreoffice structadoc:local --headless --version
docker run --rm --entrypoint /bin/sh structadoc:local -c '! command -v python3 && ! command -v node && ! command -v dotnet-sdk'
```

## SQLite Container Start

The image is the entire deployment unit. There is no orchestration file: one `docker run` starts the service. SQLite, original files, generated resources, and the Data Protection key ring all live under `/data`. No database server is included in the image.

```bash
docker run --detach --name structadoc \
  --read-only \
  --security-opt no-new-privileges:true \
  --cap-drop ALL \
  --tmpfs /tmp:size=256m,mode=1777 \
  --restart unless-stopped \
  --publish 8080:8080 \
  --volume /srv/structadoc/data:/data \
  --env Authentication__BootstrapAdministratorUsername='structadoc-admin' \
  --env Authentication__BootstrapAdministratorPassword='use-a-secret-manager-or-a-long-random-value' \
  structadoc:local
```

PowerShell:

```powershell
docker run --detach --name structadoc `
  --read-only `
  --security-opt no-new-privileges:true `
  --cap-drop ALL `
  --tmpfs /tmp:size=256m,mode=1777 `
  --restart unless-stopped `
  --publish 8080:8080 `
  --volume D:\StructaDoc\data:/data `
  --env Authentication__BootstrapAdministratorUsername='structadoc-admin' `
  --env Authentication__BootstrapAdministratorPassword='use-a-secret-manager-or-a-long-random-value' `
  structadoc:local
```

`scripts/start-container.sh` is that command written down for Linux and WSL, using the published image and the repository's own `data` directory. It removes an existing `structadoc` container before starting the current one, so re-running it is how a new image is applied, and it needs root only to give the data directory to UID 1654. It carries no bootstrap credentials, which is why it can live in the repository.

The security flags are part of the supported configuration, not decoration: a read-only root filesystem, dropped Linux capabilities, no privilege escalation, a bounded `/tmp` `tmpfs`, and the non-root UID from the official .NET image. Everything the service writes goes to `/data` or that `tmpfs`.

A host-directory bind mount is preferred over a named volume. `/data` is the entire recovery set, and a plain directory can be inspected and copied without Docker-specific tooling.

The bootstrap variables are optional. Without them the container starts with no administrator and the first visitor to `http://localhost:8080` is sent to `/setup` to create one; see [Authentication](../development/authentication.md) for what that exposes. Set them for unattended deployments, where an account must exist before the service accepts requests.

The examples are placeholders, not default credentials. Production environments must inject real values through deployment secrets, not repository files or shared `.env` files. Remove bootstrap variables after confirming that the first administrator can sign in; the stored account remains.

The default mapping is `http://localhost:8080`, and readiness is `/health/ready`. One container serves the document workspace at `/`, the administration area at `/admin`, and the API under `/api/v1`, so a deployment needs one published port, one certificate, and one reverse-proxy upstream. See [User Workspace and OIDC](../development/user-workspace-oidc.md).

Supplying a parsing Provider credential under `/admin` is the one thing a new deployment must do, and it is the only thing standing between an upload and an execution attempt. A deployment with no Provider at all configures the official MinerU endpoint on startup — hosted type, published address, model `vlm`, enabled and marked default — so what is left is its API token. The token belongs to the deployment's own MinerU account and cannot ship in an image, so Parse Runs are refused with that reason until an administrator enters it, and both the administration area and the workspace say so.

Everything about that configuration is editable, and a deployment that would rather use a self-hosted MinerU adds its own Provider and marks it default instead. One Provider has to be enabled and marked default either way: the workspace starts a parse without naming a Provider, so a deployment with configuration but no enabled default has a button that can only fail. The administration area says so when that is the case, and puts Providers at the top of the page under **必须配置** for the same reason.

There is no separate switch to turn parsing on afterwards. Supplying a Provider's credential and leaving it enabled is the point at which a deployment says its documents may be sent there — a cloud Provider means they leave the machine — and a further default-off flag added nothing to that decision while producing deployments where an upload queued forever with nothing failing and nothing logged. To pause parsing, disable the Provider; that stops new runs from being created, though runs already queued carry their own configuration snapshot and will still run.

`Worker__Enabled=false` stops this Host running Workers at all. That is for splitting a deployment — one Host serving the API, others parsing — not for pausing: it is not settable from a browser, and a Host with it off still accepts Parse Runs and leaves them queued. The workspace says so when it is off, and `GET /api/v1/parse-execution` answers the same question for anything that is not a browser.

Sign-in through an identity provider is configured under `/admin` as well. Until it is, only administrators can use the deployment: the workspace has no other way in. See [User Workspace and OIDC](../development/user-workspace-oidc.md) for what to register at the provider, and [Service Settings](../development/service-settings.md) for what else is settable from the browser and what each change requires.

## Where /data Comes From

The image's `/data` layout is a configuration file inside the image, `appsettings.Container.json`, rather than environment variables. The difference matters: an environment variable pins a setting, so the web interface reports it as unchangeable and refuses to write it. Storage and the business database are meant to be moved from the browser, so they ship as defaults a stored setting can be layered over.

Setting `Storage__*` or `Database__*` on `docker run` still works and still pins them, which is what an operator managing configuration from outside the container wants. What is genuinely fixed by the image stays an environment variable: the control-plane database path, the Data Protection key ring, and whether migrations are applied at startup are not settable from a browser at all.

Moving either is a migration, not a switch. A new database is created empty at the next start and a new storage location starts empty; nothing copies existing documents, objects, or Parse Runs across. Test the new location with the button beside it first — the storage test writes and removes a probe object, and the database test connects and reads migration history without creating anything.

## Behind a Reverse Proxy

A proxy that terminates TLS forwards plain HTTP from its own address, so without being told otherwise the service is wrong about three things at once: session cookies are issued without `Secure`, because the request it can see is not secure; the sign-in redirect address composed for an identity provider says `http` and no longer matches what was registered there; and the sign-in rate limiter partitions every visitor into one bucket belonging to the proxy, so ten wrong passwords from anyone lock out everyone.

The proxy states what the browser actually asked for in `X-Forwarded-Proto`, `X-Forwarded-Host`, and `X-Forwarded-For`. A forwarded header is a claim by whoever sent it, so nothing is read until the deployment names the peer it believes:

```bash
docker run ... \
  --env ReverseProxy__TrustedProxies='172.17.0.1' \
  --env ReverseProxy__PublicHosts='docs.example.com' \
  structadoc:local
```

The address to name is the one the container sees, which is rarely the one the proxy has. A proxy running on the Docker host arrives as the bridge gateway, typically `172.17.0.1`; a proxy in another container on a shared network arrives as its address there, which is worth naming as a range such as `172.18.0.0/16` because it is not stable. Addresses and ranges are separated by commas. Nothing else is trusted, including loopback: inside a container that is the container itself.

Getting the address wrong looks like a working deployment until sign-in fails, and nothing outside the container can read that address off. The service reports it instead: a forwarded header that arrives and is not applied is logged once per peer, naming the address to trust.

`ReverseProxy__PublicHosts` is separate because `X-Forwarded-Host` is the one forwarded value a proxy usually does not set and does pass through from the client, so trusting the peer is not enough to trust the value. Until the published host names are listed, that header is ignored entirely. Set it if an identity provider is configured, since the host decides the sign-in redirect address.

`ReverseProxy__ForwardLimit` defaults to `1`. Raise it to the number of proxies actually in front of the deployment — a CDN in front of an ingress is `2` — and no higher, because each hop consumes one entry and a limit above the real count lets the client supply the rest.

None of this is settable from `/admin`. An administrator reaches the service through the proxy and cannot see what is in front of it, so it stays with whoever placed the container in that network. Reaching the deployment directly over HTTP, without a proxy, needs no configuration here at all.

## Restart Policy

Some settings only take effect after a restart, and the administration page offers a button that stops the Host so its supervisor starts it again. The image cannot restart itself, so run the container with a restart policy or that button leaves the service down until it is started by hand:

```bash
docker run --restart unless-stopped ...
```

The Host exits cleanly, with status `0`. `unless-stopped` and `always` restart on that; `on-failure` does not, so it is not a working choice here.

A deployment whose administrators are not expected to change settings can leave the policy off; the button then reports that the service will not come back on its own, both before and after it is used.

## Persistence and Permissions

The image declares `/data` and prepares:

- `/data/control.db` — control-plane database holding administrator accounts, always local SQLite;
- `/data/structadoc.db` — SQLite business database and sidecar files;
- `/data/storage` — originals, Provider archives, segments, Assets, and Artifacts;
- `/data/keys` — the Data Protection key ring for cookies, antiforgery, Provider credentials, and submission checkpoints;
- `/data/temp` — bounded temporary space for LibreOffice, ZIP intake, and normalization.

Named volumes inherit the image's non-root ownership. For bind mounts, grant the image `APP_UID` write access in advance; do not switch back to root to bypass permissions.

Backups must include the database, storage, and key ring as one consistent recovery set. Restoring the database without its objects breaks resource references; restoring encrypted Provider data without its key ring makes it unreadable.

That cuts the other way too, and the service says so at every start: `No XML encryptor configured. Key … may be persisted to storage in unencrypted form.` The key ring under `/data/keys` is written in the clear, because a single container on Linux has nothing to encrypt it with that would not itself have to be stored somewhere — and it is the key that makes stored Provider tokens readable. Anyone holding a copy of `/data` holds those tokens. Treat the directory, and every backup and snapshot of it, as a secret: restrict it to the `APP_UID`, keep backups encrypted, and rotate a Provider credential that was in a copy which left the host. A deployment that needs the key ring itself encrypted at rest needs a master key held outside `/data`, which this image does not yet support.

## Runtime Limits

Application code bounds conversion concurrency, execution time, file size, archive expansion, and temporary disk. These limits do not replace platform CPU, memory, process, filesystem, and log-rotation quotas. Production deployments should configure those quotas and capacity alerts for `/data`.

The image logs database commands at `Warning`. At `Information` the two Workers alone write a few hundred lines a minute on a service doing nothing, which rotates away whatever an operator went looking for; the failures are what is worth keeping. Set `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information` to get the statements back while diagnosing something, and unset it afterwards.

The container has a one-minute graceful shutdown window after `SIGTERM`. Remote Provider work remains governed by Parse Run leases and recovery semantics.
