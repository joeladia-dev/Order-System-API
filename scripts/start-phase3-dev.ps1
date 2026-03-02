param(
    [string]$FrontendRoot = "..\Order-System-Frontend",
    [switch]$SkipRabbitMq,
    [switch]$SkipFrontend,
    [switch]$SkipApi
)

$ErrorActionPreference = "Stop"

function Ensure-SigningKey {
    if (-not [string]::IsNullOrWhiteSpace($Env:Auth__SigningKey)) {
        return $Env:Auth__SigningKey
    }

    if (-not [string]::IsNullOrWhiteSpace($Env:ORDER_SYSTEM_JWT_SIGNING_KEY)) {
        $Env:Auth__SigningKey = $Env:ORDER_SYSTEM_JWT_SIGNING_KEY
        return $Env:Auth__SigningKey
    }

    $bytes = New-Object byte[] 48
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()

    $generated = [Convert]::ToBase64String($bytes)
    $Env:Auth__SigningKey = $generated
    $Env:ORDER_SYSTEM_JWT_SIGNING_KEY = $generated

    return $generated
}

function Start-ServiceProcess {
    param(
        [string]$RepoRoot,
        [string]$ProjectPath,
        [int]$Port,
        [string]$SigningKey
    )

    $command = @"
Set-Location '$RepoRoot'
`$env:ASPNETCORE_ENVIRONMENT='Development'
`$env:ASPNETCORE_URLS='http://localhost:$Port'
`$env:Auth__SigningKey='$SigningKey'
`$env:ORDER_SYSTEM_JWT_SIGNING_KEY='$SigningKey'
dotnet run --no-launch-profile --project '$ProjectPath'
"@

    Start-Process -FilePath "powershell" -ArgumentList @(
        "-NoExit",
        "-ExecutionPolicy", "Bypass",
        "-Command", $command
    ) | Out-Null
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$frontendResolved = Resolve-Path (Join-Path $repoRoot $FrontendRoot) -ErrorAction SilentlyContinue

if (-not $SkipRabbitMq) {
    & (Join-Path $scriptRoot "bootstrap-rabbitmq.ps1") -SkipIfRunning
}

$signingKey = Ensure-SigningKey

if (-not $SkipApi) {
    $services = @(
        @{ Project = "src/AuthService"; Port = 8086 },
        @{ Project = "src/OrderService"; Port = 8081 },
        @{ Project = "src/ProductService"; Port = 8082 },
        @{ Project = "src/InventoryService"; Port = 8083 },
        @{ Project = "src/PaymentService"; Port = 8084 },
        @{ Project = "src/ShippingService"; Port = 8085 }
    )

    foreach ($service in $services) {
        Start-ServiceProcess -RepoRoot $repoRoot -ProjectPath $service.Project -Port $service.Port -SigningKey $signingKey
    }
}

if (-not $SkipFrontend) {
    if (-not $frontendResolved) {
        throw "Frontend path '$FrontendRoot' was not found from repo root."
    }

    $frontendCommand = @"
Set-Location '$frontendResolved'
    npm run dev -- --host localhost --port 5173
"@

    Start-Process -FilePath "powershell" -ArgumentList @(
        "-NoExit",
        "-ExecutionPolicy", "Bypass",
        "-Command", $frontendCommand
    ) | Out-Null
}

Write-Host "Phase 3 dev stack launched."
Write-Host "Frontend: http://localhost:5173"
Write-Host "APIs: http://localhost:8081..8086"
Write-Host "RabbitMQ: http://localhost:15672 (guest/guest)"
