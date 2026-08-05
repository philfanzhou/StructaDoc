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
$dockerArguments = @(
    'build',
    '--tag', $ImageTag,
    '--build-arg', "DOTNET_REGISTRY=$dotNetRegistry",
    '--build-arg', "NUGET_SOURCE=$($env:STRUCTADOC_NUGET_SOURCE)",
    '--build-arg', "APT_MIRROR=$($env:STRUCTADOC_APT_MIRROR)",
    '--build-arg', "APT_PORTS_MIRROR=$($env:STRUCTADOC_APT_PORTS_MIRROR)",
    '.'
)

docker @dockerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Docker build failed with exit code $LASTEXITCODE."
}
