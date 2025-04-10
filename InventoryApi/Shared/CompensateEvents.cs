namespace PaymentApi.Shared
{
    // CompensateEvents.cs
    // InventoryEvents.cs
    public record UpdateInventory(Guid CorrelationId, Guid OrderId);
    public record InventoryUpdated(Guid CorrelationId, bool Success);
    public record CompensateInventory(Guid CorrelationId, Guid OrderId);
    public record InventoryCompensated(Guid CorrelationId, bool Success);
}
