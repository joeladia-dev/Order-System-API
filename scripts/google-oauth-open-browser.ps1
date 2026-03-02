param(
    [string]$BaseUrl = "http://localhost:8086",
    [string]$ReturnUrl = "http://localhost:5173/customer-sign-in"
)

$ErrorActionPreference = "Stop"

try {
    Write-Host "Requesting Google OAuth authorize URL..."
    $encodedReturnUrl = [System.Uri]::EscapeDataString($ReturnUrl)
    $start = Invoke-RestMethod -Uri "$BaseUrl/api/auth/oauth/start/google?returnUrl=$encodedReturnUrl" -Method Get

    if ([string]::IsNullOrWhiteSpace($start.authorizeUrl)) {
        throw "Authorize URL was not returned by start endpoint."
    }

    Write-Host "Provider: $($start.provider)"
    Write-Host "State: $($start.state)"
    Write-Host "ExpiresAt: $($start.expiresAt)"
    Write-Host "ReturnUrl: $ReturnUrl"
    Write-Host "Opening browser..."

    Start-Process $start.authorizeUrl

    Write-Host "Browser opened. Complete Google sign-in and consent."
    Write-Host "Google should redirect to your callback URL with code/state automatically."
    exit 0
}
catch {
    Write-Error "Failed to open Google OAuth browser flow: $($_.Exception.Message)"
    exit 1
}
