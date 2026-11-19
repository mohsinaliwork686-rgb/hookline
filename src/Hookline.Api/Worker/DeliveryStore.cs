using Hookline.Api.Data;
using Hookline.Core;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hookline.Api.Worker;

/// <summary>A delivery a worker has taken, with everything it needs to send it.</summary>
public sealed record ClaimedDelivery(
    string Id,
    Guid LeaseId,
    int Attempts,
    string EventId,
    string Body,
    string SubscriptionId,
    string Url,
    string Secret,
    bool SubscriptionEnabled);

/// <summary>
/// The worker's two short transactions: taking due deliveries, and recording how an attempt went. Neither
/// holds a connection while a request is in flight.
/// </summary>
public sealed class DeliveryStore(HooklineDbContext db, TimeProvider clock, IOptions<DeliveryOptions> options)
{
    public const string NotSentDisabled = "Not sent: the subscription is disabled.";

    /// <summary>
    /// Takes up to <paramref name="limit"/> deliveries that are pending, due and not held by another worker,
    /// and leases them to this one.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(int limit, CancellationToken cancellationToken)
    {
        var lease = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var until = now + options.Value.Lease;

        // One statement, so one short transaction. SKIP LOCKED passes over rows another worker is taking at this
        // instant instead of waiting for them, and the lease keeps them away afterwards while this one sends.
        var taken = await db.Database.ExecuteSqlAsync(
            $"""
            WITH due AS (
                SELECT id FROM deliveries
                WHERE status = 'Pending'
                  AND next_attempt_at <= {now}
                  AND (locked_until IS NULL OR locked_until <= {now})
                ORDER BY next_attempt_at
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED)
            UPDATE deliveries SET locked_until = {until}, lease_id = {lease}
            FROM due
            WHERE deliveries.id = due.id
            """,
            cancellationToken);

        if (taken == 0)
        {
            return [];
        }

        return await db.Deliveries.AsNoTracking()
            .Where(d => d.LeaseId == lease)
            .OrderBy(d => d.NextAttemptAt)
            .Select(d => new ClaimedDelivery(
                d.Id,
                lease,
                d.Attempts,
                d.EventId,
                d.Event.Body,
                d.SubscriptionId,
                d.Subscription.Url,
                d.Subscription.Secret,
                d.Subscription.Enabled))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Saves the attempt and moves the delivery on: delivered, due again later, or dead. A 410 also disables
    /// the subscription.
    /// </summary>
    /// <returns>
    /// False when this worker no longer holds the lease. Someone else has taken the delivery since, and their
    /// result stands, so nothing is written.
    /// </returns>
    public async Task<bool> RecordAsync(ClaimedDelivery delivery, AttemptResult attempt, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var number = delivery.Attempts + 1;
        var decision = attempt.Outcome == AttemptOutcome.Delivered
            ? null
            : RetryPolicy.AfterFailure(options.Value.BuildSchedule(), number, attempt.Outcome, now, attempt.RetryAfter, Random.Shared);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var moved = await db.Deliveries
            .Where(d => d.Id == delivery.Id && d.LeaseId == delivery.LeaseId)
            .ExecuteUpdateAsync(
                set =>
                {
                    set.SetProperty(d => d.Attempts, number)
                        .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null)
                        .SetProperty(d => d.LeaseId, (Guid?)null);
                    switch (decision)
                    {
                        case null:
                            set.SetProperty(d => d.Status, DeliveryStatus.Delivered)
                                .SetProperty(d => d.DeliveredAt, (DateTimeOffset?)now)
                                .SetProperty(d => d.LastError, (string?)null);
                            break;
                        case RetryDecision.Retry retry:
                            set.SetProperty(d => d.NextAttemptAt, retry.At)
                                .SetProperty(d => d.LastError, attempt.Summary);
                            break;
                        case RetryDecision.Dead dead:
                            set.SetProperty(d => d.Status, DeliveryStatus.Dead)
                                .SetProperty(d => d.LastError, attempt.Outcome == AttemptOutcome.Gone ? attempt.Summary : $"{attempt.Summary} {dead.Reason}");
                            break;
                    }
                },
                cancellationToken);

        if (moved == 0)
        {
            return false;
        }

        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            DeliveryId = delivery.Id,
            Number = number,
            StartedAt = attempt.StartedAt,
            DurationMs = attempt.DurationMs,
            StatusCode = attempt.StatusCode,
            Error = attempt.Error,
            ResponseExcerpt = attempt.ResponseExcerpt,
        });
        await db.SaveChangesAsync(cancellationToken);

        if (attempt.Outcome == AttemptOutcome.Gone)
        {
            await DisableAsync(delivery.SubscriptionId, "The receiver answered 410 Gone.", now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Ends a delivery whose subscription was disabled after it was created, without sending it.</summary>
    public async Task<bool> RetireAsync(ClaimedDelivery delivery, CancellationToken cancellationToken) =>
        await db.Deliveries
            .Where(d => d.Id == delivery.Id && d.LeaseId == delivery.LeaseId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(d => d.Status, DeliveryStatus.Dead)
                    .SetProperty(d => d.LastError, NotSentDisabled)
                    .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(d => d.LeaseId, (Guid?)null),
                cancellationToken) == 1;

    private async Task DisableAsync(string subscriptionId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.Subscriptions
            .Where(s => s.Id == subscriptionId)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Enabled, false).SetProperty(s => s.DisabledReason, reason), cancellationToken);

        // Its other waiting deliveries end now too. One that another worker holds at this moment ends the next
        // time a worker takes it and finds the subscription disabled.
        await db.Deliveries
            .Where(d => d.SubscriptionId == subscriptionId
                && d.Status == DeliveryStatus.Pending
                && (d.LockedUntil == null || d.LockedUntil <= now))
            .ExecuteUpdateAsync(
                set => set.SetProperty(d => d.Status, DeliveryStatus.Dead).SetProperty(d => d.LastError, NotSentDisabled),
                cancellationToken);
    }
}
