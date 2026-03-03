using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

if (authOptions.Enabled && string.IsNullOrWhiteSpace(authOptions.SigningKey))
{
    throw new InvalidOperationException("Auth is enabled but Auth:SigningKey is missing. Set ORDER_SYSTEM_JWT_SIGNING_KEY.");
}

builder.Services.AddOpenApi();
builder.Services.AddDbContext<ProductDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ProductDb") ?? "Data Source=products.db"));

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
        options.AddPolicy("AdminOnly", policy => policy.RequireClaim(ClaimTypes.Role, "admin"));
    });
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
    db.Database.EnsureCreated();
    EnsureProductSchema(db);
    SeedProductsIfEmpty(db);
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ProductService" }));

app.MapGet("/api/products", async (ProductDbContext db) =>
{
    var products = await db.Products
        .Where(x => !x.IsArchived)
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(products);
});

var listArchivedProductsEndpoint = app.MapGet("/api/products/archived", async (ProductDbContext db) =>
{
    var products = await db.Products
        .Where(x => x.IsArchived)
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(products);
});
if (authOptions.Enabled)
{
    listArchivedProductsEndpoint.RequireAuthorization("AdminOnly");
}

app.MapGet("/api/products/{id}", async (string id, ProductDbContext db) =>
{
    var product = await db.Products.FirstOrDefaultAsync(x => x.Id == id);
    return product is null ? Results.NotFound() : Results.Ok(product);
});

var createProductEndpoint = app.MapPost("/api/products", async (CreateProductRequest request, ProductDbContext db) =>
{
    var existing = await db.Products.AnyAsync(x => x.Id == request.Id);
    if (existing)
    {
        return Results.Conflict($"Product {request.Id} already exists.");
    }

    var product = new ProductEntity
    {
        Id = request.Id,
        Name = request.Name,
        Price = request.Price,
        Stock = request.Stock
    };

    db.Products.Add(product);
    await db.SaveChangesAsync();

    return Results.Created($"/api/products/{product.Id}", product);
});
if (authOptions.Enabled)
{
    createProductEndpoint.RequireAuthorization("AdminOnly");
}

var updateStockEndpoint = app.MapPut("/api/products/{id}/stock", async (string id, UpdateProductStockRequest request, ProductDbContext db) =>
{
    var product = await db.Products.FirstOrDefaultAsync(x => x.Id == id);
    if (product is null)
    {
        return Results.NotFound();
    }

    if (product.IsArchived)
    {
        return Results.BadRequest("Archived products cannot be updated.");
    }

    product.Stock = request.Stock;
    await db.SaveChangesAsync();

    return Results.Ok(product);
});
if (authOptions.Enabled)
{
    updateStockEndpoint.RequireAuthorization("AdminOnly");
}

var updateProductEndpoint = app.MapPut("/api/products/{id}", async (string id, UpdateProductRequest request, ProductDbContext db) =>
{
    var product = await db.Products.FirstOrDefaultAsync(x => x.Id == id);
    if (product is null)
    {
        return Results.NotFound();
    }

    if (product.IsArchived)
    {
        return Results.BadRequest("Archived products cannot be updated.");
    }

    product.Name = request.Name;
    product.Price = request.Price;
    product.Stock = request.Stock;

    await db.SaveChangesAsync();

    return Results.Ok(product);
});
if (authOptions.Enabled)
{
    updateProductEndpoint.RequireAuthorization("AdminOnly");
}

