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
