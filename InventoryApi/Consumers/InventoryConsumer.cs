using InventoryApi.Data;
using InventoryApi.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using SharedContracts.Events;

namespace InventoryApi.Consumers
{
    /// <summary>
    /// Handles inventory operations as part of the distributed saga.
    /// Responsible for reserving and releasing inventory.
    /// </summary>
    public class InventoryConsumer :
        IConsumer<UpdateInventory>,
        IConsumer<CompensateInventory>
    {
        private readonly InventoryDbContext _context;
        private readonly ILogger<InventoryConsumer> _logger;

        public InventoryConsumer(
            InventoryDbContext context,
            ILogger<InventoryConsumer> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Processes inventory reservation requests.
        /// Checks if sufficient inventory is available and reserves it.
        /// Uses the Outbox pattern for reliable messaging.
        /// </summary>
        public async Task Consume(ConsumeContext<UpdateInventory> context)
        {
            var correlationId = context.Message.CorrelationId;
            var orderId = context.Message.OrderId;
            var items = context.Message.Items;

            _logger.LogInformation($"Updating inventory for Order {orderId}, CorrelationId: {correlationId}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Check for duplicate reservations
                var existingReservation = await _context.InventoryReservations
                    .FirstOrDefaultAsync(r => r.OrderId == orderId);

                if (existingReservation != null)
                {
                    _logger.LogWarning($"Duplicate inventory update detected for Order {orderId}. Skipping...");

                    await _context.SaveEventToOutboxAsync(new InventoryUpdated(correlationId, orderId, true));
                    await transaction.CommitAsync();
                    return;
                }

                // Process each item in the order
                bool allItemsReserved = true;
                var reservations = new List<InventoryReservation>();

                foreach (var item in items)
                {
                    // Find inventory item
                    var inventoryItem = await _context.InventoryItems
                        .FirstOrDefaultAsync(i => i.ProductId == item.ProductId);

                    if (inventoryItem == null)
                    {
                        _logger.LogWarning($"Product {item.ProductId} not found in inventory");
                        allItemsReserved = false;
                        break;
                    }

                    // Check if sufficient inventory is available
                    if (inventoryItem.AvailableQuantity < item.Quantity)
                    {
                        _logger.LogWarning($"Insufficient inventory for product {item.ProductId}: Requested {item.Quantity}, Available {inventoryItem.AvailableQuantity}");
                        allItemsReserved = false;
                        break;
                    }

                    // Reserve inventory
                    inventoryItem.AvailableQuantity -= item.Quantity;
                    inventoryItem.ReservedQuantity += item.Quantity;

                    // Create reservation record
                    var reservation = new InventoryReservation
                    {
                        Id = Guid.NewGuid(),
                        OrderId = orderId,
                        ProductId = item.ProductId,
                        Quantity = item.Quantity,
                        CreatedAt = DateTime.UtcNow
                    };

                    reservations.Add(reservation);
                }

                if (allItemsReserved)
                {
                    // Save reservations
                    _context.InventoryReservations.AddRange(reservations);
                    await _context.SaveChangesAsync();

                    // Publish success event through outbox
                    await _context.SaveEventToOutboxAsync(new InventoryUpdated(correlationId, orderId, true));
                    _logger.LogInformation($"Inventory successfully updated for Order {orderId}");
                }
                else
                {
                    // Publish failure event through outbox
                    await _context.SaveEventToOutboxAsync(new InventoryUpdated(correlationId, orderId, false));
                    _logger.LogWarning($"Inventory update failed for Order {orderId} due to insufficient stock");
                }

                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, $"Exception during inventory update for Order {orderId}");

                // Ensure failure event is published even if the primary transaction fails
                try
                {
                    using var newTransaction = await _context.Database.BeginTransactionAsync();
                    await _context.SaveEventToOutboxAsync(new InventoryUpdated(correlationId, orderId, false));
                    await newTransaction.CommitAsync();
                }
                catch (Exception outboxEx)
                {
                    _logger.LogError(outboxEx, $"Failed to save inventory update failure event to outbox for Order {orderId}");
                }
            }
        }

        /// <summary>
        /// Handles compensation requests for inventory reservations.
        /// Releases previously reserved inventory as part of saga rollback.
        /// </summary>
        public async Task Consume(ConsumeContext<CompensateInventory> context)
        {
            var correlationId = context.Message.CorrelationId;
            var orderId = context.Message.OrderId;

            _logger.LogInformation($"Compensating inventory for Order {orderId}, CorrelationId: {correlationId}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Find all reservations for this order
                var reservations = await _context.InventoryReservations
                    .Where(r => r.OrderId == orderId)
                    .ToListAsync();

                if (reservations.Count == 0)
                {
                    _logger.LogWarning($"No inventory reservations found for Order {orderId} to compensate");
                    // Still publish successful compensation to allow saga to progress
                    await _context.SaveEventToOutboxAsync(new InventoryCompensated(correlationId, orderId, true));
                    await transaction.CommitAsync();
                    return;
                }

                // Release inventory for each item
                foreach (var reservation in reservations)
                {
                    var inventoryItem = await _context.InventoryItems
                        .FirstOrDefaultAsync(i => i.ProductId == reservation.ProductId);

                    if (inventoryItem != null)
                    {
                        // Return quantities back to available
                        inventoryItem.AvailableQuantity += reservation.Quantity;
                        inventoryItem.ReservedQuantity -= reservation.Quantity;
                    }
                }

                // Remove the reservations
                _context.InventoryReservations.RemoveRange(reservations);
                await _context.SaveChangesAsync();

                // Publish success event through outbox
                await _context.SaveEventToOutboxAsync(new InventoryCompensated(correlationId, orderId, true));

                _logger.LogInformation($"Inventory successfully compensated for Order {orderId}");
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, $"Inventory compensation failed for Order {orderId}");

                // Ensure compensation failure event is published
                try
                {
                    using var newTransaction = await _context.Database.BeginTransactionAsync();
                    await _context.SaveEventToOutboxAsync(new InventoryCompensated(correlationId, orderId, false));
                    await newTransaction.CommitAsync();
                }
                catch (Exception outboxEx)
                {
                    _logger.LogError(outboxEx, $"Failed to save inventory compensation failure event to outbox for Order {orderId}");
                }
            }
        }
    }
}