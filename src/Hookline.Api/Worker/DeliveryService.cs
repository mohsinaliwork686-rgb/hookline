using Microsoft.Extensions.Options;

namespace Hookline.Api.Worker;

/// <summary>Runs delivery passes for as long as the app runs, resting between passes that find little to do.</summary>
public sealed partial class DeliveryService(
    DeliveryWorker worker,
    TimeProvider clock,
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var taken = 0;
            try
            {
                taken = await worker.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Most likely the database is unreachable. Keep going; the next pass tries again.
                LogPassFailed(logger, e);
            }

            // A full batch means there may be more waiting, so go straight round again.
            if (taken < options.Value.BatchSize)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A delivery pass failed.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
