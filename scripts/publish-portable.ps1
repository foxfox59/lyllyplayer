<#!
  Produces a self-contained portable folder and ZIP (no .NET runtime install required on target PC).
  Requires: .NET 8 SDK on the *build* machine only.

  Usage (from repo root):
    powershell -ExecutionPolicy Bypass -File .\scripts\publish-portable.ps1
    powershell -File .\scripts\publish-portable.ps1 -Runtime win-x86   # 32-bit Windows
    powershell -File .\scripts\publish-portable.ps1 -ArtifactRoot artifacts\staging   # under repo (optional)

  Default output: <repo>\artifacts\publish\<rid>\ and <repo>\artifacts\LyllyPlayer-portable-<rid>.zip
  Multi-RID: .\scripts\publish-artifacts.ps1
  Docs: docs/RELEASING.md
#>
param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release',
    [string] $Project = (Join-Path $PSScriptRoot '..\LyllyPlayer\LyllyPlayer.App\LyllyPlayer.App.csproj'),
    # Base folder for publish\<rid>\ and ZIPs. Relative paths are under the repo root (default: artifacts).
    [string] $ArtifactRoot = ''
)

$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path $Project).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $repoRoot 'artifacts'
}
elseif ([System.IO.Path]::IsPathRooted($ArtifactRoot)) {
    $ArtifactRoot = [System.IO.Path]::GetFullPath($ArtifactRoot)
}
else {
    $ArtifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ArtifactRoot))
}

$outDir = Join-Path $ArtifactRoot "publish\$Runtime"

# Read app version from csproj for ZIP naming.
$projectXml = [xml](Get-Content -LiteralPath $Project -Raw)
$version = ($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.0.0' }
$version = ($version.Trim() -replace '[^0-9A-Za-z\.\-_]+', '_')

$zipPath = Join-Path $ArtifactRoot "LyllyPlayer-portable-$version-$Runtime.zip"

New-Item -ItemType Directory -Force -Path (Split-Path $outDir -Parent) | Out-Null
if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

Write-Host "Publishing self-contained $Runtime to $outDir ..." -ForegroundColor Cyan

dotnet publish $Project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Zipping -> $zipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -Force

Write-Host "Done. Run: $outDir\LyllyPlayer.exe" -ForegroundColor Green
