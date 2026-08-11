#!/usr/bin/env bash
set -euo pipefail

mirror_mode="${1:-auto}"
image_tag="${STRUCTADOC_IMAGE_TAG:-structadoc:local}"
official_nuget_source="https://api.nuget.org/v3/index.json"
official_apt_probe="https://archive.ubuntu.com/ubuntu/dists/noble/InRelease"
china_nuget_source="${STRUCTADOC_CN_NUGET_SOURCE:-https://repo.huaweicloud.com/repository/nuget/v3/index.json}"
china_apt_mirror="${STRUCTADOC_CN_APT_MIRROR:-https://mirrors.tuna.tsinghua.edu.cn/ubuntu}"
china_apt_ports_mirror="${STRUCTADOC_CN_APT_PORTS_MIRROR:-https://mirrors.tuna.tsinghua.edu.cn/ubuntu-ports}"

probe_endpoint() {
    curl --fail --silent --show-error --location --max-time 5 --output /dev/null "$1"
}

if ! command -v docker >/dev/null 2>&1; then
    echo 'Docker is required to build the StructaDoc container image.' >&2
    exit 1
fi

case "${mirror_mode,,}" in
    auto)
        if probe_endpoint "$official_nuget_source" && probe_endpoint "$official_apt_probe"; then
            selected_mode="official"
        else
            selected_mode="china"
        fi
        ;;
    official|china)
        selected_mode="${mirror_mode,,}"
        ;;
    *)
        echo 'Usage: build-container.sh [auto|official|china]' >&2
        exit 2
        ;;
esac

if [[ "$selected_mode" == 'china' ]]; then
    export STRUCTADOC_NUGET_SOURCE="$china_nuget_source"
    export STRUCTADOC_APT_MIRROR="$china_apt_mirror"
    export STRUCTADOC_APT_PORTS_MIRROR="$china_apt_ports_mirror"
else
    export STRUCTADOC_NUGET_SOURCE="$official_nuget_source"
    export STRUCTADOC_APT_MIRROR=''
    export STRUCTADOC_APT_PORTS_MIRROR=''
fi

echo "StructaDoc build mirror mode: $selected_mode"
echo "NuGet source: $STRUCTADOC_NUGET_SOURCE"
echo "APT mirror: ${STRUCTADOC_APT_MIRROR:-Ubuntu official repositories}"

# What the built service answers to /api/v1/system/info. The SDK ships SourceLink and would work this
# out on its own, but .dockerignore keeps .git out of the build context, so the commit has to be
# handed in. A working copy with changes in it produced an image that matches no commit, and saying
# so is the point: an image that names a commit it was not built from is worse than one that admits
# it. Outside a checkout, neither is claimed.
source_revision="$(git rev-parse HEAD 2>/dev/null || true)"
if [[ -n "$source_revision" && -n "$(git status --porcelain 2>/dev/null)" ]]; then
    source_revision="$source_revision-dirty"
fi
echo "Source revision: ${source_revision:-unknown, not a checkout}"

docker build \
    --tag "$image_tag" \
    --build-arg "DOTNET_REGISTRY=${STRUCTADOC_DOTNET_REGISTRY:-mcr.microsoft.com/dotnet}" \
    --build-arg "NUGET_SOURCE=$STRUCTADOC_NUGET_SOURCE" \
    --build-arg "APT_MIRROR=$STRUCTADOC_APT_MIRROR" \
    --build-arg "APT_PORTS_MIRROR=$STRUCTADOC_APT_PORTS_MIRROR" \
    --build-arg "SOURCE_REVISION=$source_revision" \
    .
