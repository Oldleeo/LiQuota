[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$Runtime = @('win-x64', 'win-arm64'),
    [string]$Configuration = 'Release',
    [string]$NuGetConfig
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'LiQuota.csproj'
$testProject = Join-Path $projectRoot 'tests\LiQuota.SmokeTests\LiQuota.SmokeTests.csproj'
$artifactsRoot = Join-Path $projectRoot 'artifacts'

function Invoke-DotnetRestore {
    param([string]$Project, [string]$RuntimeIdentifier)

    $arguments = @('restore', $Project)
    if ($RuntimeIdentifier) { $arguments += @('-r', $RuntimeIdentifier) }
    if ($NuGetConfig) { $arguments += @('--configfile', $NuGetConfig) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $Project." }
}

Invoke-DotnetRestore -Project $projectFile
Invoke-DotnetRestore -Project $testProject

dotnet run --project $testProject -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
$checksumLines = @()
foreach ($rid in $Runtime) {
    Invoke-DotnetRestore -Project $projectFile -RuntimeIdentifier $rid
    $publishDirectory = Join-Path $artifactsRoot $rid
    dotnet publish $projectFile -c $Configuration -r $rid --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }

    $archivePath = Join-Path $artifactsRoot "LiQuota-$rid.zip"
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -Force
    $hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    $checksumLines += "$($hash.Hash.ToLowerInvariant())  $($hash.Path | Split-Path -Leaf)"
}

$checksumLines | Set-Content -LiteralPath (Join-Path $artifactsRoot 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Release artifacts: $artifactsRoot"
