param(
    [Parameter(Mandatory = $true)]
    [string]$JsonPath,
    [string]$RedirectUri = "http://localhost:8086/api/auth/oauth/callback/google",
    [switch]$StartAuthService = $true,
    [switch]$PersistUserEnv
)

$ErrorActionPreference = "Stop"

function Assert-CommandAvailable {
    param([string]$CommandName)

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

function Assert-ExitCode {
    param(
        [int]$ExitCode,
        [string]$Step
    )

    if ($ExitCode -ne 0) {
        throw "$Step failed with exit code $ExitCode."
    }
}

function Set-ScopedEnv {
    param(
        [string]$Name,
        [string]$Value,
        [bool]$Persist
    )

    Set-Item -Path "Env:$Name" -Value $Value
    if ($Persist) {
        [Environment]::SetEnvironmentVariable($Name, $Value, "User")
    }
}

function Ensure-SigningKey {
    param([bool]$Persist)

    if ([string]::IsNullOrWhiteSpace($Env:ORDER_SYSTEM_JWT_SIGNING_KEY)) {
        $bytes = New-Object byte[] 48
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($bytes)
        $rng.Dispose()
        $generated = [Convert]::ToBase64String($bytes)
        Set-ScopedEnv -Name "ORDER_SYSTEM_JWT_SIGNING_KEY" -Value $generated -Persist $Persist
        Write-Host "Generated ORDER_SYSTEM_JWT_SIGNING_KEY for this environment."
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")

Push-Location $repoRoot
try {
    Assert-CommandAvailable -CommandName "docker"

    if (-not (Test-Path $JsonPath)) {
        throw "OAuth JSON file not found at '$JsonPath'."
    }

    $json = Get-Content $JsonPath -Raw | ConvertFrom-Json
    if ($null -eq $json.web) {
        throw "Invalid OAuth JSON format. Expected a top-level 'web' object."
    }

    $clientId = [string]$json.web.client_id
    $clientSecret = [string]$json.web.client_secret

    if ([string]::IsNullOrWhiteSpace($clientId) -or [string]::IsNullOrWhiteSpace($clientSecret)) {
        throw "OAuth JSON is missing client_id or client_secret."
    }

    $persist = $PersistUserEnv.IsPresent
    Ensure-SigningKey -Persist $persist

    Set-ScopedEnv -Name "AUTH_ISSUER" -Value "OrderSystem.Auth" -Persist $persist
    Set-ScopedEnv -Name "AUTH_AUDIENCE" -Value "OrderSystem.Api" -Persist $persist

    Set-ScopedEnv -Name "ORDER_AUTH_ENABLED" -Value "true" -Persist $persist
    Set-ScopedEnv -Name "PRODUCT_AUTH_ENABLED" -Value "false" -Persist $persist
    Set-ScopedEnv -Name "INVENTORY_AUTH_ENABLED" -Value "false" -Persist $persist
    Set-ScopedEnv -Name "PAYMENT_AUTH_ENABLED" -Value "false" -Persist $persist
    Set-ScopedEnv -Name "SHIPPING_AUTH_ENABLED" -Value "false" -Persist $persist

    Set-ScopedEnv -Name "GOOGLE_OAUTH_ENABLED" -Value "true" -Persist $persist
    Set-ScopedEnv -Name "GOOGLE_CLIENT_ID" -Value $clientId -Persist $persist
    Set-ScopedEnv -Name "GOOGLE_CLIENT_SECRET" -Value $clientSecret -Persist $persist
    Set-ScopedEnv -Name "GOOGLE_REDIRECT_URI" -Value $RedirectUri -Persist $persist

    Write-Host "Google OAuth environment variables configured in current session."
    if ($persist) {
        Write-Host "Variables also persisted at user scope."
    }

    Write-Host "ClientId loaded: $($clientId.Substring(0, [Math]::Min(12, $clientId.Length)))..."
    Write-Host "Redirect URI: $RedirectUri"

    if ($StartAuthService) {
        Write-Host "Starting auth-service via Docker Compose..."
        & docker compose up -d --build auth-service
        Assert-ExitCode -ExitCode $LASTEXITCODE -Step "docker compose up -d --build auth-service"
        Write-Host "auth-service started."
    }
}
catch {
    Write-Error "Google OAuth setup failed: $($_.Exception.Message)"
    exit 1
}
finally {
    Pop-Location
}
