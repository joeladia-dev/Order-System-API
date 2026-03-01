using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Shared.Contracts;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

if (authOptions.Enabled && string.IsNullOrWhiteSpace(authOptions.SigningKey))
{
    throw new InvalidOperationException("Auth is enabled but Auth:SigningKey is missing. Set ORDER_SYSTEM_JWT_SIGNING_KEY.");
}

builder.Services.AddOpenApi();
builder.Services.AddDbContext<ShippingDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ShippingDb") ?? "Data Source=shipping.db"));

builder.Services.AddMassTransit(configurator =>
{
    configurator.AddConsumer<PaymentCompletedConsumer>();

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

        cfg.ConfigureEndpoints(context, new KebabCaseEndpointNameFormatter("shipping", false));
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
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("InternalOnly", policy => policy.RequireAssertion(context =>
            HasScope(context.User, "internal") || context.User.HasClaim(ClaimTypes.Role, "admin")));
    });
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ShippingService" }));

var createShipmentEndpoint = app.MapPost("/api/shipping/create", async (CreateShipmentRequest request, ShippingDbContext db, IPublishEndpoint publishEndpoint) =>
{
    var existing = await db.Shipments.FirstOrDefaultAsync(x => x.OrderId == request.OrderId);
    if (existing is not null)
    {
        return Results.Ok(existing);
    }

    var shipment = new ShipmentEntity
    {
        Id = Guid.NewGuid(),
        OrderId = request.OrderId,
        TrackingNumber = $"TRK-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
        Status = ShipmentState.Shipped,
        CreatedAt = DateTimeOffset.UtcNow,
        EstimatedDeliveryDate = DateTimeOffset.UtcNow.AddDays(3),
        DeliveredAt = null
    };

    db.Shipments.Add(shipment);
    await db.SaveChangesAsync();

    var correlationId = request.CorrelationId ?? Guid.NewGuid();
    await publishEndpoint.Publish(new OrderShipped(
        new EventMetadata(Guid.NewGuid(), correlationId, nameof(OrderShipped), DateTimeOffset.UtcNow),
        shipment.OrderId,
        shipment.TrackingNumber,
        shipment.EstimatedDeliveryDate));

    return Results.Ok(shipment);
});
if (authOptions.Enabled)
{
    createShipmentEndpoint.RequireAuthorization("InternalOnly");
}

var getShippingEndpoint = app.MapGet("/api/shipping/{orderId:guid}", async (Guid orderId, ShippingDbContext db) =>
{
    var shipment = await db.Shipments.FirstOrDefaultAsync(x => x.OrderId == orderId);
    return shipment is null ? Results.NotFound() : Results.Ok(shipment);
});
if (authOptions.Enabled)
{
    getShippingEndpoint.RequireAuthorization("InternalOnly");
}

bool HasScope(ClaimsPrincipal user, string scope)
{
    var scopeClaims = user.FindAll("scope").Select(x => x.Value);
    foreach (var claim in scopeClaims)
    {
        var scopes = claim.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scopes.Contains(scope, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

app.Run();

sealed class PaymentCompletedConsumer(ShippingDbContext db, IPublishEndpoint publishEndpoint) : IConsumer<PaymentCompleted>
{
    public async Task Consume(ConsumeContext<PaymentCompleted> context)
    {
        var shipment = await db.Shipments.FirstOrDefaultAsync(x => x.OrderId == context.Message.OrderId, context.CancellationToken);
        if (shipment is null)
        {
            shipment = new ShipmentEntity
            {
                Id = Guid.NewGuid(),
                OrderId = context.Message.OrderId,
                TrackingNumber = $"TRK-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
                Status = ShipmentState.Shipped,
                CreatedAt = DateTimeOffset.UtcNow,
                EstimatedDeliveryDate = DateTimeOffset.UtcNow.AddDays(3),
                DeliveredAt = null
            };
            db.Shipments.Add(shipment);
        }

        shipment.Status = ShipmentState.Shipped;
        await db.SaveChangesAsync(context.CancellationToken);

        var shippedMetadata = new EventMetadata(Guid.NewGuid(), context.Message.Metadata.CorrelationId, nameof(OrderShipped), DateTimeOffset.UtcNow);
        await publishEndpoint.Publish(new OrderShipped(
            shippedMetadata,
            shipment.OrderId,
            shipment.TrackingNumber,
            shipment.EstimatedDeliveryDate), context.CancellationToken);

        shipment.Status = ShipmentState.Delivered;
        shipment.DeliveredAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);

        var deliveredMetadata = new EventMetadata(Guid.NewGuid(), context.Message.Metadata.CorrelationId, nameof(OrderDelivered), DateTimeOffset.UtcNow);
        await publishEndpoint.Publish(new OrderDelivered(deliveredMetadata, shipment.OrderId, shipment.DeliveredAt.Value), context.CancellationToken);
    }
}

sealed class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    public DbSet<ShipmentEntity> Shipments => Set<ShipmentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShipmentEntity>()
            .HasIndex(x => x.OrderId)
            .IsUnique();

        modelBuilder.Entity<ShipmentEntity>()
            .Property(x => x.Status)
            .HasConversion<string>();
    }
}

sealed class ShipmentEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string TrackingNumber { get; set; } = string.Empty;
    public ShipmentState Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset EstimatedDeliveryDate { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

enum ShipmentState
{
    Shipped,
    Delivered
}

sealed record CreateShipmentRequest(Guid OrderId, Guid? CorrelationId);

sealed class AuthOptions
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "OrderSystem.Auth";
    public string Audience { get; set; } = "OrderSystem.Api";
    public string SigningKey { get; set; } = string.Empty;
}
