using Hookline.Core;

namespace Hookline.Api.Worker;

/// <summary>How the delivery worker runs, bound from the <c>Delivery</c> section of configuration.</summary>
public sealed class DeliveryOptions
{
    public const string Section = "Delivery";

    /// <summary>Whether this process runs the worker at all. The API works without it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most deliveries one pass takes.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>The most requests one pass has in flight at once.</summary>
    public int Parallelism { get; set; } = 10;

    /// <summary>How long a taken delivery stays away from other workers. Longer than <see cref="Timeout"/>.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long one attempt may take, from sending to the start of the answer's body.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the worker waits when a pass finds less than a full batch.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The waits before each attempt. Empty means the Standard Webhooks schedule.</summary>
    public TimeSpan[] Schedule { get; set; } = [];

    /// <summary>The most random extra on each wait, as a share of it.</summary>
    public double Jitter { get; set; } = 0.1;

    public RetrySchedule BuildSchedule() => new(Schedule.Length > 0 ? Schedule : RetrySchedule.Standard.Waits, Jitter);

    internal string? Problem() => this switch
    {
        { BatchSize: < 1 } => "Delivery:BatchSize must be at least 1.",
        { Parallelism: < 1 } => "Delivery:Parallelism must be at least 1.",
        _ when Timeout <= TimeSpan.Zero => "Delivery:Timeout must be positive.",
        _ when Lease <= Timeout => "Delivery:Lease must be longer than Delivery:Timeout, or a slow attempt would lose its lease.",
        _ when PollInterval <= TimeSpan.Zero => "Delivery:PollInterval must be positive.",
        _ when Schedule.Any(w => w < TimeSpan.Zero) => "Delivery:Schedule can't have a negative wait.",
        { Jitter: < 0 or > 1 } => "Delivery:Jitter is a share of the wait, from 0 to 1.",
        _ => null,
    };
}
