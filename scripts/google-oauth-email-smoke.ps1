param(
    [string]$BaseUrl = "http://localhost:8086",
    [string]$AuthSettingsPath = "src/AuthService/appsettings.json"
)

$ErrorActionPreference = "Stop"
Write-Warning "google-oauth-email-smoke.ps1 is a legacy alias. Use google-oauth-smoke.ps1 directly."

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $scriptRoot "google-oauth-smoke.ps1"

if (-not (Test-Path $target)) {
    Write-Error "Target script not found: $target"
    exit 1
}

& $target -BaseUrl $BaseUrl
exit $LASTEXITCODE
