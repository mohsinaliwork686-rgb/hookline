namespace Hookline.Core;

/// <summary>What happens to a delivery after an attempt that didn't succeed.</summary>
public abstract record RetryDecision
{
    private RetryDecision()
    {
    }

    /// <summary>Try again at this time.</summary>
    public sealed record Retry(DateTimeOffset At) : RetryDecision;

    /// <summary>Stop trying; the delivery goes to the dead-letter queue.</summary>
    public sealed record Dead(string Reason) : RetryDecision;
}

/// <summary>
/// Decides when a failed delivery is tried again, or that it is dead. No database, no HTTP and no clock of
/// its own: the caller passes the time and the source of randomness, so tests can pin both.
/// </summary>
public static class RetryPolicy
{
    /// <param name="schedule">The waits between attempts.</param>
    /// <param name="attemptsMade">Attempts so far, counting the one that just failed.</param>
    /// <param name="outcome">How that attempt went.</param>
    /// <param name="now">When it finished.</param>
    /// <param name="retryAfter">How long the receiver asked us to wait, if it did.</param>
    /// <param name="random">Where the jitter comes from.</param>
    public static RetryDecision AfterFailure(
        RetrySchedule schedule,
        int attemptsMade,
        AttemptOutcome outcome,
        DateTimeOffset now,
        TimeSpan? retryAfter,
        Random random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsMade, 1);

        if (outcome == AttemptOutcome.Delivered)
        {
            throw new ArgumentException("A delivered attempt isn't a failure.", nameof(outcome));
        }

        if (outcome == AttemptOutcome.Gone)
        {
            return new RetryDecision.Dead("The receiver answered 410 Gone.");
        }

        if (attemptsMade >= schedule.MaxAttempts)
        {
            return new RetryDecision.Dead($"No attempts left after {attemptsMade}.");
        }

        var wait = schedule.Waits[attemptsMade];
        wait += wait * (schedule.Jitter * random.NextDouble());

        // A receiver that asks us to slow down gets at least what it asked for, but never more than the
        // schedule's longest wait, so a huge Retry-After can't park a delivery for weeks.
        if (retryAfter is { } asked && asked > wait)
        {
            wait = asked < schedule.LongestWait ? asked : schedule.LongestWait;
        }

        return new RetryDecision.Retry(now + wait);
    }
}
