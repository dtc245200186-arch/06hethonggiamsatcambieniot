namespace Sensor.Api.Data;

public class SensorEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public double MinThreshold { get; set; }
    public double MaxThreshold { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<SensorReadingEntity> Readings { get; set; } = [];
}

public class SensorReadingEntity
{
    public Guid Id { get; set; }
    public Guid SensorId { get; set; }
    public double Value { get; set; }
    public DateTime RecordedAt { get; set; }
    public bool IsAlert { get; set; }
    public string? AlertMessage { get; set; }

    public SensorEntity? Sensor { get; set; }
}