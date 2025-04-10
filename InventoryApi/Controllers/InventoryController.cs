using InventoryApi.Data;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentApi.Shared;

namespace InventoryApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class InventoryController : ControllerBase
    {
        private readonly InventoryDbContext _context;
        private readonly IBus _bus;
        private readonly ILogger<InventoryController> _logger;

        public InventoryController(
            InventoryDbContext context,
            IBus bus,
            ILogger<InventoryController> logger)
        {
            _context = context;
            _bus = bus;
            _logger = logger;
        }

        [HttpPost("update")]
        public async Task<IActionResult> UpdateInventory([FromBody] UpdateInventoryRequest request)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var item = await _context.InventoryItems
                    .FirstOrDefaultAsync(i => i.ProductId == request.ProductId);

                if (item == null)
                {
                    _logger.LogError($"Product {request.ProductId} not found");
                    return NotFound();
                }

                // Kiểm tra số lượng tồn kho
                if (item.Quantity - item.ReservedQuantity < request.Quantity)
                {
                    _logger.LogError("Insufficient inventory");
                    await _bus.Publish(new InventoryUpdated(request.CorrelationId, false));
                    return BadRequest("Insufficient inventory");
                }

                // Cập nhật số lượng đã đặt
                item.ReservedQuantity += request.Quantity;
                await _context.SaveChangesAsync();

                // Publish event thành công
                await _bus.Publish(new InventoryUpdated(request.CorrelationId, true));
                await transaction.CommitAsync();

                return Ok(item);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Inventory update failed");
                await _bus.Publish(new InventoryUpdated(request.CorrelationId, false));
                return StatusCode(500, "Internal server error");
            }
        }
    }

    public record UpdateInventoryRequest(
        Guid CorrelationId,
        string ProductId,
        int Quantity
    );
}
