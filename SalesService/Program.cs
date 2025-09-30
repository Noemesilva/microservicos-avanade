using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using SalesService.Data;
using SalesService.Models;
using System.Net.Http.Json;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using RabbitMQ.Client;
using System.Text.Json;
using Shared.Messages;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<SalesDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")));

builder.Services.AddHttpClient("inventory", client =>
{
    var baseUrl = builder.Configuration.GetValue<string>("InventoryService:BaseUrl") ?? "http://localhost:5001";
    client.BaseAddress = new Uri(baseUrl);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// JWT
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

app.UseAuthentication();
app.UseAuthorization();

// DTO para criação de pedido
public record CreateOrderItemDto(Guid ProductId, int Quantity);
public record CreateOrderDto(List<CreateOrderItemDto> Items);

app.MapPost("/api/orders", async (CreateOrderDto dto, SalesDbContext db, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    if (dto.Items.Count == 0) return Results.BadRequest("Pedido sem itens");

    var client = httpClientFactory.CreateClient("inventory");
    // validação simples de estoque: busca produto e checa quantidade
    foreach (var item in dto.Items)
    {
        var product = await client.GetFromJsonAsync<ProductProxy>($"/api/products/{item.ProductId}");
        if (product is null) return Results.BadRequest($"Produto {item.ProductId} não encontrado");
        if (product.Quantity < item.Quantity) return Results.BadRequest($"Estoque insuficiente para {product.Name}");
    }

    var order = new Order();
    decimal total = 0m;

    foreach (var item in dto.Items)
    {
        var product = await client.GetFromJsonAsync<ProductProxy>($"/api/products/{item.ProductId}");
        if (product is null) continue;
        var orderItem = new OrderItem
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            ProductId = product.Id,
            ProductName = product.Name,
            UnitPrice = product.Price,
            Quantity = item.Quantity
        };
        total += orderItem.LineTotal;
        order.Items.Add(orderItem);
    }

    order.Id = Guid.NewGuid();
    order.TotalAmount = total;
    order.Status = OrderStatus.Confirmed;

    db.Orders.Add(order);
    await db.SaveChangesAsync();

    // Publica evento no RabbitMQ
    var factory = new ConnectionFactory
    {
        HostName = config.GetValue<string>("RabbitMQ:Host") ?? "localhost",
        UserName = config.GetValue<string>("RabbitMQ:Username") ?? "guest",
        Password = config.GetValue<string>("RabbitMQ:Password") ?? "guest"
    };
    using var connection = factory.CreateConnection();
    using var channel = connection.CreateModel();
    channel.ExchangeDeclare(exchange: "sales", type: ExchangeType.Fanout, durable: true);
    var message = new SaleCreated
    {
        OrderId = order.Id,
        Items = dto.Items.Select(i => new SaleItem { ProductId = i.ProductId, Quantity = i.Quantity }).ToList()
    };
    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
    channel.BasicPublish(exchange: "sales", routingKey: string.Empty, basicProperties: null, body: body);

    return Results.Created($"/api/orders/{order.Id}", order);
})
.WithName("CreateOrder")
.RequireAuthorization()
.WithOpenApi();

app.MapGet("/api/orders/{id}", async (Guid id, SalesDbContext db) =>
    await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id) is Order o
        ? Results.Ok(o)
        : Results.NotFound())
    .WithName("GetOrderById")
    .WithOpenApi();

app.MapGet("/api/orders", async (SalesDbContext db) =>
    await db.Orders.AsNoTracking().ToListAsync())
    .WithName("GetOrders")
    .WithOpenApi();

app.Run();

// Proxy de produto para validação de estoque
public class ProductProxy
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}
