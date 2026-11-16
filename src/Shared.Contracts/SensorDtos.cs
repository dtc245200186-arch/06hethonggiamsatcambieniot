namespace Shared.Contracts;

public record CreateSensorRequest(
    string Name,
    string Type,
    string Location,
    double MinThreshold,
    double MaxThreshold
);

public record SensorDto(
    Guid Id,
    string Name,
    string Type,
    string Location,
    double MinThreshold,
    double MaxThreshold,
    DateTime CreatedAt
);

public record CreateReadingRequest(
    double Value,
    DateTime? RecordedAt
);

public record SensorReadingDto(
    Guid Id,
    Guid SensorId,
    double Value,
    DateTime RecordedAt,
    bool IsAlert,
    string? AlertMessage
);

public record ApiError(string Code, string Message);