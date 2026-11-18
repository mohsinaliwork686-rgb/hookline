namespace Hookline.Core;

/// <summary>
/// How long to wait before each attempt. The first entry is the wait before the first attempt, so the
/// number of entries is the number of attempts. Each wait gets up to <see cref="Jitter"/> extra at random,
/// so deliveries that failed together don't all retry in the same second.
/// </summary>
public sealed record RetrySchedule
{
    public RetrySchedule(IReadOnlyList<TimeSpan> waits, double jitter = 0.1)
    {
        if (waits.Count == 0)
        {
            throw new ArgumentException("A schedule needs at least one attempt.", nameof(waits));
        }

        if (waits.Any(w => w < TimeSpan.Zero))
        {
            throw new ArgumentException("A wait can't be negative.", nameof(waits));
        }

        if (jitter is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), "Jitter is a share of the wait, from 0 to 1.");
        }

        Waits = waits;
        Jitter = jitter;
    }

    /// <summary>
    /// The Standard Webhooks schedule: ten attempts over about three days.
    /// </summary>
    public static RetrySchedule Standard { get; } = new(
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(5),
        TimeSpan.FromHours(10),
        TimeSpan.FromHours(14),
        TimeSpan.FromHours(20),
        TimeSpan.FromHours(24),
    ]);

    public IReadOnlyList<TimeSpan> Waits { get; }

    public double Jitter { get; }

    public int MaxAttempts => Waits.Count;

    public TimeSpan LongestWait => Waits.Max();
}