var archiveProductEndpoint = app.MapPut("/api/products/{id}/archive", async (string id, ProductDbContext db) =>
{
    var product = await db.Products.FirstOrDefaultAsync(x => x.Id == id);
    if (product is null)
    {
        return Results.NotFound();
    }

    if (!product.IsArchived)
    {
        product.IsArchived = true;
        product.ArchivedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    return Results.Ok(product);
});
if (authOptions.Enabled)
{
    archiveProductEndpoint.RequireAuthorization("AdminOnly");
}

var restoreProductEndpoint = app.MapPut("/api/products/{id}/restore", async (string id, ProductDbContext db) =>
{
    var product = await db.Products.FirstOrDefaultAsync(x => x.Id == id);
    if (product is null)
    {
        return Results.NotFound();
    }

    if (!product.IsArchived)
    {
        return Results.BadRequest("Product is not archived.");
    }

    product.IsArchived = false;
    product.ArchivedAtUtc = null;
    await db.SaveChangesAsync();

    return Results.Ok(product);
});
if (authOptions.Enabled)
{
    restoreProductEndpoint.RequireAuthorization("AdminOnly");
}

var deleteProductEndpoint = app.MapDelete("/api/products/{id}", async (string id, ProductDbContext db) =>
{
    var product = await db.Products.FirstOrDefaultAsync(x => x.Id == id);
    if (product is null)
    {
        return Results.NotFound();
    }

    if (!product.IsArchived)
    {
        return Results.BadRequest("Product must be archived before permanent deletion.");
    }

    db.Products.Remove(product);
    await db.SaveChangesAsync();

    return Results.NoContent();
});
if (authOptions.Enabled)
{
    deleteProductEndpoint.RequireAuthorization("AdminOnly");
}

app.Run();

static void EnsureProductSchema(ProductDbContext db)
{
    var columns = db.Database
        .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Products');")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    if (!columns.Contains("IsArchived"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Products ADD COLUMN IsArchived INTEGER NOT NULL DEFAULT 0;");
    }

    if (!columns.Contains("ArchivedAtUtc"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Products ADD COLUMN ArchivedAtUtc TEXT NULL;");
    }
}

static void SeedProductsIfEmpty(ProductDbContext db)
{
    var defaultProducts = new[]
    {
        new ProductEntity { Id = "sku-1000", Name = "Starter Product", Price = 25m, Stock = 12 },
        new ProductEntity { Id = "sku-1001", Name = "Wireless Mouse", Price = 29.99m, Stock = 50 },
        new ProductEntity { Id = "sku-1002", Name = "Mechanical Keyboard", Price = 89.99m, Stock = 35 },
        new ProductEntity { Id = "sku-1003", Name = "27-inch Monitor", Price = 219.99m, Stock = 18 },
        new ProductEntity { Id = "sku-1004", Name = "USB-C Hub", Price = 39.99m, Stock = 42 },
        new ProductEntity { Id = "sku-1005", Name = "Laptop Stand", Price = 34.50m, Stock = 40 },
        new ProductEntity { Id = "sku-1006", Name = "Noise-Canceling Headphones", Price = 149.00m, Stock = 22 },
        new ProductEntity { Id = "sku-1007", Name = "Webcam 1080p", Price = 59.00m, Stock = 30 },
        new ProductEntity { Id = "sku-1008", Name = "Portable SSD 1TB", Price = 119.99m, Stock = 26 },
        new ProductEntity { Id = "sku-1009", Name = "Wireless Charger", Price = 24.99m, Stock = 60 },
        new ProductEntity { Id = "sku-1010", Name = "Bluetooth Speaker", Price = 79.99m, Stock = 28 },
        new ProductEntity { Id = "sku-1011", Name = "Ergonomic Chair", Price = 249.00m, Stock = 10 },
        new ProductEntity { Id = "sku-1012", Name = "Desk Lamp", Price = 32.00m, Stock = 33 },
        new ProductEntity { Id = "sku-1013", Name = "Smart Plug", Price = 19.99m, Stock = 75 },
        new ProductEntity { Id = "sku-1014", Name = "Router AX3000", Price = 129.99m, Stock = 14 },
        new ProductEntity { Id = "sku-1015", Name = "Power Bank 20000mAh", Price = 44.99m, Stock = 38 },
        new ProductEntity { Id = "sku-1016", Name = "Phone Tripod", Price = 21.50m, Stock = 47 },
        new ProductEntity { Id = "sku-1017", Name = "Graphics Tablet", Price = 69.99m, Stock = 19 },
        new ProductEntity { Id = "sku-1018", Name = "Microphone USB", Price = 89.50m, Stock = 16 },
        new ProductEntity { Id = "sku-1019", Name = "Cable Organizer Kit", Price = 14.99m, Stock = 80 }
    };

    var existingIds = db.Products
        .Select(product => product.Id)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var missingDefaults = defaultProducts
        .Where(product => !existingIds.Contains(product.Id))
        .ToList();

    if (missingDefaults.Count == 0)
    {
        return;
    }

    db.Products.AddRange(missingDefaults);

    db.SaveChanges();
}

sealed class ProductDbContext(DbContextOptions<ProductDbContext> options) : DbContext(options)
{
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
}

sealed class ProductEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
}

sealed record CreateProductRequest(string Id, string Name, decimal Price, int Stock);
sealed record UpdateProductStockRequest(int Stock);
sealed record UpdateProductRequest(string Name, decimal Price, int Stock);

sealed class AuthOptions
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "OrderSystem.Auth";
    public string Audience { get; set; } = "OrderSystem.Api";
    public string CookieName { get; set; } = "order_system_auth";
    public string SigningKey { get; set; } = string.Empty;
}
