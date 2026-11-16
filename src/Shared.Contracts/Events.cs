namespace Shared.Contracts;

// Message từ Sensor.Api -> RabbitMQ -> Monitor.Worker
public record SensorReadingReceivedEvent(
    Guid CorrelationId,
    Guid SensorId,
    double Value,
    DateTime RecordedAt
);

// Message từ Monitor.Worker -> RabbitMQ -> Notification.Service -> Dashboard.Client
public record SensorAlertEvent(
    Guid CorrelationId,
    Guid SensorId,
    string SensorName,
    string Location,
    double Value,
    double MinThreshold,
    double MaxThreshold,
    DateTime RecordedAt,
    string Message
);