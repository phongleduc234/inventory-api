using InventoryApi.Consumers;
using InventoryApi.Data;
using MassTransit;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Configure Entity Framework and PostgreSQL
builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register IHttpClientFactory
builder.Services.AddHttpClient();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<InventoryConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitConfig = builder.Configuration.GetSection("RabbitMq");
        var host = rabbitConfig["Host"] ?? "localhost";
        var port = rabbitConfig.GetValue<int>("Port", 5672);
        var username = rabbitConfig["UserName"] ?? "guest";
        var password = rabbitConfig["Password"] ?? "guest";

        cfg.Host(new Uri($"rabbitmq://{host}:{port}"), h =>
        {
            h.Username(username);
            h.Password(password);
        });

        // Thêm cấu hình retry toàn cục
        cfg.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)
        ));
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));

        // Cấu hình queue cho UpdateInventory
        cfg.ReceiveEndpoint("update-inventory", e =>
        {
            e.ConfigureConsumer<InventoryConsumer>(context);
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
            e.BindDeadLetterQueue("inventory-dlq", "inventory-dlx",
                dlq => dlq.Durable = true);
        });

        // Cập nhật cấu hình cho CompensateInventory
        cfg.ReceiveEndpoint("compensate-inventory", e =>
        {
            e.ConfigureConsumer<InventoryConsumer>(context);
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
            // Thêm DLQ
            e.BindDeadLetterQueue("compensate-inventory-dlq", "compensate-inventory-dlx",
                dlq => dlq.Durable = true);
        });

        // Thêm endpoint xử lý dead letter messages nếu cần
        cfg.ReceiveEndpoint("inventory-dead-letter-queue", e =>
        {
            e.Handler<object>(async context =>
            {
                var logger = context.GetPayload<ILogger<object>>();
                logger.LogError("Dead-lettered inventory message received: {MessageId}", context.MessageId);
                // Thêm xử lý
            });

            // Bind các dead letter queues
            e.Bind("inventory-dlq");
            e.Bind("compensate-inventory-dlq");
        });
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Order API V1");
    c.RoutePrefix = "swagger";
});

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.MapHealthChecks("/health");
app.UseRouting();

app.UseAuthorization();

app.MapControllers();


app.Run();
