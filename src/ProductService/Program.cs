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
    var products = await db.Products.OrderBy(x => x.Name).ToListAsync();
    return Results.Ok(products);
});

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

    product.Stock = request.Stock;
    await db.SaveChangesAsync();

    return Results.Ok(product);
});
if (authOptions.Enabled)
{
    updateStockEndpoint.RequireAuthorization("AdminOnly");
}

app.Run();

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
}

sealed record CreateProductRequest(string Id, string Name, decimal Price, int Stock);
sealed record UpdateProductStockRequest(int Stock);

sealed class AuthOptions
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "OrderSystem.Auth";
    public string Audience { get; set; } = "OrderSystem.Api";
    public string SigningKey { get; set; } = string.Empty;
}
