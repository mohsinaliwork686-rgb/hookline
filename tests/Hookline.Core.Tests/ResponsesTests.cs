using Hookline.Core;

namespace Hookline.Core.Tests;

public class ResponsesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 13, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(299)]
    public void Only_a_2xx_is_delivered(int status) => Assert.Equal(AttemptOutcome.Delivered, Responses.Classify(status));

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void Redirects_and_errors_are_failures_to_retry(int status) => Assert.Equal(AttemptOutcome.Failed, Responses.Classify(status));

    [Fact]
    public void A_410_means_gone() => Assert.Equal(AttemptOutcome.Gone, Responses.Classify(410));

    [Fact]
    public void Retry_after_in_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), Responses.RetryAfter("120", Now));
        Assert.Equal(TimeSpan.FromSeconds(7), Responses.RetryAfter(" 7 ", Now));
    }

    [Fact]
    public void Retry_after_as_an_http_date()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Responses.RetryAfter("Wed, 18 Nov 2026 13:05:00 GMT", Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("1.5")]
    [InlineData("Wed, 18 Nov 2026 12:00:00 GMT")]
    public void Retry_after_that_is_missing_past_or_nonsense_gives_no_wait(string? header)
    {
        Assert.Null(Responses.RetryAfter(header, Now));
    }
}
