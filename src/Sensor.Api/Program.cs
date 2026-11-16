using Microsoft.EntityFrameworkCore;
using Sensor.Api.Data;
using Sensor.Api.Messaging;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

// 1. Đăng ký PostgreSQL & EF Core
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Đăng ký RabbitMQ Publisher
builder.Services.AddSingleton<RabbitMqPublisher>();

// 3. Health Check (Yêu cầu mở rộng ăn điểm)
builder.Services.AddHealthChecks();

var app = builder.Build();

// Tự động tạo bảng trong PostgreSQL nếu chưa có (kèm retry khi DB mới khởi động)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (int i = 1; i <= 10; i++)
    {
        try
        {
            await db.Database.EnsureCreatedAsync();
            logger.LogInformation("Database initialized successfully.");
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Waiting for database... Attempt {Attempt}/10. Error: {Message}", i, ex.Message);
            await Task.Delay(2000);
        }
    }
}

app.MapHealthChecks("/health");

// API 1: POST /sensors - Đăng ký thông tin cảm biến
app.MapPost("/sensors", async (CreateSensorRequest req, AppDbContext db, ILogger<Program> logger) =>
{
    var sensor = new SensorEntity
    {
        Id = Guid.NewGuid(),
        Name = req.Name,
        Type = req.Type,
        Location = req.Location,
        MinThreshold = req.MinThreshold,
        MaxThreshold = req.MaxThreshold,
        CreatedAt = DateTime.UtcNow
    };

    db.Sensors.Add(sensor);
    await db.SaveChangesAsync();

    logger.LogInformation("Registered new sensor: {Name} ({Id})", sensor.Name, sensor.Id);

    var dto = new SensorDto(sensor.Id, sensor.Name, sensor.Type, sensor.Location, sensor.MinThreshold, sensor.MaxThreshold, sensor.CreatedAt);
    return Results.Created($"/sensors/{sensor.Id}", dto);
});

// API 2: GET /sensors - Lấy danh sách cảm biến
app.MapGet("/sensors", async (AppDbContext db) =>
{
    var sensors = await db.Sensors
        .OrderByDescending(s => s.CreatedAt)
        .Select(s => new SensorDto(s.Id, s.Name, s.Type, s.Location, s.MinThreshold, s.MaxThreshold, s.CreatedAt))
        .ToListAsync();

    return Results.Ok(sensors);
});

// API 3: POST /sensors/{id}/readings - Tiếp nhận dữ liệu đo và đẩy vào RabbitMQ
app.MapPost("/sensors/{id:guid}/readings", async (
    Guid id,
    CreateReadingRequest req,
    AppDbContext db,
    RabbitMqPublisher publisher,
    ILogger<Program> logger) =>
{
    var exists = await db.Sensors.AnyAsync(s => s.Id == id);
    if (!exists)
        return Results.NotFound(new { message = $"Sensor {id} not found." });

    var correlationId = Guid.NewGuid();
    var recordedAt = req.RecordedAt?.ToUniversalTime() ?? DateTime.UtcNow;

    var evt = new SensorReadingReceivedEvent(correlationId, id, req.Value, recordedAt);
    await publisher.PublishReadingAsync(evt);

    return Results.Accepted($"/sensors/{id}/readings/latest", new
    {
        correlationId,
        status = "Queued",
        sensorId = id,
        value = req.Value,
        recordedAt
    });
});

// API 4: GET /sensors/{id}/readings/latest - Tra cứu dữ liệu gần nhất
app.MapGet("/sensors/{id:guid}/readings/latest", async (Guid id, AppDbContext db) =>
{
    var latest = await db.SensorReadings
        .Where(r => r.SensorId == id)
        .OrderByDescending(r => r.RecordedAt)
        .Select(r => new SensorReadingDto(r.Id, r.SensorId, r.Value, r.RecordedAt, r.IsAlert, r.AlertMessage))
        .FirstOrDefaultAsync();

    return latest is not null ? Results.Ok(latest) : Results.NotFound(new { message = "No readings found." });
});

// API 5: GET /sensors/{id}/readings - Tra cứu lịch sử dữ liệu theo khoảng thời gian
app.MapGet("/sensors/{id:guid}/readings", async (
    Guid id,
    DateTime? from,
    DateTime? to,
    AppDbContext db) =>
{
    var query = db.SensorReadings.Where(r => r.SensorId == id);

    if (from.HasValue)
        query = query.Where(r => r.RecordedAt >= from.Value.ToUniversalTime());

    if (to.HasValue)
        query = query.Where(r => r.RecordedAt <= to.Value.ToUniversalTime());

    var readings = await query
        .OrderByDescending(r => r.RecordedAt)
        .Take(100)
        .Select(r => new SensorReadingDto(r.Id, r.SensorId, r.Value, r.RecordedAt, r.IsAlert, r.AlertMessage))
        .ToListAsync();

    return Results.Ok(readings);
});

app.Run();