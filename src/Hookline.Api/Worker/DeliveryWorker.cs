using Microsoft.Extensions.Options;

namespace Hookline.Api.Worker;

/// <summary>
/// One pass of delivery: take what is due, send it in parallel up to a limit, and record each result as soon
/// as it comes in, so a slow receiver holds up only its own delivery. Any number of workers, in one process or
/// many, can run passes at once; the database decides who sends what.
/// </summary>
public sealed partial class DeliveryWorker(
    IServiceScopeFactory scopes,
    DeliverySender sender,
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryWorker> logger)
{
    /// <returns>How many deliveries the pass took.</returns>
    public async Task<int> RunOnceAsync(CancellationToken stopping)
    {
        IReadOnlyList<ClaimedDelivery> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            claimed = await scope.ServiceProvider.GetRequiredService<DeliveryStore>().ClaimAsync(options.Value.BatchSize, stopping);
        }

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.Value.Parallelism, CancellationToken = stopping };
        await Parallel.ForEachAsync(claimed, parallel, async (delivery, token) =>
        {
            try
            {
                await DeliverAsync(delivery, token);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                // The lease runs out and another pass tries again. One failure doesn't end the others.
                LogFailed(logger, e, delivery.Id);
            }
        });

        return claimed.Count;
    }

    private async Task DeliverAsync(ClaimedDelivery delivery, CancellationToken stopping)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DeliveryStore>();

        if (!delivery.SubscriptionEnabled)
        {
            await store.RetireAsync(delivery, stopping);
            return;
        }

        var attempt = await sender.SendAsync(delivery, stopping);

        // The request has gone out, so the result is saved even if the app is stopping. Dropping it would only
        // mean sending the event again later.
        if (!await store.RecordAsync(delivery, attempt, CancellationToken.None))
        {
            LogLostLease(logger, delivery.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} was taken by another worker after its lease ran out. That worker's result stands.")]
    private static partial void LogLostLease(ILogger logger, string deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} failed in the worker. It will be taken again when its lease runs out.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string deliveryId);
}
