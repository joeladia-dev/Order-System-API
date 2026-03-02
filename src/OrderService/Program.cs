using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Shared.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var productServiceOptions = builder.Configuration.GetSection("ProductService").Get<ProductServiceOptions>() ?? new ProductServiceOptions();

if (authOptions.Enabled && string.IsNullOrWhiteSpace(authOptions.SigningKey))
{
    throw new InvalidOperationException("Auth is enabled but Auth:SigningKey is missing. Set ORDER_SYSTEM_JWT_SIGNING_KEY.");
}

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("OrderDb") ?? "Data Source=orders.db"));

builder.Services.AddMassTransit(configurator =>
{
    configurator.AddConsumer<InventoryReservedConsumer>();
    configurator.AddConsumer<InventoryFailedConsumer>();
    configurator.AddConsumer<PaymentCompletedConsumer>();
    configurator.AddConsumer<PaymentFailedConsumer>();
    configurator.AddConsumer<OrderShippedConsumer>();
    configurator.AddConsumer<OrderDeliveredConsumer>();

    configurator.UsingRabbitMq((context, cfg) =>
    {
        var host = builder.Configuration["RabbitMq:Host"] ?? "localhost";
        var username = builder.Configuration["RabbitMq:Username"] ?? "guest";
        var password = builder.Configuration["RabbitMq:Password"] ?? "guest";

        cfg.Host(host, "/", h =>
        {
            h.Username(username);
            h.Password(password);
        });

        cfg.ConfigureEndpoints(context, new KebabCaseEndpointNameFormatter("order", false));
    });
});

if (authOptions.Enabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidIssuer = authOptions.Issuer,
                ValidAudience = authOptions.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.SigningKey)),
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (string.IsNullOrWhiteSpace(context.Token) &&
                        context.Request.Cookies.TryGetValue(authOptions.CookieName, out var cookieToken) &&
                        !string.IsNullOrWhiteSpace(cookieToken))
                    {
                        context.Token = cookieToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("CustomerOrAdmin", policy => policy.RequireAssertion(context =>
            context.User.HasClaim(ClaimTypes.Role, "customer") || context.User.HasClaim(ClaimTypes.Role, "admin")));
        options.AddPolicy("AdminOnly", policy => policy.RequireClaim(ClaimTypes.Role, "admin"));
    });
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (authOptions.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "OrderService" }));

var createOrderEndpoint = app.MapPost("/api/orders", async (
    CreateOrderRequest request,
    HttpContext httpContext,
    OrderDbContext db,
    IPublishEndpoint publishEndpoint,
    IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId) ||
        string.IsNullOrWhiteSpace(request.ShippingAddress) ||
        string.IsNullOrWhiteSpace(request.PaymentMethod))
    {
        return Results.BadRequest("CustomerId, ShippingAddress, and PaymentMethod are required.");
    }

    if (request.Items.Count == 0)
    {
        return Results.BadRequest("Order requires at least one item.");
    }

    if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.ProductId) || item.Quantity < 1))
    {
        return Results.BadRequest("Each order item must include ProductId and quantity of at least 1.");
    }

    var normalizedItems = request.Items
        .GroupBy(item => item.ProductId.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(group => new CreateOrderItemRequest(group.Key, group.Sum(item => item.Quantity)))
        .ToList();

    foreach (var item in normalizedItems)
    {
        var lookup = await TryGetProductAsync(httpClientFactory, productServiceOptions.BaseUrl, item.ProductId, httpContext.RequestAborted);

        if (!lookup.Success)
        {
            if (lookup.StatusCode == HttpStatusCode.NotFound)
            {
                return Results.BadRequest($"Product '{item.ProductId}' does not exist.");
            }

            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (lookup.Product?.IsArchived == true)
        {
            return Results.BadRequest($"Product '{item.ProductId}' is archived and cannot be ordered.");
        }

        var availableStock = lookup.Product?.Stock ?? 0;
        if (availableStock < item.Quantity)
        {
            return Results.BadRequest($"Insufficient stock for product '{item.ProductId}'. Available: {availableStock}, requested: {item.Quantity}.");
        }
    }

    var order = new OrderEntity
    {
        Id = Guid.NewGuid(),
        CustomerId = request.CustomerId,
        ShippingAddress = request.ShippingAddress,
        PaymentMethod = request.PaymentMethod,
        Status = OrderStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Items = normalizedItems.Select(item => new OrderItemEntity
        {
            Id = Guid.NewGuid(),
            ProductId = item.ProductId,
            Quantity = item.Quantity
        }).ToList()
    };

    db.Orders.Add(order);
    await db.SaveChangesAsync();

    var correlationId = GetOrCreateCorrelationId(httpContext);
    var metadata = CreateMetadata(nameof(OrderCreated), correlationId);

    try
    {
        using var publishTimeout = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        publishTimeout.CancelAfter(TimeSpan.FromSeconds(8));

        await publishEndpoint.Publish(new OrderCreated(
            metadata,
            order.Id,
            order.CustomerId,
            order.ShippingAddress,
            order.Items.Select(i => new OrderItem(i.ProductId, i.Quantity)).ToList(),
            order.PaymentMethod), publishTimeout.Token);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex,
            "Failed to publish OrderCreated event for order {OrderId}.", order.Id);

        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Created($"/api/orders/{order.Id}", new { orderId = order.Id, order.Status, correlationId });
});
if (authOptions.Enabled)
{
    createOrderEndpoint.RequireAuthorization("CustomerOrAdmin");
}

