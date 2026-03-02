param(
    [string]$BaseOrderUrl = "http://localhost:8081",
    [string]$BaseProductUrl = "http://localhost:8082",
    [string]$BaseInventoryUrl = "http://localhost:8083",
    [string]$BasePaymentUrl = "http://localhost:8084",
    [string]$BaseShippingUrl = "http://localhost:8085",
    [string]$BaseAuthUrl = "http://localhost:8086",
    [string]$RabbitMqApiUrl = "http://localhost:15672",
    [string]$RabbitMqUsername = "guest",
    [string]$RabbitMqPassword = "guest",
    [int]$PollAttempts = 20,
    [int]$PollDelayMs = 600,
    [switch]$SkipBusReadinessCheck,
    [switch]$SkipAuthToken
)

$ErrorActionPreference = "Stop"

function Assert-Status200 {
    param(
        [string]$Name,
        [string]$Url
    )

    $statusCode = (& curl.exe -s -o NUL -w "%{http_code}" --max-time 10 $Url)
    if ($statusCode -ne "200") {
        throw "$Name health check failed at $Url (HTTP $statusCode)."
    }
}

function Wait-Status200 {
    param(
        [string]$Name,
        [string]$Url,
        [int]$Attempts,
        [int]$DelayMs
    )

    for ($i = 0; $i -lt $Attempts; $i++) {
        $statusCode = (& curl.exe -s -o NUL -w "%{http_code}" --max-time 10 $Url)
        if ($statusCode -eq "200") {
            return
        }

        Start-Sleep -Milliseconds $DelayMs
    }

    throw "$Name health check failed at $Url (HTTP $statusCode)."
}

function Wait-OrderStatus {
    param(
        [Guid]$OrderId,
        [int[]]$ExpectedStatuses,
        [int]$Attempts,
        [int]$DelayMs,
        [string]$OrderUrl,
        [hashtable]$Headers
    )

    $latest = $null
    for ($i = 0; $i -lt $Attempts; $i++) {
        Start-Sleep -Milliseconds $DelayMs
        $latest = Invoke-RestMethod -Uri "$OrderUrl/api/orders/$OrderId" -Method Get -Headers $Headers
        if ($ExpectedStatuses -contains [int]$latest.status) {
            return $latest
        }
    }

    return $latest
}

function Get-AuthBearerToken {
    param(
        [string]$AuthUrl,
        [int]$Attempts,
        [int]$DelayMs
    )

    $email = "smoke+$([Guid]::NewGuid().ToString('N').Substring(0, 8))@example.com"
    $requestBody = @{ email = $email } | ConvertTo-Json
    $requestCodeResponse = Invoke-RestMethod -Uri "$AuthUrl/api/auth/request-code" -Method Post -ContentType "application/json" -Body $requestBody

    $code = $requestCodeResponse.verificationCode
    if ([string]::IsNullOrWhiteSpace($code)) {
        throw "AuthService did not return a verification code in Development mode."
    }

    $verifyBody = @{ email = $email; code = $code } | ConvertTo-Json
    $verifyResponse = Invoke-RestMethod -Uri "$AuthUrl/api/auth/verify-code" -Method Post -ContentType "application/json" -Body $verifyBody

    if ([string]::IsNullOrWhiteSpace($verifyResponse.accessToken)) {
        throw "AuthService verify-code did not return an access token."
    }

    return $verifyResponse.accessToken
}

function Get-DevAdminBearerToken {
    param(
        [string]$AuthUrl
    )

    $requestBody = @{
        email = "admin-smoke@example.com"
        roles = @("admin")
        scopes = @("orders.read", "orders.write", "catalog.write", "internal")
    } | ConvertTo-Json -Depth 5

    $response = Invoke-RestMethod -Uri "$AuthUrl/api/auth/dev/token" -Method Post -ContentType "application/json" -Body $requestBody
    if ([string]::IsNullOrWhiteSpace($response.accessToken)) {
        throw "AuthService dev token endpoint did not return an access token."
    }

    return $response.accessToken
}

