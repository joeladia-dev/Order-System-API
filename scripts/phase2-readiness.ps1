param(
    [string]$SolutionPath = "OrderSystem.slnx",
    [switch]$SkipCleanState,
    [switch]$SkipDockerBuild,
    [switch]$SkipSmokeTest,
    [int]$SmokePollAttempts = 60,
    [int]$SmokePollDelayMs = 1000
)

$ErrorActionPreference = "Stop"

function Assert-ExitCode {
    param(
        [int]$ExitCode,
        [string]$Step
    )

    if ($ExitCode -ne 0) {
        throw "$Step failed with exit code $ExitCode."
    }
}

function Assert-CommandAvailable {
    param([string]$CommandName)

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

function Ensure-SigningKey {
    if ([string]::IsNullOrWhiteSpace($Env:ORDER_SYSTEM_JWT_SIGNING_KEY)) {
        $bytes = New-Object byte[] 48
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($bytes)
        $rng.Dispose()
        $Env:ORDER_SYSTEM_JWT_SIGNING_KEY = [Convert]::ToBase64String($bytes)
        Write-Host "Generated ephemeral ORDER_SYSTEM_JWT_SIGNING_KEY for this session."
    }

    if ([string]::IsNullOrWhiteSpace($Env:AUTH_ISSUER)) {
        $Env:AUTH_ISSUER = "OrderSystem.Auth"
    }

    if ([string]::IsNullOrWhiteSpace($Env:AUTH_AUDIENCE)) {
        $Env:AUTH_AUDIENCE = "OrderSystem.Api"
    }

    if ([string]::IsNullOrWhiteSpace($Env:ORDER_AUTH_ENABLED)) {
        $Env:ORDER_AUTH_ENABLED = "true"
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$smokeScriptPath = Join-Path $scriptRoot "smoke-test.ps1"

Push-Location $repoRoot
try {
    Assert-CommandAvailable -CommandName "dotnet"
    Assert-CommandAvailable -CommandName "docker"
    Ensure-SigningKey

    Write-Host "[1/3] Restoring and building solution..."
    & dotnet restore $SolutionPath
    Assert-ExitCode -ExitCode $LASTEXITCODE -Step "dotnet restore"

    & dotnet build $SolutionPath -c Debug --no-restore
    Assert-ExitCode -ExitCode $LASTEXITCODE -Step "dotnet build"

    Write-Host "[2/3] Preparing Docker environment..."
    if (-not $SkipDockerBuild) {
        if (-not $SkipCleanState) {
            & docker compose down -v
            Assert-ExitCode -ExitCode $LASTEXITCODE -Step "docker compose down -v"
        }

        & docker compose up -d --build
        Assert-ExitCode -ExitCode $LASTEXITCODE -Step "docker compose up -d --build"
    }
    else {
        Write-Host "Skipping Docker compose rebuild/start as requested."
    }

    Write-Host "[3/3] Running strict smoke test..."
    if (-not $SkipSmokeTest) {
        & $smokeScriptPath -PollAttempts $SmokePollAttempts -PollDelayMs $SmokePollDelayMs
        Assert-ExitCode -ExitCode $LASTEXITCODE -Step "smoke-test.ps1"
    }
    else {
        Write-Host "Skipping smoke test as requested."
    }

    Write-Host "Phase 2 readiness check passed."
    exit 0
}
catch {
    Write-Error "Phase 2 readiness check failed: $($_.Exception.Message)"
    exit 1
}
finally {
    Pop-Location
}
