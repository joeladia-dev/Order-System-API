param(
    [string]$BaseUrl = "http://localhost:8086"
)

$ErrorActionPreference = "Stop"

function Invoke-JsonGet {
    param([string]$Url)

    return Invoke-RestMethod -Uri $Url -Method Get
}

try {
    Write-Host "[1/2] Checking OAuth start endpoint..."
    $startResponse = Invoke-JsonGet -Url "$BaseUrl/api/auth/oauth/start/google"

    if ([string]::IsNullOrWhiteSpace($startResponse.authorizeUrl) -or [string]::IsNullOrWhiteSpace($startResponse.state)) {
        throw "Start endpoint did not return authorizeUrl/state."
    }

    Write-Host "Start endpoint OK."
    Write-Host "Provider: $($startResponse.provider)"
    Write-Host "Authorize URL host: $(([Uri]$startResponse.authorizeUrl).Host)"

    Write-Host "[2/2] Checking callback handling with fake code..."
    $callbackUrl = "$BaseUrl/api/auth/oauth/callback/google?code=fake-code&state=$($startResponse.state)"

    try {
        Invoke-JsonGet -Url $callbackUrl | Out-Null
        throw "Callback unexpectedly succeeded with fake code."
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) {
            throw "Callback failed without HTTP response: $($_.Exception.Message)"
        }

        $statusCode = [int]$response.StatusCode
        Write-Host "Callback returned status: $statusCode"

        if ($statusCode -ne 400) {
            throw "Unexpected callback status with fake code: $statusCode"
        }
    }

    Write-Host "Google OAuth smoke test passed."
    exit 0
}
catch {
    Write-Error "Google OAuth smoke test failed: $($_.Exception.Message)"
    exit 1
}
