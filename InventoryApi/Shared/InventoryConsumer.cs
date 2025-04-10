using InventoryApi.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace PaymentApi.Shared
{
    public class InventoryConsumer :
    IConsumer<UpdateInventory>,
    IConsumer<CompensateInventory>
    {
        private readonly InventoryDbContext _context;
        private readonly ILogger<InventoryConsumer> _logger;
        private readonly IBus _bus;

        public InventoryConsumer(
            InventoryDbContext context,
            ILogger<InventoryConsumer> logger,
            IBus bus)
        {
            _context = context;
            _logger = logger;
            _bus = bus;
        }

        // Xử lý cập nhật tồn kho
        public async Task Consume(ConsumeContext<UpdateInventory> context)
        {
            _logger.LogInformation($"Updating inventory for Order {context.Message.OrderId}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Logic cập nhật tồn kho (được xử lý trong API Controller)
                // Publish event từ API Controller thay vì ở đây
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Inventory update failed");
                await context.Publish(new InventoryUpdated(context.Message.CorrelationId, false));
            }
        }

        // Xử lý rollback tồn kho
        public async Task Consume(ConsumeContext<CompensateInventory> context)
        {
            _logger.LogInformation($"Compensating inventory for Order {context.Message.OrderId}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var item = await _context.InventoryItems
                    .FirstOrDefaultAsync(i => i.ProductId == "prod_123"); // Giả định productId

                if (item != null)
                {
                    item.ReservedQuantity -= 2; // Giả định số lượng hủy
                    await _context.SaveChangesAsync();
                }

                // Thông báo compensate thành công
                await _bus.Publish(new InventoryCompensated(context.Message.CorrelationId, true));
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Inventory compensation failed");
                await _bus.Publish(new InventoryCompensated(context.Message.CorrelationId, false));
            }
        }
    }
}
