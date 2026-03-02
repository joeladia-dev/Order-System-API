param(
    [string]$ContainerName = "order-system-rabbitmq",
    [int]$TimeoutSeconds = 60,
    [switch]$SkipIfRunning
)

$ErrorActionPreference = "Stop"

function Test-PortOpen {
    param(
        [string]$TargetAddress,
        [int]$Port,
        [int]$TimeoutMs = 1200
    )

    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $async = $client.BeginConnect($TargetAddress, $Port, $null, $null)
        $completed = $async.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
        if (-not $completed) {
            $client.Close()
            return $false
        }

        $client.EndConnect($async)
        $client.Close()
        return $true
    }
    catch {
        return $false
    }
}

function Wait-RabbitMqReady {
    param([int]$TimeoutSeconds)

    $authValue = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("guest:guest"))
    $headers = @{ Authorization = "Basic $authValue" }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $result = Invoke-RestMethod -Uri "http://localhost:15672/api/overview" -Method Get -Headers $headers -TimeoutSec 4
            if ($null -ne $result) {
                return
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 1200
    }

    throw "RabbitMQ did not become ready within $TimeoutSeconds seconds."
}

if ((Test-PortOpen -TargetAddress "127.0.0.1" -Port 5672) -and (Test-PortOpen -TargetAddress "127.0.0.1" -Port 15672)) {
    if ($SkipIfRunning) {
        Write-Host "RabbitMQ is already reachable on ports 5672/15672."
        exit 0
    }
}

$dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
if (-not $dockerCommand) {
    throw "Docker CLI was not found. Install/start Docker Desktop or run RabbitMQ locally on 5672/15672."
}

try {
    docker version --format "{{.Server.Version}}" | Out-Null
}
catch {
    throw "Docker daemon is not reachable. Start Docker Desktop, then rerun this script."
}

$existing = docker ps -a --filter "name=^/$ContainerName$" --format "{{.Names}}"
if ([string]::IsNullOrWhiteSpace($existing)) {
    Write-Host "Creating RabbitMQ container '$ContainerName'..."
    docker run -d `
        --name $ContainerName `
        -p 5672:5672 `
        -p 15672:15672 `
        -e RABBITMQ_DEFAULT_USER=guest `
        -e RABBITMQ_DEFAULT_PASS=guest `
        rabbitmq:3.13-management | Out-Null
}
else {
    Write-Host "Starting existing RabbitMQ container '$ContainerName'..."
    docker start $ContainerName | Out-Null
}

Wait-RabbitMqReady -TimeoutSeconds $TimeoutSeconds
Write-Host "RabbitMQ is ready at http://localhost:15672 (guest/guest)."
