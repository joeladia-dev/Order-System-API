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
builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("InventoryDb") ?? "Data Source=inventory.db"));

builder.Services.AddMassTransit(configurator =>
{
    configurator.AddConsumer<OrderCreatedConsumer>();
    configurator.AddConsumer<OrderCancelledConsumer>();

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

        cfg.ConfigureEndpoints(context, new KebabCaseEndpointNameFormatter("inventory", false));
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
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "InventoryService" }));

var getInventoryEndpoint = app.MapGet("/api/inventory/{productId}", async (string productId, InventoryDbContext db) =>
{
    var item = await db.Items.FirstOrDefaultAsync(x => x.ProductId == productId);
    if (item is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        item.ProductId,
        item.Stock,
        item.Reserved,
        Available = item.Stock - item.Reserved
    });
});
if (authOptions.Enabled)
{
    getInventoryEndpoint.RequireAuthorization("InternalOnly");
}

var reserveInventoryEndpoint = app.MapPost("/api/inventory/reserve", async (ReserveInventoryRequest request, InventoryDbContext db) =>
{
    var failed = new List<string>();
    foreach (var requested in request.Items)
    {
        var item = await db.Items.FirstOrDefaultAsync(x => x.ProductId == requested.ProductId);
        if (item is null || (item.Stock - item.Reserved) < requested.Quantity)
        {
            failed.Add(requested.ProductId);
        }
    }

    if (failed.Count > 0)
    {
        return Results.BadRequest(new { message = "Insufficient stock", failedProducts = failed });
    }

    foreach (var requested in request.Items)
    {
        var item = await db.Items.FirstAsync(x => x.ProductId == requested.ProductId);
        item.Reserved += requested.Quantity;
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { status = "reserved" });
});
if (authOptions.Enabled)
{
    reserveInventoryEndpoint.RequireAuthorization("InternalOnly");
}

var releaseInventoryEndpoint = app.MapPost("/api/inventory/release", async (ReleaseInventoryRequest request, InventoryDbContext db) =>
{
    foreach (var requested in request.Items)
    {
        var item = await db.Items.FirstOrDefaultAsync(x => x.ProductId == requested.ProductId);
        if (item is null)
        {
            continue;
        }

        item.Reserved = Math.Max(0, item.Reserved - requested.Quantity);
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { status = "released" });
});
if (authOptions.Enabled)
{
    releaseInventoryEndpoint.RequireAuthorization("InternalOnly");
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

sealed class OrderCreatedConsumer(InventoryDbContext db, IPublishEndpoint publishEndpoint) : IConsumer<OrderCreated>
{
    public async Task Consume(ConsumeContext<OrderCreated> context)
    {
        var unavailable = new List<string>();
        var stockByProduct = new Dictionary<string, InventoryItemEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var orderItem in context.Message.Items)
        {
            var stockItem = await db.Items.FirstOrDefaultAsync(x => x.ProductId == orderItem.ProductId, context.CancellationToken);
            if (stockItem is null)
            {
                stockItem = new InventoryItemEntity
                {
                    ProductId = orderItem.ProductId,
                    Stock = 100,
                    Reserved = 0
                };
                db.Items.Add(stockItem);
            }

            stockByProduct[orderItem.ProductId] = stockItem;

            if ((stockItem.Stock - stockItem.Reserved) < orderItem.Quantity)
            {
                unavailable.Add(orderItem.ProductId);
            }
        }

        if (unavailable.Count > 0)
        {
            await publishEndpoint.Publish(new InventoryFailed(
                new EventMetadata(Guid.NewGuid(), context.Message.Metadata.CorrelationId, nameof(InventoryFailed), DateTimeOffset.UtcNow),
                context.Message.OrderId,
                $"Out of stock: {string.Join(",", unavailable)}"), context.CancellationToken);
            return;
        }

        foreach (var orderItem in context.Message.Items)
        {
            if (!stockByProduct.TryGetValue(orderItem.ProductId, out var stockItem)) continue;
            stockItem.Reserved += orderItem.Quantity;
        }

        db.Allocations.Add(new InventoryAllocationEntity
        {
            Id = Guid.NewGuid(),
            OrderId = context.Message.OrderId,
            Items = context.Message.Items.Select(x => new InventoryAllocationItemEntity
            {
                Id = Guid.NewGuid(),
                ProductId = x.ProductId,
                Quantity = x.Quantity
            }).ToList()
        });

        await db.SaveChangesAsync(context.CancellationToken);

        await publishEndpoint.Publish(new InventoryReserved(
            new EventMetadata(Guid.NewGuid(), context.Message.Metadata.CorrelationId, nameof(InventoryReserved), DateTimeOffset.UtcNow),
            context.Message.OrderId,
            context.Message.Items), context.CancellationToken);
    }
}

sealed class OrderCancelledConsumer(InventoryDbContext db) : IConsumer<OrderCancelled>
{
    public async Task Consume(ConsumeContext<OrderCancelled> context)
    {
        var allocation = await db.Allocations
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.OrderId == context.Message.OrderId, context.CancellationToken);

        if (allocation is null)
        {
            return;
        }

        foreach (var allocatedItem in allocation.Items)
        {
            var stock = await db.Items.FirstOrDefaultAsync(x => x.ProductId == allocatedItem.ProductId, context.CancellationToken);
            if (stock is null)
            {
                continue;
            }

            stock.Reserved = Math.Max(0, stock.Reserved - allocatedItem.Quantity);
        }

        db.Allocations.Remove(allocation);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}

sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<InventoryItemEntity> Items => Set<InventoryItemEntity>();
    public DbSet<InventoryAllocationEntity> Allocations => Set<InventoryAllocationEntity>();
    public DbSet<InventoryAllocationItemEntity> AllocationItems => Set<InventoryAllocationItemEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryItemEntity>().HasKey(x => x.ProductId);

        modelBuilder.Entity<InventoryAllocationEntity>()
            .HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(x => x.AllocationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

sealed class InventoryItemEntity
{
    public string ProductId { get; set; } = string.Empty;
    public int Stock { get; set; }
    public int Reserved { get; set; }
}

sealed class InventoryAllocationEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public List<InventoryAllocationItemEntity> Items { get; set; } = [];
}

sealed class InventoryAllocationItemEntity
{
    public Guid Id { get; set; }
    public Guid AllocationId { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

sealed record ReserveInventoryRequest(IReadOnlyCollection<OrderItem> Items);
sealed record ReleaseInventoryRequest(IReadOnlyCollection<OrderItem> Items);

sealed class AuthOptions
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "OrderSystem.Auth";
    public string Audience { get; set; } = "OrderSystem.Api";
    public string SigningKey { get; set; } = string.Empty;
}
