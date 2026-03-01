# Order System API

This project is our local playground for the order lifecycle: create order, reserve stock, take payment, create shipment, mark delivered.

It is intentionally simple in infrastructure (RabbitMQ + SQLite per service) so we can focus on service boundaries and event flow.

## Services in this solution

- `OrderService`: create/cancel orders and keep order status in sync with downstream events.
- `InventoryService`: reserve/release stock and publish inventory outcomes.
- `PaymentService`: process payment and publish payment result.
- `ShippingService`: create shipment records and publish shipped/delivered events.
- `ProductService`: simple product catalog + stock updates.
- `AuthService`: OTP auth, dev token endpoint, and optional OAuth providers.
- `Shared.Contracts`: event contracts shared by all services.

Happy path event chain:

`OrderCreated -> InventoryReserved -> PaymentCompleted -> OrderShipped -> OrderDelivered`

Failure examples:

`OrderCreated -> InventoryFailed` or `InventoryReserved -> PaymentFailed`

## Runtime stack

- .NET `net10.0`
- ASP.NET Core minimal APIs + OpenAPI in Development
- MassTransit + RabbitMQ (`rabbitmq:3.13-management`)
- EF Core + SQLite (one DB per service)
- Docker Compose for local orchestration

## Quick start (Docker, recommended)

From repo root:

```bash
docker compose up -d --build
```

Endpoints:

- OrderService: `http://localhost:8081`
- ProductService: `http://localhost:8082`
- InventoryService: `http://localhost:8083`
- PaymentService: `http://localhost:8084`
- ShippingService: `http://localhost:8085`
- AuthService: `http://localhost:8086`
- RabbitMQ UI: `http://localhost:15672` (`guest` / `guest`)

Quick health check:

```bash
curl http://localhost:8081/health
curl http://localhost:8082/health
curl http://localhost:8083/health
curl http://localhost:8084/health
curl http://localhost:8085/health
curl http://localhost:8086/health
```

OpenAPI docs are available per service in Development at:

`/openapi/v1.json`

## Required env var

`Auth:SigningKey` must be present when auth is enabled. In Docker Compose this maps to:

`ORDER_SYSTEM_JWT_SIGNING_KEY`

Set it before startup if your shell doesn’t already have it.

PowerShell example:

```powershell
$env:ORDER_SYSTEM_JWT_SIGNING_KEY = "replace-with-a-long-random-secret"
docker compose up -d --build
```

## Current auth toggles in compose

Auth enabled by default:

- `order-service`
- `product-service`

Auth disabled by default:

- `inventory-service`
- `payment-service`
- `shipping-service`

If you change these toggles, adjust your test calls so JWT roles/scopes match each endpoint policy.

## Smoke test

The smoke script does the full local sanity pass: health checks, queue readiness, product creation, order creation, and end-state verification.

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/smoke-test.ps1
```

Useful switches:

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/smoke-test.ps1 -SkipBusReadinessCheck
powershell -ExecutionPolicy Bypass -File ./scripts/smoke-test.ps1 -SkipAuthToken
```

## Phase 2 readiness gate

This script runs restore/build, optional docker clean rebuild, and strict smoke test:

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/phase2-readiness.ps1
```

Minimum pass indicators:

- process exit code `0`
- final order status reaches Delivered (`6`)
- shipping and inventory records are found

## API surface

### OrderService

- `POST /api/orders`
- `GET /api/orders/{orderId:guid}`
- `GET /api/orders/customer/{customerId}`
- `POST /api/orders/{orderId:guid}/cancel`
- `PUT /api/orders/{orderId:guid}/status`

### ProductService

- `GET /api/products`
- `GET /api/products/{id}`
- `POST /api/products`
- `PUT /api/products/{id}/stock`

### InventoryService

- `GET /api/inventory/{productId}`
- `POST /api/inventory/reserve`
- `POST /api/inventory/release`

### PaymentService

- `POST /api/payments/process`
- `GET /api/payments/{orderId:guid}`

### ShippingService

- `POST /api/shipping/create`
- `GET /api/shipping/{orderId:guid}`

### AuthService

- `POST /api/auth/request-code`
- `POST /api/auth/verify-code`
- `POST /api/auth/dev/token` (Development only)
- `GET /api/auth/oauth/start/{provider}`
- `GET /api/auth/oauth/callback/{provider}`

## OAuth notes

Provider config lives in:

`src/AuthService/appsettings.json`

For local Google flow, redirect URI should be:

`http://localhost:8086/api/auth/oauth/callback/google`

Helper scripts in `scripts/`:

- `google-oauth-setup.ps1`
- `google-oauth-open-browser.ps1`
- `google-oauth-preflight-open.ps1`
- `google-oauth-smoke.ps1`

## Running without Docker

Run each service in its own terminal:

```bash
dotnet run --project src/AuthService
dotnet run --project src/OrderService
dotnet run --project src/ProductService
dotnet run --project src/InventoryService
dotnet run --project src/PaymentService
dotnet run --project src/ShippingService
```

RabbitMQ still needs to be running locally (or via Docker).

## Practical notes

- REST Client environment files are in `http-client.env.json` and `rest-client.env.json`.
- `.http` files under each service can be used directly from VS Code REST Client.
- Generated files (`bin/`, `obj/`, local DB files, IDE state) are ignored by `.gitignore`.
