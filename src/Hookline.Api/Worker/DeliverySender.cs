using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

using Hookline.Core;

using Microsoft.Extensions.Options;

namespace Hookline.Api.Worker;

/// <summary>What one attempt came back with: a status code, or the reason there wasn't one.</summary>
public sealed record AttemptResult(
    DateTimeOffset StartedAt,
    int DurationMs,
    int? StatusCode,
    string? Error,
    string? ResponseExcerpt,
    TimeSpan? RetryAfter)
{
    public AttemptOutcome Outcome => StatusCode is { } code ? Responses.Classify(code) : AttemptOutcome.Failed;

    /// <summary>One line for the delivery's last error.</summary>
    public string Summary => StatusCode switch
    {
        null => Error ?? "The attempt failed.",
        410 => "Answered 410 Gone, so the subscription is disabled.",
        >= 300 and <= 399 => $"Answered {StatusCode}. Redirects aren't followed.",
        _ => $"Answered {StatusCode}.",
    };
}

/// <summary>
/// Sends one delivery: a POST of the event's saved body, signed the Standard Webhooks way with a timestamp
/// taken now, so every retry carries a fresh, valid signature over the same bytes.
/// </summary>
public sealed class DeliverySender(IHttpClientFactory httpClients, TimeProvider clock, IOptions<DeliveryOptions> options)
{
    public const string ClientName = "deliveries";

    private const int ExcerptBytes = 4096;
    private const int ExcerptChars = 1000;
    private const int ErrorChars = 2000;

    /// <param name="stopping">Cancelled when the app shuts down. The attempt is then dropped, not recorded,
    /// and the delivery is taken again once its lease runs out.</param>
    public async Task<AttemptResult> SendAsync(ClaimedDelivery delivery, CancellationToken stopping)
    {
        var body = Encoding.UTF8.GetBytes(delivery.Body);
        var startedAt = clock.GetUtcNow();
        var timestamp = startedAt.ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Add(WebhookSignature.IdHeader, delivery.EventId);
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Sign(delivery.Secret, delivery.EventId, timestamp, body));

        // The timeout is real time, not the injected clock: it is about the network, while the clock is about
        // the retry schedule. It covers everything up to the answer's headers and the start of its body.
        var timeout = options.Value.Timeout;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        attempt.CancelAfter(timeout);
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await httpClients.CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            var code = (int)response.StatusCode;
            var retryAfter = code is 429 or 503 && response.Headers.TryGetValues("Retry-After", out var values)
                ? Responses.RetryAfter(values.FirstOrDefault(), clock.GetUtcNow())
                : null;
            var excerpt = await ExcerptAsync(response.Content, attempt.Token, stopping);
            return new AttemptResult(startedAt, Elapsed(started), code, null, excerpt, retryAfter);
        }
        catch (Exception e) when (e is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            var error = e is OperationCanceledException
                ? string.Create(CultureInfo.InvariantCulture, $"Timed out after {timeout.TotalSeconds:0.###} s.")
                : Limit(e.Message, ErrorChars);
            return new AttemptResult(startedAt, Elapsed(started), null, error, null, null);
        }
    }

    /// <summary>
    /// The start of the answer, for the dashboard. The status code has already arrived and is what counts, so a
    /// body that is slow or broken only shortens the excerpt.
    /// </summary>
    private static async Task<string?> ExcerptAsync(HttpContent content, CancellationToken attempt, CancellationToken stopping)
    {
        var buffer = new byte[ExcerptBytes];
        var read = 0;
        try
        {
            await using var stream = await content.ReadAsStreamAsync(attempt);
            int count;
            while (read < buffer.Length && (count = await stream.ReadAsync(buffer.AsMemory(read), attempt)) > 0)
            {
                read += count;
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException
            || (e is OperationCanceledException && !stopping.IsCancellationRequested))
        {
        }

        if (read == 0)
        {
            return null;
        }

        // PostgreSQL text can't hold a NUL, and a receiver may answer with anything.
        return Limit(Encoding.UTF8.GetString(buffer, 0, read).Replace('\0', '\uFFFD'), ExcerptChars);
    }

    private static int Elapsed(long started) => (int)Math.Min(Stopwatch.GetElapsedTime(started).TotalMilliseconds, int.MaxValue);

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length];
}
