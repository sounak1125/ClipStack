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

Assert-Version $Version

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "==> ClipStack release build $Version" -ForegroundColor Cyan

Write-Host "==> Restoring local tools (vpk)"
dotnet tool restore

Write-Host "==> Restoring packages"
dotnet restore .\ClipStack.sln

Write-Host "==> Running tests"
dotnet test .\ClipStack.sln -c Release --nologo

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
    --outputDir $packDir

if ($LASTEXITCODE -ne 0) {
    throw "Velopack packaging failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Release complete." -ForegroundColor Green
Write-Host "Publish output: $publishDir"
Write-Host "Installer/update files: $packDir"
Get-ChildItem $packDir | ForEach-Object { Write-Host ("  - " + $_.FullName) }
