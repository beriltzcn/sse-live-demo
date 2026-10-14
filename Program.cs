using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<EventHub>();
builder.Services.AddHostedService<MetricTicker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/events", (HttpContext http, EventHub hub, CancellationToken ct) =>
    TypedResults.ServerSentEvents(hub.Subscribe(http.Request.Headers["Last-Event-ID"], ct)));

app.MapGet("/clients", (EventHub hub) => hub.ClientCount);

app.Run();

sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<SseItem<string>>> _clients = new();
    private long _sequence;

    public int ClientCount => _clients.Count;

    public IAsyncEnumerable<SseItem<string>> Subscribe(string? lastEventId, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<SseItem<string>>();
        var clientId = Guid.NewGuid();
        _clients[clientId] = channel;

        return Read(channel, clientId, lastEventId, ct);
    }

    public int Broadcast(string eventType, string data)
    {
        var item = new SseItem<string>(data, eventType)
        {
            EventId = Interlocked.Increment(ref _sequence).ToString()
        };

        foreach (var channel in _clients.Values)
            channel.Writer.TryWrite(item);

        return _clients.Count;
    }

    private async IAsyncEnumerable<SseItem<string>> Read(
        Channel<SseItem<string>> channel,
        Guid clientId,
        string? lastEventId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            if (long.TryParse(lastEventId, out var lastId))
            {
                var missed = Math.Max(0, Interlocked.Read(ref _sequence) - lastId);
                yield return new SseItem<string>(JsonSerializer.Serialize(new { missed }), "resume");
            }

            await foreach (var item in channel.Reader.ReadAllAsync(ct))
                yield return item;
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
        }
    }
}

sealed class MetricTicker(EventHub hub) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var payload = JsonSerializer.Serialize(new
            {
                cpu = Math.Round(Random.Shared.NextDouble() * 50 + 25, 1),
                requests = Random.Shared.Next(80, 260),
            });

            hub.Broadcast("tick", payload);
        }
    }
}
