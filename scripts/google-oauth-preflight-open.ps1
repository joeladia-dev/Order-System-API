param(
    [string]$BaseUrl = "http://localhost:8086",
    [string]$ExpectedRedirectUri = "http://localhost:8086/api/auth/oauth/callback/google"
)

$ErrorActionPreference = "Stop"

function Get-QueryValue {
    param(
        [string]$Url,
        [string]$Key
    )

    $uri = [System.Uri]$Url
    $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
    return $query[$Key]
}

try {
    Write-Host "[1/3] Checking auth-service health..."
    $health = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get
    if ($health.status -ne "healthy") {
        throw "Auth service is not healthy at $BaseUrl."
    }
    Write-Host "Auth service is healthy."

    Write-Host "[2/3] Requesting OAuth start payload..."
    $start = Invoke-RestMethod -Uri "$BaseUrl/api/auth/oauth/start/google" -Method Get
    if ([string]::IsNullOrWhiteSpace($start.authorizeUrl)) {
        throw "Start endpoint did not return authorizeUrl."
    }

    $redirectInRequest = Get-QueryValue -Url $start.authorizeUrl -Key "redirect_uri"
    if ([string]::IsNullOrWhiteSpace($redirectInRequest)) {
        throw "authorizeUrl is missing redirect_uri query parameter."
    }

    if ($redirectInRequest -ne $ExpectedRedirectUri) {
        throw "Redirect URI mismatch. Expected '$ExpectedRedirectUri' but got '$redirectInRequest'."
    }

    Write-Host "[3/3] Preflight passed. Opening browser..."
    Write-Host "State: $($start.state)"
    Write-Host "ExpiresAt: $($start.expiresAt)"
    Start-Process $start.authorizeUrl
    Write-Host "Browser opened. Complete Google sign-in now."
    exit 0
}
catch {
    Write-Error "OAuth preflight failed: $($_.Exception.Message)"
    exit 1
}
