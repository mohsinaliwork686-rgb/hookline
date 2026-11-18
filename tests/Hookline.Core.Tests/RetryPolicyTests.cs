using Hookline.Core;

namespace Hookline.Core.Tests;

public class RetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 13, 0, 0, TimeSpan.Zero);
    private static readonly RetrySchedule Standard = RetrySchedule.Standard;

    [Fact]
    public void The_standard_schedule_is_ten_attempts_over_about_three_days()
    {
        Assert.Equal(10, Standard.MaxAttempts);
        Assert.Equal(TimeSpan.Zero, Standard.Waits[0]);
        var total = Standard.Waits.Aggregate(TimeSpan.Zero, (sum, wait) => sum + wait);
        Assert.InRange(total.TotalHours, 75, 76);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 5 * 60)]
    [InlineData(3, 30 * 60)]
    [InlineData(4, 2 * 3600)]
    [InlineData(9, 24 * 3600)]
    public void Without_jitter_the_next_attempt_is_the_scheduled_wait_away(int attemptsMade, int seconds)
    {
        var decision = RetryPolicy.AfterFailure(Standard, attemptsMade, AttemptOutcome.Failed, Now, null, new FixedRandom(0));

        Assert.Equal(new RetryDecision.Retry(Now.AddSeconds(seconds)), decision);
    }

    [Fact]
    public void Jitter_adds_at_most_a_tenth_of_the_wait_and_never_brings_it_forward()
    {
        var least = RetryPolicy.AfterFailure(Standard, 3, AttemptOutcome.Failed, Now, null, new FixedRandom(0));
        var most = RetryPolicy.AfterFailure(Standard, 3, AttemptOutcome.Failed, Now, null, new FixedRandom(0.999999));

        Assert.Equal(Now.AddMinutes(30), Assert.IsType<RetryDecision.Retry>(least).At);
        Assert.InRange(Assert.IsType<RetryDecision.Retry>(most).At, Now.AddMinutes(32.9), Now.AddMinutes(33));
    }

    [Fact]
    public void After_the_last_attempt_the_delivery_is_dead()
    {
        var decision = RetryPolicy.AfterFailure(Standard, 10, AttemptOutcome.Failed, Now, null, new FixedRandom(0));

        Assert.IsType<RetryDecision.Dead>(decision);
    }

    [Fact]
    public void A_410_is_dead_at_once_whatever_attempts_are_left()
    {
        var decision = RetryPolicy.AfterFailure(Standard, 1, AttemptOutcome.Gone, Now, null, new FixedRandom(0));

        Assert.Contains("410", Assert.IsType<RetryDecision.Dead>(decision).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retry_after_longer_than_the_wait_is_honoured()
    {
        var decision = RetryPolicy.AfterFailure(Standard, 1, AttemptOutcome.Failed, Now, TimeSpan.FromMinutes(2), new FixedRandom(0));

        Assert.Equal(new RetryDecision.Retry(Now.AddMinutes(2)), decision);
    }

    [Fact]
    public void A_retry_after_shorter_than_the_wait_doesnt_bring_the_attempt_forward()
    {
        var decision = RetryPolicy.AfterFailure(Standard, 3, AttemptOutcome.Failed, Now, TimeSpan.FromSeconds(10), new FixedRandom(0));

        Assert.Equal(new RetryDecision.Retry(Now.AddMinutes(30)), decision);
    }

    [Fact]
    public void A_retry_after_is_capped_at_the_longest_wait()
    {
        var decision = RetryPolicy.AfterFailure(Standard, 1, AttemptOutcome.Failed, Now, TimeSpan.FromDays(30), new FixedRandom(0));

        Assert.Equal(new RetryDecision.Retry(Now.AddHours(24)), decision);
    }

    [Fact]
    public void A_short_schedule_works_the_same_way()
    {
        var demo = new RetrySchedule([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], jitter: 0);

        Assert.Equal(new RetryDecision.Retry(Now.AddSeconds(2)), RetryPolicy.AfterFailure(demo, 1, AttemptOutcome.Failed, Now, null, Random.Shared));
        Assert.Equal(new RetryDecision.Retry(Now.AddSeconds(4)), RetryPolicy.AfterFailure(demo, 2, AttemptOutcome.Failed, Now, null, Random.Shared));
        Assert.IsType<RetryDecision.Dead>(RetryPolicy.AfterFailure(demo, 3, AttemptOutcome.Failed, Now, null, Random.Shared));
    }

    [Fact]
    public void Nonsense_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.AfterFailure(Standard, 0, AttemptOutcome.Failed, Now, null, Random.Shared));
        Assert.Throws<ArgumentException>(() => RetryPolicy.AfterFailure(Standard, 1, AttemptOutcome.Delivered, Now, null, Random.Shared));
        Assert.Throws<ArgumentException>(() => new RetrySchedule([]));
        Assert.Throws<ArgumentException>(() => new RetrySchedule([TimeSpan.FromSeconds(-1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetrySchedule([TimeSpan.Zero], jitter: 2));
    }

    /// <summary>A source of randomness that always gives the same number, so jitter can be pinned.</summary>
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
