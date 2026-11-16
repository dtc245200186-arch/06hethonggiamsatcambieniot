using Microsoft.EntityFrameworkCore;
using Sensor.Api.Data;
using Sensor.Api.Messaging;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Tự động khởi tạo bảng trong PostgreSQL
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

// 1. GET / - Endpoint kiểm tra trạng thái dịch vụ (Chuẩn Bài TH 1.2 & 2.2)
app.MapGet("/", () => Results.Ok(new
{
    service = "IoT Sensor Monitoring API (Đề 06)",
    status = "Running",
    timestampUtc = DateTime.UtcNow
}));

// 2. GET /sensors?keyword=... - Lấy danh sách & lọc theo query string (Chuẩn Bài TH 2.2 - Bước 13)
app.MapGet("/sensors", async (string? keyword, AppDbContext db) =>
{
    var query = db.Sensors.AsQueryable();

    if (!string.IsNullOrWhiteSpace(keyword))
    {
        var kw = keyword.Trim().ToLower();
        query = query.Where(s => s.Name.ToLower().Contains(kw) || s.Location.ToLower().Contains(kw));
    }

    var sensors = await query
        .OrderByDescending(s => s.CreatedAt)
        .Select(s => new SensorDto(s.Id, s.Name, s.Type, s.Location, s.MinThreshold, s.MaxThreshold, s.CreatedAt))
        .ToListAsync();

    return Results.Ok(sensors);
});

// 3. GET /sensors/{id:guid} - Lấy chi tiết cảm biến theo Route Constraint (Chuẩn Bài TH 2.2 - Bước 4)
app.MapGet("/sensors/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var sensor = await db.Sensors
        .Where(s => s.Id == id)
        .Select(s => new SensorDto(s.Id, s.Name, s.Type, s.Location, s.MinThreshold, s.MaxThreshold, s.CreatedAt))
        .FirstOrDefaultAsync();

    return sensor is not null
        ? Results.Ok(sensor)
        : Results.NotFound(new ApiError("SENSOR_NOT_FOUND", $"Không tìm thấy cảm biến có mã {id}."));
});

// 4. POST /sensors - Đăng ký cảm biến mới kèm Validation & 201 Created (Chuẩn Bài TH 1.2 & 2.2)
app.MapPost("/sensors", async (CreateSensorRequest req, AppDbContext db, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Type) || string.IsNullOrWhiteSpace(req.Location))
    {
        return Results.BadRequest(new ApiError("INVALID_SENSOR", "Tên, loại cảm biến và vị trí không được để trống."));
    }

    if (req.MinThreshold >= req.MaxThreshold)
    {
        return Results.BadRequest(new ApiError("INVALID_THRESHOLD", "Ngưỡng dưới (MinThreshold) phải nhỏ hơn ngưỡng trên (MaxThreshold)."));
    }

    var sensor = new SensorEntity
    {
        Id = Guid.NewGuid(),
        Name = req.Name.Trim(),
        Type = req.Type.Trim(),
        Location = req.Location.Trim(),
        MinThreshold = req.MinThreshold,
        MaxThreshold = req.MaxThreshold,
        CreatedAt = DateTime.UtcNow
    };

    db.Sensors.Add(sensor);
    await db.SaveChangesAsync();

    logger.LogInformation("Registered sensor: {Name} ({Id})", sensor.Name, sensor.Id);

    var dto = new SensorDto(sensor.Id, sensor.Name, sensor.Type, sensor.Location, sensor.MinThreshold, sensor.MaxThreshold, sensor.CreatedAt);
    return Results.Created($"/sensors/{sensor.Id}", dto);
});

// 5. POST /sensors/{id}/readings - Tiếp nhận dữ liệu đo và đẩy vào RabbitMQ
app.MapPost("/sensors/{id:guid}/readings", async (
    Guid id,
    CreateReadingRequest req,
    AppDbContext db,
    RabbitMqPublisher publisher) =>
{
    var exists = await db.Sensors.AnyAsync(s => s.Id == id);
    if (!exists)
        return Results.NotFound(new ApiError("SENSOR_NOT_FOUND", $"Không tìm thấy cảm biến {id}."));

    if (double.IsNaN(req.Value) || double.IsInfinity(req.Value))
        return Results.BadRequest(new ApiError("INVALID_READING", "Giá trị đo không hợp lệ."));

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

// 6. GET /sensors/{id}/readings/latest - Tra cứu dữ liệu đo gần nhất
app.MapGet("/sensors/{id:guid}/readings/latest", async (Guid id, AppDbContext db) =>
{
    var latest = await db.SensorReadings
        .Where(r => r.SensorId == id)
        .OrderByDescending(r => r.RecordedAt)
        .Select(r => new SensorReadingDto(r.Id, r.SensorId, r.Value, r.RecordedAt, r.IsAlert, r.AlertMessage))
        .FirstOrDefaultAsync();

    return latest is not null
        ? Results.Ok(latest)
        : Results.NotFound(new ApiError("READING_NOT_FOUND", "Cảm biến chưa có dữ liệu đo."));
});

// 7. GET /sensors/{id}/readings?from=...&to=... - Tra cứu lịch sử theo khoảng thời gian
app.MapGet("/sensors/{id:guid}/readings", async (Guid id, DateTime? from, DateTime? to, AppDbContext db) =>
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