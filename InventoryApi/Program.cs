using InventoryApi.Consumers;
using InventoryApi.Data;
using MassTransit;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SharedContracts.Models;
using StackExchange.Redis;
using System.Reflection;


var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// Configure Entity Framework and PostgreSQL
builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register IHttpClientFactory
builder.Services.AddHttpClient();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Inventory API",
        Version = "v1",
        Description = "API for inventory management with Outbox Pattern and DLQ support",
        Contact = new OpenApiContact
        {
            Name = "Development Team",
            Email = "jun8124@gmail.com"
        }
    });

    // Add XML comments support
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }

    // Group endpoints by controller
    options.TagActionsBy(api => new[] { api.GroupName ?? api.ActionDescriptor.RouteValues["controller"] });
});

// Add Redis connection
builder.Services.AddSingleton(sp =>
{
    var redisConfig = builder.Configuration.GetSection("Redis");
    var host = redisConfig["Host"] ?? "localhost";
    var port = redisConfig["Port"] ?? "6379";
    var password = redisConfig["Password"] ?? "";

    var configOptions = new ConfigurationOptions
    {
        AbortOnConnectFail = false,
        ConnectRetry = 3, // Tăng số lần thử lại khi kết nối thất bại
        ConnectTimeout = 10000, // Tăng timeout kết nối lên 10 giây
        SyncTimeout = 10000, // Tăng timeout cho các lệnh đồng bộ lên 10 giây
        AsyncTimeout = 10000 // Tăng timeout cho các lệnh bất đồng bộ lên 10 giây
    };

    configOptions.EndPoints.Add($"{host}:{port}");

    if (!string.IsNullOrEmpty(password))
    {
        configOptions.Password = password;
    }
    return ConnectionMultiplexer.Connect(configOptions);
});

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
            // Thêm DLQ với cấu hình đúng
            e.BindDeadLetterQueue("compensate-inventory-dlq", "compensate-inventory-dlx",
                dlq => {
                    dlq.Durable = true;
                    dlq.AutoDelete = false;
                });
        });

        // Thêm endpoint xử lý dead letter messages
        cfg.ReceiveEndpoint("inventory-dead-letter-queue", e =>
        {
            e.Handler<DeadLetterMessage>(async context =>
            {
                var logger = context.GetPayload<ILogger<DeadLetterMessage>>();
                logger.LogError("Dead-lettered inventory message received: {MessageId}", context.Message.MessageId);
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
