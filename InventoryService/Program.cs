using InventoryService.Data;
using InventoryService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")));

// Auth JWT (validação somente)
var jwtKey = builder.Configuration["Jwt:Key"] ?? "dev";
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = key
        };
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/products", async (InventoryDbContext db) =>
    await db.Products.AsNoTracking().ToListAsync())
    .WithName("GetProducts")
    .WithOpenApi();

app.MapGet("/api/products/{id}", async (Guid id, InventoryDbContext db) =>
    await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id) is Product p
        ? Results.Ok(p)
        : Results.NotFound())
    .WithName("GetProductById")
    .WithOpenApi();

app.MapPost("/api/products", async (Product input, InventoryDbContext db) =>
{
    input.Id = Guid.NewGuid();
    db.Products.Add(input);
    await db.SaveChangesAsync();
    return Results.Created($"/api/products/{input.Id}", input);
})
.WithName("CreateProduct")
.RequireAuthorization()
.WithOpenApi();

app.MapPut("/api/products/{id}", async (Guid id, Product update, InventoryDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();
    product.Name = update.Name;
    product.Description = update.Description;
    product.Price = update.Price;
    product.Quantity = update.Quantity;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("UpdateProduct")
.RequireAuthorization()
.WithOpenApi();

app.MapPatch("/api/products/{id}/stock/{quantity}", async (Guid id, int quantity, InventoryDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();
    product.Quantity = quantity;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("SetProductStock")
.RequireAuthorization()
.WithOpenApi();

app.Run();