function Get-OptionalJson {
    param(
        [string]$Url
    )

    try {
        return Invoke-RestMethod -Uri $Url -Method Get
    } catch {
        return $null
    }
}

function Wait-QueueBindings {
    param(
        [string]$ApiUrl,
        [string]$Username,
        [string]$Password,
        [int]$Attempts,
        [int]$DelayMs
    )

    $expectedQueues = @(
        "inventory-order-created",
        "inventory-order-cancelled",
        "order-inventory-reserved",
        "order-inventory-failed",
        "order-payment-completed",
        "order-payment-failed",
        "order-order-shipped",
        "order-order-delivered",
        "payment-inventory-reserved",
        "payment-inventory-failed",
        "shipping-payment-completed"
    )

    $authValue = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("$Username`:$Password"))
    $headers = @{ Authorization = "Basic $authValue" }

    for ($i = 0; $i -lt $Attempts; $i++) {
        try {
            $queues = Invoke-RestMethod -Uri "$ApiUrl/api/queues" -Method Get -Headers $headers
            $queueNames = @($queues | Select-Object -ExpandProperty name)
            $missing = @($expectedQueues | Where-Object { $_ -notin $queueNames })

            if ($missing.Count -eq 0) {
                return
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds $DelayMs
    }

    throw "Message bus readiness check failed. Not all expected consumer queues were detected."
}

Write-Host "Running health checks..."
Wait-Status200 -Name "OrderService" -Url "$BaseOrderUrl/health" -Attempts $PollAttempts -DelayMs $PollDelayMs
Wait-Status200 -Name "ProductService" -Url "$BaseProductUrl/health" -Attempts $PollAttempts -DelayMs $PollDelayMs
Wait-Status200 -Name "InventoryService" -Url "$BaseInventoryUrl/health" -Attempts $PollAttempts -DelayMs $PollDelayMs
Wait-Status200 -Name "PaymentService" -Url "$BasePaymentUrl/health" -Attempts $PollAttempts -DelayMs $PollDelayMs
Wait-Status200 -Name "ShippingService" -Url "$BaseShippingUrl/health" -Attempts $PollAttempts -DelayMs $PollDelayMs
Wait-Status200 -Name "AuthService" -Url "$BaseAuthUrl/health" -Attempts $PollAttempts -DelayMs $PollDelayMs
Write-Host "Health checks passed."

if (-not $SkipBusReadinessCheck) {
    Write-Host "Waiting for message bus queue bindings..."
    Wait-QueueBindings -ApiUrl $RabbitMqApiUrl -Username $RabbitMqUsername -Password $RabbitMqPassword -Attempts $PollAttempts -DelayMs $PollDelayMs
    Write-Host "Message bus bindings ready."
}

$orderHeaders = @{}
$productWriteHeaders = @{}
if (-not $SkipAuthToken) {
    Write-Host "Requesting smoke auth token..."
    $accessToken = Get-AuthBearerToken -AuthUrl $BaseAuthUrl -Attempts $PollAttempts -DelayMs $PollDelayMs
    $orderHeaders["Authorization"] = "Bearer $accessToken"
    $adminToken = Get-DevAdminBearerToken -AuthUrl $BaseAuthUrl
    $productWriteHeaders["Authorization"] = "Bearer $adminToken"
    Write-Host "Auth token acquired."
}

$productId = "sku-smoke-" + (Get-Random -Minimum 1000 -Maximum 9999)
$productPayload = @{
    id = $productId
    name = "Smoke Item $productId"
    price = 19.99
    stock = 25
} | ConvertTo-Json

Write-Host "Creating product $productId..."
$productResponse = Invoke-RestMethod -Uri "$BaseProductUrl/api/products" -Method Post -Headers $productWriteHeaders -ContentType "application/json" -Body $productPayload -TimeoutSec 20

$orderPayload = @{
    customerId = "cust-smoke"
    shippingAddress = "123 Test St"
    paymentMethod = "card"
    items = @(
        @{
            productId = $productId
            quantity = 1
        }
    )
} | ConvertTo-Json -Depth 5

Write-Host "Creating order..."
try {
    $orderCreated = Invoke-RestMethod -Uri "$BaseOrderUrl/api/orders" -Method Post -Headers $orderHeaders -ContentType "application/json" -Body $orderPayload -TimeoutSec 20
}
catch {
    if ($_.Exception.Response) {
        throw "Order creation failed with HTTP $([int]$_.Exception.Response.StatusCode). Ensure RabbitMQ is running and reachable."
    }

    throw
}
$orderId = [Guid]$orderCreated.orderId
Write-Host "Order created: $orderId"

$afterCreate = Wait-OrderStatus -OrderId $orderId -ExpectedStatuses @(4, 6) -Attempts $PollAttempts -DelayMs $PollDelayMs -OrderUrl $BaseOrderUrl -Headers $orderHeaders
if (-not $afterCreate) {
    throw "Order state polling failed for order $orderId."
}

if ([int]$afterCreate.status -eq 6) {
    $finalOrder = $afterCreate
    $extendedChainCompleted = $true
} else {
    Write-Host "Order reached status $($afterCreate.status). Triggering payment success to validate remaining event chain..."

    $paymentPayload = @{
        orderId = $orderId
        amount = 100.00
        success = $true
        correlationId = [Guid]::NewGuid()
    } | ConvertTo-Json

    $paymentTrigger = Invoke-RestMethod -Uri "$BasePaymentUrl/api/payments/process" -Method Post -ContentType "application/json" -Body $paymentPayload -TimeoutSec 20
    $finalOrder = Wait-OrderStatus -OrderId $orderId -ExpectedStatuses @(6) -Attempts $PollAttempts -DelayMs $PollDelayMs -OrderUrl $BaseOrderUrl -Headers $orderHeaders

    $extendedChainCompleted = ([int]$finalOrder.status -eq 6)
}

$payment = $null
for ($i = 0; $i -lt $PollAttempts; $i++) {
    $payment = Get-OptionalJson -Url "$BasePaymentUrl/api/payments/$orderId"
    if ($payment) { break }
    Start-Sleep -Milliseconds $PollDelayMs
}

if (-not $payment) {
    throw "Payment record was not found for order $orderId."
}

$shipping = Get-OptionalJson -Url "$BaseShippingUrl/api/shipping/$orderId"
$inventory = Get-OptionalJson -Url "$BaseInventoryUrl/api/inventory/$productId"

$result = [PSCustomObject]@{
    ProductId = $productId
    OrderId = $orderId
    FinalOrderStatus = [int]$finalOrder.status
    PaymentStatus = [int]$payment.status
    ShippingFound = [bool]$shipping
    ShippingStatus = if ($shipping) { [int]$shipping.status } else { $null }
    TrackingNumber = if ($shipping) { $shipping.trackingNumber } else { $null }
    InventoryFound = [bool]$inventory
    InventoryReserved = if ($inventory) { [int]$inventory.reserved } else { $null }
    ExtendedChainCompleted = $extendedChainCompleted
}

if ([int]$finalOrder.status -ne 6) {
    throw "Order did not reach Delivered status (expected 6, got $([int]$finalOrder.status))."
}

if (-not $extendedChainCompleted) {
    throw "Extended PaymentCompleted -> Shipping -> Delivered path did not complete for order $orderId."
}

if (-not $shipping) {
    throw "Shipping record was not found for order $orderId."
}

if (-not $inventory) {
    throw "Inventory record was not found for product $productId."
}

if ([int]$inventory.reserved -lt 1) {
    throw "Inventory reserved quantity is invalid for product $productId (reserved=$([int]$inventory.reserved))."
}

Write-Host "Smoke test passed (strict E2E path)."
$result | Format-List
