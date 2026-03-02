param(
    [switch]$RemoveVolumes,
    [switch]$StopLocalApiProcesses
)

$ErrorActionPreference = "Stop"

function Stop-ProcessIfRunning {
    param([int]$ProcessId)

    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Stop-FrontendFromPidFile {
    param([string]$RunDir)

    $pidFile = Join-Path $RunDir "frontend-dev.pid"
    if (-not (Test-Path $pidFile)) {
        return $false
    }

    $content = (Get-Content $pidFile -Raw).Trim()
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue

    if ($content -notmatch '^\d+$') {
        return $false
    }

    return (Stop-ProcessIfRunning -ProcessId ([int]$content))
}

function Stop-FrontendByPort {
    $stopped = $false

    try {
        $listeners = Get-NetTCPConnection -State Listen -LocalPort 5173 -ErrorAction Stop
        $pids = $listeners | Select-Object -ExpandProperty OwningProcess -Unique

        foreach ($processId in $pids) {
            if (Stop-ProcessIfRunning -ProcessId $processId) {
                $stopped = $true
            }
        }
    }
    catch {
    }

    return $stopped
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$runDir = Join-Path $repoRoot ".run"

$frontendStopped = $false
if (Test-Path $runDir) {
    $frontendStopped = Stop-FrontendFromPidFile -RunDir $runDir
}

if (-not $frontendStopped) {
    $frontendStopped = Stop-FrontendByPort
}

if ($frontendStopped) {
    Write-Host "Frontend dev process stopped."
}
else {
    Write-Host "No frontend dev process was stopped (it may not be running)."
}

Push-Location $repoRoot
try {
    $composeArgs = @("compose", "down")
    if ($RemoveVolumes) {
        $composeArgs += "-v"
    }

    & docker @composeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose down failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if ($StopLocalApiProcesses) {
    $serviceNames = @("AuthService", "OrderService", "ProductService", "InventoryService", "PaymentService", "ShippingService")
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $serviceNames -contains $_.ProcessName } |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Host "Local API processes stopped if any were running."
}

Write-Host "Docker stack stopped cleanly."
