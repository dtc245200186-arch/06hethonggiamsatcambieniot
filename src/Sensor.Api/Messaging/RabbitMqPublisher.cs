using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using Shared.Contracts;

namespace Sensor.Api.Messaging;

public class RabbitMqPublisher : IAsyncDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RabbitMqPublisher(IConfiguration config, ILogger<RabbitMqPublisher> logger)
    {
        _config = config;
        _logger = logger;
    }

    private async Task EnsureChannelAsync(CancellationToken ct = default)
    {
        if (_channel is { IsOpen: true }) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true }) return;

            var factory = new ConnectionFactory
            {
                HostName = _config["RabbitMQ:HostName"] ?? "localhost",
                Port = int.Parse(_config["RabbitMQ:Port"] ?? "5672"),
                UserName = _config["RabbitMQ:UserName"] ?? "admin",
                Password = _config["RabbitMQ:Password"] ?? "admin123"
            };

            _connection = await factory.CreateConnectionAsync(ct);
            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

            await _channel.QueueDeclareAsync(
                queue: QueueNames.SensorReadings,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PublishReadingAsync(SensorReadingReceivedEvent evt, CancellationToken ct = default)
    {
        await EnsureChannelAsync(ct);

        var json = JsonSerializer.Serialize(evt);
        var body = Encoding.UTF8.GetBytes(json);

        var props = new BasicProperties
        {
            Persistent = true,
            CorrelationId = evt.CorrelationId.ToString()
        };

        await _channel!.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: QueueNames.SensorReadings,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);

        _logger.LogInformation(
            "[CorrelationId: {CorrelationId}] Published reading for Sensor {SensorId}: Value={Value}",
            evt.CorrelationId, evt.SensorId, evt.Value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _lock.Dispose();
    }
}