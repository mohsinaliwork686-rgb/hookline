using Microsoft.Extensions.Options;

namespace Hookline.Api.Worker;

/// <summary>
/// Keeps up to <see cref="DeliveryOptions.Parallelism"/> requests in flight for as long as the app runs. It takes
/// new work whenever a request finishes, not when a whole batch has, so a receiver that hangs until the timeout
/// holds up only its own delivery. It takes no more than it has room to send at once, so nothing it holds sits
/// waiting while its lease runs down.
/// </summary>
public sealed partial class DeliveryService(
    DeliveryWorker worker,
    TimeProvider clock,
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        // Only this loop takes slots; each request gives its slot back when it is done.
        using var slots = new SemaphoreSlim(settings.Parallelism, settings.Parallelism);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                var wanted = Math.Min(slots.CurrentCount + 1, settings.BatchSize);

                IReadOnlyList<ClaimedDelivery> claimed = [];
                try
                {
                    claimed = await worker.ClaimAsync(wanted, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    slots.Release();
                    break;
                }
                catch (Exception e)
                {
                    // Most likely the database is unreachable. The next round tries again.
                    LogClaimFailed(logger, e);
                }

                if (claimed.Count == 0)
                {
                    slots.Release();
                }

                for (var i = 0; i < claimed.Count; i++)
                {
                    if (i > 0)
                    {
                        await slots.WaitAsync(CancellationToken.None);
                    }

                    var delivery = claimed[i];
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await worker.DeliverAsync(delivery, stoppingToken);
                            }
                            finally
                            {
                                slots.Release();
                            }
                        },
                        CancellationToken.None);
                }

                // Fewer than asked for means nothing else is due yet.
                if (claimed.Count < wanted)
                {
                    await Task.Delay(settings.PollInterval, clock, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Let requests in flight finish and record their results before the slots go away.
            for (var i = 0; i < settings.Parallelism; i++)
            {
                await slots.WaitAsync(CancellationToken.None);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Taking due deliveries failed.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception);
}
