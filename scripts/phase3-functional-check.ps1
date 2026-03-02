param(
    [string]$BaseOrderUrl = "http://localhost:8081",
    [string]$BaseProductUrl = "http://localhost:8082",
    [string]$BaseInventoryUrl = "http://localhost:8083",
    [string]$BasePaymentUrl = "http://localhost:8084",
    [string]$BaseShippingUrl = "http://localhost:8085",
    [string]$BaseAuthUrl = "http://localhost:8086",
    [string]$FrontendUrl = "http://localhost:5173",
    [switch]$SkipRabbitMqBootstrap,
    [switch]$SkipFrontendChecks
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $SkipRabbitMqBootstrap) {
    & (Join-Path $scriptRoot "bootstrap-rabbitmq.ps1") -SkipIfRunning
}

$healthTargets = @(
    "$BaseOrderUrl/health",
    "$BaseProductUrl/health",
    "$BaseInventoryUrl/health",
    "$BasePaymentUrl/health",
    "$BaseShippingUrl/health",
    "$BaseAuthUrl/health"
)

foreach ($target in $healthTargets) {
    $response = Invoke-WebRequest -Uri $target -UseBasicParsing -TimeoutSec 8
    if ($response.StatusCode -ne 200) {
        throw "Health check failed for $target"
    }
}

Write-Host "API health checks passed."

if (-not $SkipFrontendChecks) {
    $frontendResponse = Invoke-WebRequest -Uri $FrontendUrl -UseBasicParsing -TimeoutSec 8
    if ($frontendResponse.StatusCode -ne 200) {
        throw "Frontend check failed at $FrontendUrl"
    }

    $proxyProducts = Invoke-WebRequest -Uri "$FrontendUrl/api/products" -UseBasicParsing -TimeoutSec 8
    if ($proxyProducts.StatusCode -ne 200) {
        throw "Vite proxy check failed for /api/products"
    }

    Write-Host "Frontend and proxy checks passed."
}

& (Join-Path $scriptRoot "smoke-test.ps1")

Write-Host "Phase 3 functional check passed."
