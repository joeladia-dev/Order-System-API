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
builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("PaymentDb") ?? "Data Source=payments.db"));

builder.Services.AddMassTransit(configurator =>
{
    configurator.AddConsumer<InventoryReservedConsumer>();
    configurator.AddConsumer<InventoryFailedConsumer>();

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

        cfg.ConfigureEndpoints(context, new KebabCaseEndpointNameFormatter("payment", false));
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
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    db.Database.EnsureCreated();
    EnsurePaymentSchema(db);
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "PaymentService" }));

var processPaymentEndpoint = app.MapPost("/api/payments/process", async (ProcessPaymentRequest request, PaymentDbContext db, IPublishEndpoint publishEndpoint) =>
{
    var payment = await db.Payments.FirstOrDefaultAsync(x => x.OrderId == request.OrderId);
    if (payment is null)
    {
        payment = new PaymentEntity
        {
            Id = Guid.NewGuid(),
            OrderId = request.OrderId,
            Amount = request.Amount,
            Status = PaymentState.Processing,
            TransactionId = Guid.NewGuid().ToString("N"),
            FailureReason = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Payments.Add(payment);
    }

    payment.Status = request.Success ? PaymentState.Completed : PaymentState.Failed;
    payment.FailureReason = request.Success ? null : "Payment was declined";
    payment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    var metadata = new EventMetadata(Guid.NewGuid(), request.CorrelationId ?? Guid.NewGuid(),
        request.Success ? nameof(PaymentCompleted) : nameof(PaymentFailed), DateTimeOffset.UtcNow);

    if (request.Success)
    {
        await publishEndpoint.Publish(new PaymentCompleted(metadata, request.OrderId, request.Amount, payment.TransactionId));
    }
    else
    {
        await publishEndpoint.Publish(new PaymentFailed(metadata, request.OrderId, "Payment was declined"));
    }

    return Results.Ok(new { payment.OrderId, Status = payment.Status.ToString(), payment.TransactionId });
});
if (authOptions.Enabled)
{
    processPaymentEndpoint.RequireAuthorization("InternalOnly");
}

var getPaymentEndpoint = app.MapGet("/api/payments/{orderId:guid}", async (Guid orderId, PaymentDbContext db) =>
{
    var payment = await db.Payments.FirstOrDefaultAsync(x => x.OrderId == orderId);
    return payment is null ? Results.NotFound() : Results.Ok(payment);
});
if (authOptions.Enabled)
{
    getPaymentEndpoint.RequireAuthorization("InternalOnly");
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

static void EnsurePaymentSchema(PaymentDbContext db)
{
    var columns = db.Database
        .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Payments');")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    if (!columns.Contains("FailureReason"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Payments ADD COLUMN FailureReason TEXT NULL;");
    }
}

app.Run();

sealed class InventoryReservedConsumer(PaymentDbContext db, IPublishEndpoint publishEndpoint) : IConsumer<InventoryReserved>
{
    public async Task Consume(ConsumeContext<InventoryReserved> context)
    {
        var amount = context.Message.ReservedItems.Sum(x => x.Quantity * 100m);

        var payment = await db.Payments.FirstOrDefaultAsync(x => x.OrderId == context.Message.OrderId, context.CancellationToken);
        if (payment is null)
        {
            payment = new PaymentEntity
            {
                Id = Guid.NewGuid(),
                OrderId = context.Message.OrderId,
                Amount = amount,
                Status = PaymentState.Processing,
                TransactionId = Guid.NewGuid().ToString("N"),
                FailureReason = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Payments.Add(payment);
        }

        payment.Status = PaymentState.Completed;
        payment.FailureReason = null;
        payment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);

        await publishEndpoint.Publish(new PaymentCompleted(
            new EventMetadata(Guid.NewGuid(), context.Message.Metadata.CorrelationId, nameof(PaymentCompleted), DateTimeOffset.UtcNow),
            context.Message.OrderId,
            amount,
            payment.TransactionId), context.CancellationToken);
    }
}

sealed class InventoryFailedConsumer(PaymentDbContext db, IPublishEndpoint publishEndpoint) : IConsumer<InventoryFailed>
{
    public async Task Consume(ConsumeContext<InventoryFailed> context)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(x => x.OrderId == context.Message.OrderId, context.CancellationToken);
        if (payment is null)
        {
            payment = new PaymentEntity
            {
                Id = Guid.NewGuid(),
                OrderId = context.Message.OrderId,
                Amount = 0,
                Status = PaymentState.Failed,
                TransactionId = Guid.NewGuid().ToString("N"),
                FailureReason = context.Message.Reason,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Payments.Add(payment);
        }
        else
        {
            payment.Status = PaymentState.Failed;
            payment.FailureReason = context.Message.Reason;
            payment.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(context.CancellationToken);

        await publishEndpoint.Publish(new PaymentFailed(
            new EventMetadata(Guid.NewGuid(), context.Message.Metadata.CorrelationId, nameof(PaymentFailed), DateTimeOffset.UtcNow),
            context.Message.OrderId,
            context.Message.Reason), context.CancellationToken);
    }
}

sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentEntity> Payments => Set<PaymentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentEntity>()
            .HasIndex(x => x.OrderId)
            .IsUnique();

        modelBuilder.Entity<PaymentEntity>()
            .Property(x => x.Status)
            .HasConversion<string>();
    }
}

sealed class PaymentEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public PaymentState Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

enum PaymentState
{
    Processing,
    Completed,
    Failed
}

sealed record ProcessPaymentRequest(Guid OrderId, decimal Amount, bool Success, Guid? CorrelationId);

sealed class AuthOptions
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "OrderSystem.Auth";
    public string Audience { get; set; } = "OrderSystem.Api";
    public string SigningKey { get; set; } = string.Empty;
}
