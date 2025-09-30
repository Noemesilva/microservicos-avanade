using Microsoft.EntityFrameworkCore;
using SalesService.Data;
using SalesService.Models;
using System.Net.Http.Json;

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

// DTO para criação de pedido
public record CreateOrderItemDto(Guid ProductId, int Quantity);
public record CreateOrderDto(List<CreateOrderItemDto> Items);

app.MapPost("/api/orders", async (CreateOrderDto dto, SalesDbContext db, IHttpClientFactory httpClientFactory) =>
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

    return Results.Created($"/api/orders/{order.Id}", order);
})
.WithName("CreateOrder")
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
