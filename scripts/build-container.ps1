[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Official', 'China')]
    [string]$MirrorMode = 'Auto',

    [string]$ImageTag = 'structadoc:local'
)

$ErrorActionPreference = 'Stop'

$officialNuGetSource = 'https://api.nuget.org/v3/index.json'
$officialAptProbe = 'https://archive.ubuntu.com/ubuntu/dists/noble/InRelease'
$chinaNuGetSource = if ($env:STRUCTADOC_CN_NUGET_SOURCE) {
    $env:STRUCTADOC_CN_NUGET_SOURCE
} else {
    'https://repo.huaweicloud.com/repository/nuget/v3/index.json'
}
$chinaAptMirror = if ($env:STRUCTADOC_CN_APT_MIRROR) {
    $env:STRUCTADOC_CN_APT_MIRROR
} else {
    'https://mirrors.tuna.tsinghua.edu.cn/ubuntu'
}
$chinaAptPortsMirror = if ($env:STRUCTADOC_CN_APT_PORTS_MIRROR) {
    $env:STRUCTADOC_CN_APT_PORTS_MIRROR
} else {
    'https://mirrors.tuna.tsinghua.edu.cn/ubuntu-ports'
}

function Test-BuildEndpoint {
    param([Parameter(Mandatory)][string]$Uri)

    try {
        Invoke-WebRequest -Uri $Uri -Method Get -TimeoutSec 5 -UseBasicParsing | Out-Null
        return $true
    } catch {
        return $false
    }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required to build the StructaDoc container image.'
}

$selectedMode = $MirrorMode
if ($MirrorMode -eq 'Auto') {
    $officialReachable =
        (Test-BuildEndpoint -Uri $officialNuGetSource) -and
        (Test-BuildEndpoint -Uri $officialAptProbe)
    $selectedMode = if ($officialReachable) { 'Official' } else { 'China' }
}

if ($selectedMode -eq 'China') {
    $env:STRUCTADOC_NUGET_SOURCE = $chinaNuGetSource
    $env:STRUCTADOC_APT_MIRROR = $chinaAptMirror
    $env:STRUCTADOC_APT_PORTS_MIRROR = $chinaAptPortsMirror
} else {
    $env:STRUCTADOC_NUGET_SOURCE = $officialNuGetSource
    $env:STRUCTADOC_APT_MIRROR = ''
    $env:STRUCTADOC_APT_PORTS_MIRROR = ''
}

Write-Host "StructaDoc build mirror mode: $selectedMode"
Write-Host "NuGet source: $($env:STRUCTADOC_NUGET_SOURCE)"
Write-Host "APT mirror: $(if ($env:STRUCTADOC_APT_MIRROR) { $env:STRUCTADOC_APT_MIRROR } else { 'Ubuntu official repositories' })"

$dotNetRegistry = if ($env:STRUCTADOC_DOTNET_REGISTRY) {
    $env:STRUCTADOC_DOTNET_REGISTRY
} else {
    'mcr.microsoft.com/dotnet'
}
# What the built service answers to /api/v1/system/info. The SDK ships SourceLink and would work this
# out on its own, but .dockerignore keeps .git out of the build context, so the commit has to be
# handed in. A working copy with changes in it produced an image that matches no commit, and saying
# so is the point: an image that names a commit it was not built from is worse than one that admits
# it. Outside a checkout, neither is claimed.
#
# Git's stderr is deliberately left alone here. Redirecting a native command's stderr in Windows
# PowerShell wraps each line in an error record, which $ErrorActionPreference = 'Stop' then turns
# into a terminating error -- so a working checkout would be reported as no checkout at all. The exit
# code is the reliable signal, and outside a checkout git's own message is worth seeing.
$sourceRevision = ''
if (Get-Command git -ErrorAction SilentlyContinue) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    # Out-String rather than Select-Object: taking the first element stops the pipeline early, and a
    # stopped pipeline leaves $LASTEXITCODE at -1 even though git succeeded.
    $revision = (git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $revision) {
        $sourceRevision = $revision
        if (git status --porcelain) { $sourceRevision = "$sourceRevision-dirty" }
    }
    $ErrorActionPreference = $previousPreference
}
Write-Host "Source revision: $(if ($sourceRevision) { $sourceRevision } else { 'unknown, not a checkout' })"

$dockerArguments = @(
    'build',
    '--tag', $ImageTag,
    '--build-arg', "DOTNET_REGISTRY=$dotNetRegistry",
    '--build-arg', "NUGET_SOURCE=$($env:STRUCTADOC_NUGET_SOURCE)",
    '--build-arg', "APT_MIRROR=$($env:STRUCTADOC_APT_MIRROR)",
    '--build-arg', "APT_PORTS_MIRROR=$($env:STRUCTADOC_APT_PORTS_MIRROR)",
    '--build-arg', "SOURCE_REVISION=$sourceRevision",
    '.'
)

docker @dockerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Docker build failed with exit code $LASTEXITCODE."
}
