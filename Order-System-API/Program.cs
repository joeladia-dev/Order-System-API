var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/", () => Results.Ok(new
{
    service = "Order-System-API",
    status = "running"
}));

app.MapGet("/health", () => Results.Ok(new
{
    service = "Order-System-API",
    status = "healthy"
}));

app.Run();
