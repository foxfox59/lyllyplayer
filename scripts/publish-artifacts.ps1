<#!
  Builds self-contained portable folders + ZIPs for Windows release RIDs (no .NET install on target PCs).
  Requires: .NET 8 SDK on the build machine only.

  From repository root:
    powershell -ExecutionPolicy Bypass -File .\scripts\publish-artifacts.ps1
    powershell -File .\scripts\publish-artifacts.ps1 -IncludeArm64
    powershell -File .\scripts\publish-artifacts.ps1 -ArtifactRoot artifacts\staging   # optional; relative to repo

  Default output (gitignored): <repo>\artifacts\publish\<rid>\ and <repo>\artifacts\LyllyPlayer-portable-<rid>.zip
  See: docs\RELEASING.md
#>
param(
    [string] $Configuration = 'Release',
    [switch] $IncludeArm64,
    [string] $ArtifactRoot = ''
)

$ErrorActionPreference = 'Stop'

$portableScript = Join-Path $PSScriptRoot 'publish-portable.ps1'
if (-not (Test-Path -LiteralPath $portableScript)) {
    throw "Missing $portableScript"
}

function Invoke-Portable([string] $rid) {
    Write-Host ""
    Write-Host "========== $rid ==========" -ForegroundColor Cyan
    if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
        & $portableScript -Runtime $rid -Configuration $Configuration
    }
    else {
        & $portableScript -Runtime $rid -Configuration $Configuration -ArtifactRoot $ArtifactRoot
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Invoke-Portable 'win-x64'
Invoke-Portable 'win-x86'

if ($IncludeArm64) {
    Invoke-Portable 'win-arm64'
}

Write-Host ""
$outBase = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts'
}
else {
    $ArtifactRoot
}
Write-Host "All requested artifacts built under: $outBase (see docs\RELEASING.md)." -ForegroundColor Green
