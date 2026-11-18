namespace Hookline.Api.Data;

/// <summary>A URL that wants events, the types it wants (none listed means all), and the secret that signs them.</summary>
public sealed class Subscription
{
    public required string Id { get; init; }

    public required string Url { get; init; }

    public required string[] EventTypes { get; init; }

    public required string Secret { get; init; }

    public bool Enabled { get; set; } = true;

    public string? DisabledReason { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public bool Wants(string eventType) => EventTypes.Length == 0 || EventTypes.Contains(eventType);
}

/// <summary>
/// Something that happened in the sending app. The body is the exact JSON every delivery sends, written once
/// when the event arrives, so each retry sends the same bytes and the signature always matches them.
/// </summary>
public sealed class Event
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public required string Body { get; init; }

    public string? IdempotencyKey { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public enum DeliveryStatus
{
    Pending,
    Delivered,
    Dead,
}

/// <summary>
/// One event on its way to one subscription. A worker holds it while <see cref="LockedUntil"/> is in the
/// future; that lease, with its <see cref="LeaseId"/>, is what keeps other workers away while it sends.
/// </summary>
public sealed class Delivery
{
    public required string Id { get; init; }

    public required string EventId { get; init; }

    public Event Event { get; init; } = null!;

    public required string SubscriptionId { get; init; }

    public Subscription Subscription { get; init; } = null!;

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public Guid? LeaseId { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public string? LastError { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public List<DeliveryAttempt> AttemptLog { get; init; } = [];
}

/// <summary>What happened on one attempt: the answer, or why there wasn't one.</summary>
public sealed class DeliveryAttempt
{
    public long Id { get; init; }

    public required string DeliveryId { get; init; }

    public required int Number { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required int DurationMs { get; init; }

    public int? StatusCode { get; init; }

    public string? Error { get; init; }

    public string? ResponseExcerpt { get; init; }
}
