using Microsoft.Extensions.Options;

namespace Hookline.Api.Worker;

/// <summary>
/// Takes due deliveries and sends them. Any number of workers, in one process or many, can run at once; the
/// database decides who sends what.
/// </summary>
public sealed partial class DeliveryWorker(
    IServiceScopeFactory scopes,
    DeliverySender sender,
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryWorker> logger)
{
    /// <summary>Leases up to <paramref name="limit"/> due deliveries to this worker.</summary>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(int limit, CancellationToken stopping)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DeliveryStore>().ClaimAsync(limit, stopping);
    }

    /// <summary>
    /// Sends one claimed delivery and records how it went. A failure here is logged, not thrown: the lease
    /// runs out and the delivery is taken again.
    /// </summary>
    public async Task DeliverAsync(ClaimedDelivery delivery, CancellationToken stopping)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<DeliveryStore>();

            if (!delivery.SubscriptionEnabled)
            {
                await store.RetireAsync(delivery, stopping);
                return;
            }

            var attempt = await sender.SendAsync(delivery, stopping);

            // The request has gone out, so the result is saved even if the app is stopping. Dropping it would
            // only mean sending the event again later.
            if (!await store.RecordAsync(delivery, attempt, CancellationToken.None))
            {
                LogLostLease(logger, delivery.Id);
            }
        }
        catch (Exception e) when (!stopping.IsCancellationRequested)
        {
            LogFailed(logger, e, delivery.Id);
        }
    }

    /// <summary>
    /// One pass: take what is due, send it in parallel up to the limit, and record each result as it comes in.
    /// The tests drive the worker this way; the background service keeps requests flowing instead.
    /// </summary>
    /// <returns>How many deliveries the pass took.</returns>
    public async Task<int> RunOnceAsync(CancellationToken stopping)
    {
        var claimed = await ClaimAsync(options.Value.BatchSize, stopping);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.Value.Parallelism, CancellationToken = stopping };
        await Parallel.ForEachAsync(claimed, parallel, async (delivery, token) => await DeliverAsync(delivery, token));
        return claimed.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} was taken by another worker after its lease ran out. That worker's result stands.")]
    private static partial void LogLostLease(ILogger logger, string deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} failed in the worker. It will be taken again when its lease runs out.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string deliveryId);
}
