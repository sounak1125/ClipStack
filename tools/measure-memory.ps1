#Requires -Version 5.1
param(
    [string]$ExePath = "",
    [int]$WarmupSeconds = 5
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $root "src\ClipStack.App\bin\Release\net10.0-windows\ClipStack.exe"
}

if (-not (Test-Path $ExePath)) {
    Write-Host "Building Release first..."
    Push-Location $root
    try {
        dotnet build .\src\ClipStack.App\ClipStack.App.csproj -c Release --nologo | Out-Host
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath"
}

$existing = Get-Process -Name "ClipStack" -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "NOTE: ClipStack is already running (PID(s): $($existing.Id -join ', '))."
    Write-Host "Reporting memory for the existing instance(s). This script will not kill them."
    foreach ($p in $existing) {
        $p.Refresh()
        Write-Host ("PID {0}: WorkingSet={1:N1} MB  PrivateMemory={2:N1} MB  (approximate)" -f `
            $p.Id, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB))
    }
    exit 0
}

Write-Host "Starting $ExePath"
$proc = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds $WarmupSeconds
$proc.Refresh()

Write-Host ""
Write-Host "Approximate idle memory after ${WarmupSeconds}s warmup:"
Write-Host ("  WorkingSet:     {0:N1} MB" -f ($proc.WorkingSet64 / 1MB))
Write-Host ("  PrivateMemory:  {0:N1} MB" -f ($proc.PrivateMemorySize64 / 1MB))
Write-Host ""
Write-Host "Leaving the process running. Exit from the tray menu when finished."
Write-Host "These numbers are approximate engineering measurements, not guarantees."