var getOrderEndpoint = app.MapGet("/api/orders/{orderId:guid}", async (Guid orderId, OrderDbContext db) =>
{
    var order = await db.Orders.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == orderId);
    return order is null ? Results.NotFound() : Results.Ok(order.ToResponse());
});
if (authOptions.Enabled)
{
    getOrderEndpoint.RequireAuthorization("CustomerOrAdmin");
}

var getOrdersForCustomerEndpoint = app.MapGet("/api/orders/customer/{customerId}", async (string customerId, OrderDbContext db) =>
{
    var orders = await db.Orders
        .Include(x => x.Items)
        .Where(x => x.CustomerId == customerId)
        .OrderByDescending(x => x.CreatedAt)
        .Select(x => x.ToResponse())
        .ToListAsync();

    return Results.Ok(orders);
});
if (authOptions.Enabled)
{
    getOrdersForCustomerEndpoint.RequireAuthorization("CustomerOrAdmin");
}

var cancelOrderEndpoint = app.MapPost("/api/orders/{orderId:guid}/cancel", async (
    Guid orderId,
    CancelOrderRequest request,
    HttpContext httpContext,
    OrderDbContext db,
    IPublishEndpoint publishEndpoint) =>
{
    var order = await db.Orders.FirstOrDefaultAsync(x => x.Id == orderId);
    if (order is null)
    {
        return Results.NotFound();
    }

    if (order.Status is OrderStatus.Shipped or OrderStatus.Delivered)
    {
        return Results.BadRequest("Order cannot be cancelled once shipped.");
    }

    order.Status = OrderStatus.Cancelled;
    order.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    var correlationId = GetOrCreateCorrelationId(httpContext);
    var metadata = CreateMetadata(nameof(OrderCancelled), correlationId);
    await publishEndpoint.Publish(new OrderCancelled(metadata, order.Id, request.Reason ?? "Cancelled by customer"));

    return Results.Ok(new { orderId = order.Id, order.Status });
});
if (authOptions.Enabled)
{
    cancelOrderEndpoint.RequireAuthorization("CustomerOrAdmin");
}

var updateOrderStatusEndpoint = app.MapPut("/api/orders/{orderId:guid}/status", async (Guid orderId, UpdateOrderStatusRequest request, OrderDbContext db) =>
{
    var order = await db.Orders.FirstOrDefaultAsync(x => x.Id == orderId);
    if (order is null)
    {
        return Results.NotFound();
    }

    order.Status = request.Status;
    order.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(new { orderId = order.Id, order.Status });
});
if (authOptions.Enabled)
{
    updateOrderStatusEndpoint.RequireAuthorization("AdminOnly");
}

app.Run();

static EventMetadata CreateMetadata(string eventName, Guid correlationId) =>
    new(Guid.NewGuid(), correlationId, eventName, DateTimeOffset.UtcNow);

static Guid GetOrCreateCorrelationId(HttpContext httpContext)
{
    if (httpContext.Request.Headers.TryGetValue("x-correlation-id", out var value) &&
        Guid.TryParse(value.ToString(), out var existing))
    {
        return existing;
    }

    return Guid.NewGuid();
}

