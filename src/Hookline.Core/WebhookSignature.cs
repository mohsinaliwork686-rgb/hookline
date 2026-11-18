using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hookline.Core;

/// <summary>Why a request's signature didn't check out.</summary>
public enum VerifyFailure
{
    MissingHeader,
    BadTimestamp,
    TimestampOutsideTolerance,
    NoMatchingSignature,
}

/// <summary>
/// Signs and verifies requests the Standard Webhooks way. The signed content is the delivery's id, the
/// timestamp of this attempt and the exact body, joined by dots, so a captured request can't be replayed
/// later or with another body. The signature is base64 HMAC-SHA256, prefixed with its version, <c>v1,</c>.
/// </summary>
public static class WebhookSignature
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    private const string Version = "v1,";

    /// <summary>The value of the <c>webhook-signature</c> header for this request.</summary>
    public static string Sign(string secret, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var key = WebhookSecret.KeyBytes(secret);
        return Version + Convert.ToBase64String(Hash(key, id, timestamp, body));
    }

    /// <summary>
    /// Checks a received request. The timestamp must be within <paramref name="tolerance"/> of now, either
    /// way, and one of the space-separated signatures must match.
    /// </summary>
    /// <returns><c>null</c> when the request is genuine, or why it isn't.</returns>
    public static VerifyFailure? Verify(
        string secret,
        string? id,
        string? timestampHeader,
        string? signatureHeader,
        ReadOnlySpan<byte> body,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestampHeader) || string.IsNullOrEmpty(signatureHeader))
        {
            return VerifyFailure.MissingHeader;
        }

        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return VerifyFailure.BadTimestamp;
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > tolerance.TotalSeconds)
        {
            return VerifyFailure.TimestampOutsideTolerance;
        }

        var expected = Hash(WebhookSecret.KeyBytes(secret), id, timestamp, body);
        foreach (var candidate in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!candidate.StartsWith(Version, StringComparison.Ordinal))
            {
                continue;
            }

            var given = new byte[expected.Length];
            if (Convert.TryFromBase64String(candidate[Version.Length..], given, out var written)
                && written == expected.Length
                && CryptographicOperations.FixedTimeEquals(given, expected))
            {
                return null;
            }
        }

        return VerifyFailure.NoMatchingSignature;
    }

    private static byte[] Hash(byte[] key, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{id}.{timestamp}."));
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));
        return HMACSHA256.HashData(key, content);
    }
}
