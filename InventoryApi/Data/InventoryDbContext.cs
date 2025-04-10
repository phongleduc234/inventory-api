using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Data
{
    public class InventoryDbContext : DbContext
    {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options)
        {
        }

        // Define your DbSets here
        public DbSet<InventoryItem> InventoryItems { get; set; }
        public DbSet<OutboxMessage> OutboxMessages { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<InventoryItem>(b =>
            {
                b.HasIndex(p => p.ProductId).IsUnique();
            });
            modelBuilder.Entity<OutboxMessage>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.EventType).HasMaxLength(100);
            });

            modelBuilder.Entity<InventoryItem>().ToTable("Payments");
            modelBuilder.Entity<OutboxMessage>().ToTable("OutboxMessages");
        }
    }

    public class InventoryItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string ProductId { get; set; }
        public int Quantity { get; set; }
        public int ReservedQuantity { get; set; } // Số lượng đã đặt hàng
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }


    public class OutboxMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string EventType { get; set; }
        public string EventData { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool Processed { get; set; }
    }
}
