ARG DOTNET_VERSION=10.0
ARG DOTNET_REGISTRY=mcr.microsoft.com/dotnet
ARG NODE_VERSION=24

FROM node:${NODE_VERSION}-bookworm-slim AS web-build
WORKDIR /source/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build -- --outDir /web-dist

FROM ${DOTNET_REGISTRY}/sdk:${DOTNET_VERSION}-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
WORKDIR /source

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/StructaDoc.Application/StructaDoc.Application.csproj src/StructaDoc.Application/
COPY src/StructaDoc.Contracts/StructaDoc.Contracts.csproj src/StructaDoc.Contracts/
COPY src/StructaDoc.Domain/StructaDoc.Domain.csproj src/StructaDoc.Domain/
COPY src/StructaDoc.Host/StructaDoc.Host.csproj src/StructaDoc.Host/
COPY src/StructaDoc.Platform/StructaDoc.Platform.csproj src/StructaDoc.Platform/
COPY src/StructaDoc.Migrations.MariaDb/StructaDoc.Migrations.MariaDb.csproj src/StructaDoc.Migrations.MariaDb/
COPY src/StructaDoc.Migrations.MySql/StructaDoc.Migrations.MySql.csproj src/StructaDoc.Migrations.MySql/
COPY src/StructaDoc.Migrations.PostgreSql/StructaDoc.Migrations.PostgreSql.csproj src/StructaDoc.Migrations.PostgreSql/
COPY src/StructaDoc.Migrations.Sqlite/StructaDoc.Migrations.Sqlite.csproj src/StructaDoc.Migrations.Sqlite/
RUN test -n "${NUGET_SOURCE}" \
    && dotnet restore src/StructaDoc.Host/StructaDoc.Host.csproj \
        --source "${NUGET_SOURCE}"

COPY src/ src/
COPY --from=web-build /web-dist/ src/StructaDoc.Host/wwwroot/
RUN dotnet publish src/StructaDoc.Host/StructaDoc.Host.csproj \
    --configuration "${BUILD_CONFIGURATION}" \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM ${DOTNET_REGISTRY}/aspnet:${DOTNET_VERSION}-noble AS runtime

ARG APT_MIRROR
ARG APT_PORTS_MIRROR

USER root
RUN if [ -n "${APT_MIRROR}" ]; then \
        mirror="${APT_MIRROR%/}/"; \
        sed --in-place --regexp-extended \
            "s#https?://archive.ubuntu.com/ubuntu/?#${mirror}#g; s#https?://security.ubuntu.com/ubuntu/?#${mirror}#g" \
            /etc/apt/sources.list.d/ubuntu.sources; \
    fi \
    && if [ -n "${APT_PORTS_MIRROR}" ]; then \
        ports_mirror="${APT_PORTS_MIRROR%/}/"; \
        sed --in-place --regexp-extended \
            "s#https?://ports.ubuntu.com/ubuntu-ports/?#${ports_mirror}#g" \
            /etc/apt/sources.list.d/ubuntu.sources; \
    fi \
    && grep '^URIs:' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        fontconfig \
        fonts-crosextra-caladea \
        fonts-crosextra-carlito \
        fonts-liberation2 \
        fonts-noto-cjk \
        fonts-noto-core \
        libreoffice-calc-nogui \
        libreoffice-core-nogui \
        libreoffice-impress-nogui \
        libreoffice-math-nogui \
        libreoffice-writer-nogui \
    && rm -rf /var/lib/apt/lists/* \
    && fc-cache --force \
    && libreoffice --headless --version \
    && ! command -v python3 \
    && ! command -v node \
    && ! command -v npm \
    && test -z "$(dotnet --list-sdks)"

WORKDIR /app
COPY --from=build /app/publish/ ./

RUN install -d -o "${APP_UID}" -g "${APP_UID}" \
        /data \
        /data/keys \
        /data/storage \
        /data/temp \
        /data/temp/libreoffice \
        /data/temp/provider-normalization \
        /data/temp/provider-results

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/tmp \
    XDG_CACHE_HOME=/tmp/.cache \
    Database__Provider=Sqlite \
    Database__ConnectionString="Data Source=/data/structadoc.db" \
    ControlPlane__DatabasePath=/data/control.db \
    Database__ApplyMigrationsOnStartup=true \
    Storage__Provider=Local \
    Storage__RootPath=/data/storage \
    Authentication__DataProtectionKeysPath=/data/keys \
    LibreOffice__ExecutablePath=/usr/bin/libreoffice \
    LibreOffice__TemporaryPath=/data/temp/libreoffice \
    ProviderResults__TemporaryPath=/data/temp/provider-results \
    ProviderResultNormalization__TemporaryPath=/data/temp/provider-normalization

VOLUME ["/data"]
EXPOSE 8080
STOPSIGNAL SIGTERM

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health/ready || exit 1

USER ${APP_UID}
ENTRYPOINT ["dotnet", "StructaDoc.Host.dll"]
