#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Assert-Version([string]$v) {
    if ($v -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9\.-]+)?$') {
        throw "Invalid version '$v'. Expected semver like 0.1.0"
    }
}

# $ErrorActionPreference does not make a native executable's non-zero exit code
# terminating on every PowerShell version, so each step is checked explicitly.
# Without this a failing test run would sail straight through to a published release.
function Assert-ExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$step failed with exit code $LASTEXITCODE."
    }
}

Assert-Version $Version

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "==> ClipStack release build $Version" -ForegroundColor Cyan

Write-Host "==> Restoring local tools (vpk)"
dotnet tool restore
Assert-ExitCode "Tool restore"

Write-Host "==> Restoring packages"
dotnet restore .\ClipStack.sln
Assert-ExitCode "Package restore"

Write-Host "==> Running tests"
dotnet test .\ClipStack.sln -c Release --nologo
Assert-ExitCode "Tests"

$publishDir = Join-Path $root "artifacts\publish\win-x64"
$packDir = Join-Path $root "artifacts\releases"
$packageIcon = Join-Path $root "src\ClipStack.App\Assets\clipstack.ico"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $packDir | Out-Null

Write-Host "==> Publishing self-contained win-x64"
dotnet publish .\src\ClipStack.App\ClipStack.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version.0 `
    -p:FileVersion=$Version.0 `
    -p:InformationalVersion=$Version `
    -o $publishDir
Assert-ExitCode "Publish"

# Branding stays deliberately minimal: no --splashImage, no banner, no logo, so the
# installer shows the app icon and a progress bar and nothing else.
#
# --shortcuts StartMenuRoot overrides Velopack's Desktop,StartMenuRoot default. ClipStack
# is a tray app that enables Start with Windows on install, so a desktop icon is clutter
# the user did not ask for. The Start Menu entry stays: without it there is no way to
# launch ClipStack again after exiting from the tray.
Write-Host "==> Packaging with Velopack (vpk)"
dotnet tool run vpk pack `
    --packId ClipStack.Desktop `
    --packTitle ClipStack `
    --packAuthors ClipStack `
    --packVersion $Version `
    --channel win `
    --packDir $publishDir `
    --mainExe ClipStack.exe `
    --icon $packageIcon `
    --shortcuts StartMenuRoot `
    --outputDir $packDir
Assert-ExitCode "Velopack packaging"

Write-Host ""
Write-Host "Release complete." -ForegroundColor Green
Write-Host "Publish output: $publishDir"
Write-Host "Installer/update files: $packDir"
Get-ChildItem $packDir | ForEach-Object { Write-Host ("  - " + $_.FullName) }
