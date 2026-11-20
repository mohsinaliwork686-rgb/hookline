using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.Extensions.Options;

namespace Hookline.DemoReceiver;

public sealed class DemoOptions
{
    /// <summary>Where hookline's API is, from inside the compose network.</summary>
    public string HooklineUrl { get; set; } = "http://hookline:8080";

    public string ApiKey { get; set; } = "";

    /// <summary>Where hookline can reach this receiver.</summary>
    public string ReceiverUrl { get; set; } = "http://demo:8080";

    public int Events { get; set; } = 12;

    public TimeSpan Every { get; set; } = TimeSpan.FromSeconds(4);
}

/// <summary>Each path's signing secret, learnt when the sending app creates its subscription.</summary>
public sealed class Secrets
{
    private readonly ConcurrentDictionary<string, string> _byPath = new();

    public void Add(string path, string secret) => _byPath[path] = secret;

    public string? For(string path) => _byPath.GetValueOrDefault(path);
}

public static partial class Receiver
{
    public static readonly string[] Paths = ["ok", "flaky", "slow", "down", "gone"];

    /// <summary>Longer than hookline's timeout, so every attempt here times out.</summary>
    public static async Task<int> SlowAsync(CancellationToken aborted)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), aborted);
            return 200;
        }
        catch (OperationCanceledException)
        {
            // hookline gave up first and closed the connection.
            return 499;
        }
    }

    public static void Log(ILogger logger, string path, string id, string signature, int status)
    {
        if (status == 499)
        {
            LogGaveUp(logger, path, id, signature);
        }
        else
        {
            LogRequest(logger, path, id, signature, status);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "/{Path,-5} {Id}  {Signature}  answered {Status}")]
    private static partial void LogRequest(ILogger logger, string path, string id, string signature, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "/{Path,-5} {Id}  {Signature}  hookline timed out and hung up")]
    private static partial void LogGaveUp(ILogger logger, string path, string id, string signature);
}

/// <summary>The sending app: subscribes each path once hookline is up, then posts a stream of events.</summary>
public sealed partial class SendingApp(
    IHttpClientFactory httpClients,
    Secrets secrets,
    IOptions<DemoOptions> options,
    ILogger<SendingApp> logger) : BackgroundService
{
    private sealed record Created(string Id, string Secret);

    private sealed record Accepted(string Id, int Deliveries);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var demo = options.Value;
        var hookline = httpClients.CreateClient();
        hookline.BaseAddress = new Uri(demo.HooklineUrl);
        hookline.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", demo.ApiKey);

        await WaitForHooklineAsync(hookline, stoppingToken);

        foreach (var path in Receiver.Paths)
        {
            var response = await hookline.PostAsJsonAsync("/api/subscriptions", new { url = $"{demo.ReceiverUrl}/{path}" }, stoppingToken);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<Created>(stoppingToken)
                ?? throw new InvalidOperationException("hookline sent an empty answer.");
            secrets.Add(path, created.Secret);
            LogSubscribed(logger, path, created.Id);
        }

        for (var i = 1; i <= demo.Events && !stoppingToken.IsCancellationRequested; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
            {
                Content = JsonContent.Create(new { type = i % 3 == 0 ? "order.refunded" : "order.paid", data = new { orderId = 1000 + i, amount = 25 * i } }),
            };
            request.Headers.Add("Idempotency-Key", $"demo-order-{i}");
            var response = await hookline.SendAsync(request, stoppingToken);
            response.EnsureSuccessStatusCode();
            var accepted = await response.Content.ReadFromJsonAsync<Accepted>(stoppingToken);
            LogPosted(logger, accepted?.Id ?? "?", accepted?.Deliveries ?? 0);
            await Task.Delay(demo.Every, stoppingToken);
        }

        LogDone(logger, demo.Events);
    }

    private async Task WaitForHooklineAsync(HttpClient hookline, CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                if ((await hookline.GetAsync("/health", stoppingToken)).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            LogWaiting(logger);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for hookline to start...")]
    private static partial void LogWaiting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscribed /{Path} as {SubscriptionId}")]
    private static partial void LogSubscribed(ILogger logger, string path, string subscriptionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Posted {EventId}, {Deliveries} deliveries")]
    private static partial void LogPosted(ILogger logger, string eventId, int deliveries);

    [LoggerMessage(Level = LogLevel.Information, Message = "Posted all {Count} events. The dashboard shows the rest.")]
    private static partial void LogDone(ILogger logger, int count);
}
