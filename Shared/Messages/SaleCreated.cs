namespace Shared.Messages;

public class SaleCreated
{
    public Guid OrderId { get; set; }
    public List<SaleItem> Items { get; set; } = new();
}

public class SaleItem
{
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
}


