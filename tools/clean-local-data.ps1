#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$data = Join-Path $env:LOCALAPPDATA "ClipStack"
if (-not (Test-Path $data)) {
    Write-Host "No ClipStack data directory found at $data"
    exit 0
}

Write-Host "Removing $data"
Remove-Item -LiteralPath $data -Recurse -Force
Write-Host "Done."
