param(
    [string]$FrontendRoot = "..\Order-System-Frontend",
    [switch]$NoBuild,
    [switch]$SkipFrontend,
    [switch]$SkipHealthChecks
)

$ErrorActionPreference = "Stop"

function Ensure-RunStateDirectory {
    param([string]$RepoRoot)

    $runDir = Join-Path $RepoRoot ".run"
    if (-not (Test-Path $runDir)) {
        New-Item -ItemType Directory -Path $runDir | Out-Null
    }

    return $runDir
}

function Test-ProcessRunning {
    param([int]$ProcessId)

    try {
        Get-Process -Id $ProcessId -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Start-FrontendDevServer {
    param(
        [string]$FrontendPath,
        [string]$RunDir
    )

    $pidFile = Join-Path $RunDir "frontend-dev.pid"
    if (Test-Path $pidFile) {
        $existingPid = (Get-Content $pidFile -Raw).Trim()
        if ($existingPid -match '^\d+$' -and (Test-ProcessRunning -ProcessId ([int]$existingPid))) {
            Write-Host "Frontend dev server already running (PID $existingPid)."
            return
        }

        Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    }

    $frontendCommand = @"
Set-Location '$FrontendPath'
npm run dev -- --host localhost --port 5173
"@

    $proc = Start-Process -FilePath "powershell" -ArgumentList @(
        "-NoExit",
        "-ExecutionPolicy", "Bypass",
        "-Command", $frontendCommand
    ) -PassThru

    $proc.Id | Out-File -FilePath $pidFile -Encoding ascii -Force
    Write-Host "Frontend dev server started (PID $($proc.Id))."
}

function Wait-Health {
    param(
        [string[]]$Urls,
        [int]$Attempts = 40,
        [int]$DelayMs = 1000
    )

    foreach ($url in $Urls) {
        $healthy = $false

        for ($i = 0; $i -lt $Attempts; $i++) {
            try {
                $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
                if ($resp.StatusCode -eq 200) {
                    $healthy = $true
                    break
                }
            }
            catch {
            }

            Start-Sleep -Milliseconds $DelayMs
        }

        if (-not $healthy) {
            throw "Health check failed for $url"
        }
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$frontendPath = Resolve-Path (Join-Path $repoRoot $FrontendRoot) -ErrorAction SilentlyContinue
$runDir = Ensure-RunStateDirectory -RepoRoot $repoRoot

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker CLI is not installed or not available in PATH."
}

try {
    docker version --format "{{.Server.Version}}" | Out-Null
}
catch {
    throw "Docker daemon is not reachable. Start Docker Desktop first."
}

$composeArgs = @("compose", "up", "-d")
if (-not $NoBuild) {
    $composeArgs += "--build"
}

Push-Location $repoRoot
try {
    & docker @composeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose up failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (-not $SkipFrontend) {
    if (-not $frontendPath) {
        throw "Frontend path '$FrontendRoot' was not found from repo root."
    }

    Start-FrontendDevServer -FrontendPath $frontendPath -RunDir $runDir
}

if (-not $SkipHealthChecks) {
    Wait-Health -Urls @(
        "http://localhost:8081/health",
        "http://localhost:8082/health",
        "http://localhost:8083/health",
        "http://localhost:8084/health",
        "http://localhost:8085/health",
        "http://localhost:8086/health"
    )

    if (-not $SkipFrontend) {
        Wait-Health -Urls @("http://localhost:5173") -Attempts 25 -DelayMs 800
    }
}

Write-Host "Full stack started."
Write-Host "Frontend: http://localhost:5173"
Write-Host "APIs: http://localhost:8081..8086"
Write-Host "RabbitMQ: http://localhost:15672 (guest/guest)"
