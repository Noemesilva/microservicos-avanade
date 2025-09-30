using InventoryService.Data;
using InventoryService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using System.Text.Json;
using Shared.Messages;

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

// RabbitMQ subscriber para decrementar estoque após venda
var rabbitFactory = new ConnectionFactory
{
    HostName = builder.Configuration.GetValue<string>("RabbitMQ:Host") ?? "localhost",
    UserName = builder.Configuration.GetValue<string>("RabbitMQ:Username") ?? "guest",
    Password = builder.Configuration.GetValue<string>("RabbitMQ:Password") ?? "guest"
};
var rabbitConnection = rabbitFactory.CreateConnection();
var rabbitChannel = rabbitConnection.CreateModel();
rabbitChannel.ExchangeDeclare(exchange: "sales", type: ExchangeType.Fanout, durable: true);
var queue = rabbitChannel.QueueDeclare(queue: "inventory-sales", durable: true, exclusive: false, autoDelete: false);
rabbitChannel.QueueBind(queue: queue.QueueName, exchange: "sales", routingKey: string.Empty);
var consumer = new EventingBasicConsumer(rabbitChannel);
consumer.Received += async (_, ea) =>
{
    try
    {
        var json = Encoding.UTF8.GetString(ea.Body.ToArray());
        var message = JsonSerializer.Deserialize<SaleCreated>(json);
        if (message is null) return;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        foreach (var item in message.Items)
        {
            var product = await db.Products.FindAsync(item.ProductId);
            if (product is null) continue;
            product.Quantity = Math.Max(0, product.Quantity - item.Quantity);
        }
        await db.SaveChangesAsync();
    }
    catch
    {
        // log ignorado nesta versão inicial
    }
};
rabbitChannel.BasicConsume(queue: queue.QueueName, autoAck: true, consumer: consumer);

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
