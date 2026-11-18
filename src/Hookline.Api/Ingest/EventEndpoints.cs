using System.Text.Json;

using Hookline.Api.Data;
using Hookline.Core;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Hookline.Api.Ingest;

public sealed record PostEvent(string? Type, JsonElement? Data);

public sealed record AcceptedEvent(string Id, string Type, int Deliveries);

public sealed record DeliverySummary(
    string Id,
    string SubscriptionId,
    string Url,
    DeliveryStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? DeliveredAt,
    string? LastError);

public sealed record EventResponse(string Id, string Type, DateTimeOffset CreatedAt, JsonElement Data, List<DeliverySummary> Deliveries);

/// <summary>What every delivery of an event sends, in the shape Standard Webhooks suggests.</summary>
public sealed record EventEnvelope(string Id, string Type, DateTime Timestamp, JsonElement Data);

public static class EventEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    public const int MaxIdempotencyKeyLength = 255;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapEvents(this RouteGroupBuilder api)
    {
        var events = api.MapGroup("/events");
        events.MapPost("", PostAsync);
        events.MapGet("/{id}", GetAsync);
    }

    /// <summary>
    /// Saves the event and one delivery for each subscription that wants it, in one transaction, and only then
    /// answers. From the moment the sending app gets its 202, nothing can be lost.
    /// </summary>
    private static async Task<IResult> PostAsync(
        PostEvent request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        HooklineDbContext db,
        TimeProvider clock,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Type) || request.Type.Length > SubscriptionEndpoints.MaxEventTypeLength)
        {
            errors["type"] = [$"The event type, 1 to {SubscriptionEndpoints.MaxEventTypeLength} characters, such as order.paid."];
        }

        if (request.Data is not { ValueKind: not JsonValueKind.Undefined })
        {
            errors["data"] = ["The event's data, as any JSON value."];
        }

        if (idempotencyKey is { Length: 0 or > MaxIdempotencyKeyLength })
        {
            errors[IdempotencyHeader] = [$"1 to {MaxIdempotencyKeyLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var type = request.Type!;
        var data = request.Data!.Value;

        if (idempotencyKey is not null && await ReplayAsync(db, idempotencyKey, type, data, response, cancellationToken) is { } replay)
        {
            return replay;
        }

        var now = clock.GetUtcNow();
        var id = Ids.New(Ids.Event);
        var @event = new Event
        {
            Id = id,
            Type = type,
            Body = JsonSerializer.Serialize(new EventEnvelope(id, type, now.UtcDateTime, data), Json),
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
        };

        var subscribers = await db.Subscriptions
            .Where(s => s.Enabled && (s.EventTypes.Length == 0 || s.EventTypes.Contains(type)))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        db.Events.Add(@event);
        db.Deliveries.AddRange(subscribers.Select(subscriptionId => new Delivery
        {
            Id = Ids.New(Ids.Delivery),
            EventId = @event.Id,
            SubscriptionId = subscriptionId,
            NextAttemptAt = now,
            CreatedAt = now,
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (idempotencyKey is not null && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: HooklineDbContext.IdempotencyIndex })
        {
            // Another request with the same key got in first, between our check and our save.
            db.ChangeTracker.Clear();
            return await ReplayAsync(db, idempotencyKey, type, data, response, cancellationToken)
                ?? throw new InvalidOperationException("The idempotency key clashed, but no event holds it.", e);
        }

        return Results.Accepted($"/api/events/{@event.Id}", new AcceptedEvent(@event.Id, type, subscribers.Count));
    }

    /// <summary>
    /// The answer for a key that was used before: the first event, if this request is the same one again, or a
    /// conflict if the key now comes with a different event.
    /// </summary>
    private static async Task<IResult?> ReplayAsync(
        HooklineDbContext db,
        string idempotencyKey,
        string type,
        JsonElement data,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var existing = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var envelope = JsonSerializer.Deserialize<EventEnvelope>(existing.Body, Json)!;
        if (existing.Type != type || !JsonElement.DeepEquals(envelope.Data, data))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "This idempotency key was already used for a different event.");
        }

        var deliveries = await db.Deliveries.CountAsync(d => d.EventId == existing.Id, cancellationToken);
        response.Headers[ReplayedHeader] = "true";
        return Results.Accepted($"/api/events/{existing.Id}", new AcceptedEvent(existing.Id, existing.Type, deliveries));
    }

    private static async Task<IResult> GetAsync(string id, HooklineDbContext db, CancellationToken cancellationToken)
    {
        var @event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (@event is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such event.");
        }

        var deliveries = await db.Deliveries.AsNoTracking()
            .Where(d => d.EventId == id)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Select(d => new DeliverySummary(d.Id, d.SubscriptionId, d.Subscription.Url, d.Status, d.Attempts, d.NextAttemptAt, d.DeliveredAt, d.LastError))
            .ToListAsync(cancellationToken);
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(@event.Body, Json)!;
        return Results.Ok(new EventResponse(@event.Id, @event.Type, @event.CreatedAt, envelope.Data, deliveries));
    }
}