static async Task<ProductLookupResult> TryGetProductAsync(
    IHttpClientFactory httpClientFactory,
    string baseUrl,
    string productId,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        return new ProductLookupResult { Success = false };
    }

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var client = httpClientFactory.CreateClient();
        var safeBaseUrl = baseUrl.TrimEnd('/');
        var response = await client.GetAsync($"{safeBaseUrl}/api/products/{Uri.EscapeDataString(productId)}", cts.Token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProductLookupResult { Success = false, StatusCode = HttpStatusCode.NotFound };
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ProductLookupResult { Success = false, StatusCode = response.StatusCode };
        }

        var product = await response.Content.ReadFromJsonAsync<ProductLookupResponse>(cancellationToken: cts.Token);
        if (product is null || string.IsNullOrWhiteSpace(product.Id))
        {
            return new ProductLookupResult { Success = false };
        }

        return new ProductLookupResult
        {
            Success = true,
            StatusCode = response.StatusCode,
            Product = product
        };
    }
    catch
    {
        return new ProductLookupResult { Success = false };
    }
}

sealed class InventoryReservedConsumer(OrderDbContext db) : IConsumer<InventoryReserved>
{
    public async Task Consume(ConsumeContext<InventoryReserved> context)
    {
        var order = await db.Orders.FindAsync(context.Message.OrderId);
        if (order is null) return;

        order.Status = OrderStatus.InventoryReserved;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}

sealed class InventoryFailedConsumer(OrderDbContext db) : IConsumer<InventoryFailed>
{
    public async Task Consume(ConsumeContext<InventoryFailed> context)
    {
        var order = await db.Orders.FindAsync(context.Message.OrderId);
        if (order is null) return;

        order.Status = OrderStatus.PaymentFailed;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}

sealed class PaymentCompletedConsumer(OrderDbContext db) : IConsumer<PaymentCompleted>
{
    public async Task Consume(ConsumeContext<PaymentCompleted> context)
    {
        var order = await db.Orders.FindAsync(context.Message.OrderId);
        if (order is null) return;

        order.Status = OrderStatus.PaymentCompleted;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}

sealed class PaymentFailedConsumer(OrderDbContext db) : IConsumer<PaymentFailed>
{
    public async Task Consume(ConsumeContext<PaymentFailed> context)
    {
        var order = await db.Orders.FindAsync(context.Message.OrderId);
        if (order is null) return;

        order.Status = OrderStatus.PaymentFailed;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}

sealed class OrderShippedConsumer(OrderDbContext db) : IConsumer<OrderShipped>
{
    public async Task Consume(ConsumeContext<OrderShipped> context)
    {
        var order = await db.Orders.FindAsync(context.Message.OrderId);
        if (order is null) return;

        order.Status = OrderStatus.Shipped;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}

sealed class OrderDeliveredConsumer(OrderDbContext db) : IConsumer<OrderDelivered>
{
    public async Task Consume(ConsumeContext<OrderDelivered> context)
    {
        var order = await db.Orders.FindAsync(context.Message.OrderId);
        if (order is null) return;

        order.Status = OrderStatus.Delivered;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}

sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderEntity>()
            .HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderEntity>()
            .Property(x => x.Status)
            .HasConversion<string>();
    }
}

sealed class OrderEntity
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrderItemEntity> Items { get; set; } = [];

    public object ToResponse() => new
    {
        Id,
        CustomerId,
        ShippingAddress,
        PaymentMethod,
        Status,
        CreatedAt,
        UpdatedAt,
        Items = Items.Select(i => new { i.ProductId, i.Quantity })
    };
}

sealed class OrderItemEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

sealed record CreateOrderRequest(
    string CustomerId,
    string ShippingAddress,
    IReadOnlyCollection<CreateOrderItemRequest> Items,
    string PaymentMethod);

sealed record CreateOrderItemRequest(string ProductId, int Quantity);
sealed record CancelOrderRequest(string? Reason);
sealed record UpdateOrderStatusRequest(OrderStatus Status);

sealed class ProductServiceOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8082";
}

sealed class ProductLookupResponse
{
    public string Id { get; set; } = string.Empty;
    public int Stock { get; set; }
    public bool IsArchived { get; set; }
}

sealed class ProductLookupResult
{
    public bool Success { get; init; }
    public HttpStatusCode? StatusCode { get; init; }
    public ProductLookupResponse? Product { get; init; }
}

sealed class AuthOptions
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "OrderSystem.Auth";
    public string Audience { get; set; } = "OrderSystem.Api";
    public string CookieName { get; set; } = "order_system_auth";
    public string SigningKey { get; set; } = string.Empty;
}
