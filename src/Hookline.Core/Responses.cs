using System.Globalization;

namespace Hookline.Core;

/// <summary>How one attempt went, as far as delivery is concerned.</summary>
public enum AttemptOutcome
{
    /// <summary>A 2xx answer within the timeout.</summary>
    Delivered,

    /// <summary>Anything that should be tried again: other status codes, timeouts, refused connections.</summary>
    Failed,

    /// <summary>410 Gone: the receiver says the URL is gone for good.</summary>
    Gone,
}

public static class Responses
{
    /// <summary>
    /// Only a 2xx counts as delivered. Redirects are failures: a signed request isn't forwarded somewhere the
    /// subscriber didn't register. Every other failure is retried, except 410, because a sender can't tell a
    /// temporary 400 from a permanent one.
    /// </summary>
    public static AttemptOutcome Classify(int statusCode) => statusCode switch
    {
        >= 200 and <= 299 => AttemptOutcome.Delivered,
        410 => AttemptOutcome.Gone,
        _ => AttemptOutcome.Failed,
    };

    /// <summary>
    /// Reads a Retry-After header, which is either a number of seconds or an HTTP date. Anything else, or a
    /// time already past, gives no wait.
    /// </summary>
    public static TimeSpan? RetryAfter(string? header, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var value = header.Trim();
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, (long)TimeSpan.MaxValue.TotalSeconds)) : null;
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - now;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }
}
