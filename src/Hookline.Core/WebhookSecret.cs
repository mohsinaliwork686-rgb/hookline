using System.Security.Cryptography;

namespace Hookline.Core;

/// <summary>
/// A subscription's signing secret: <c>whsec_</c> and the key in base64, as Standard Webhooks writes it.
/// </summary>
public static class WebhookSecret
{
    private const string Prefix = "whsec_";
    private const int KeyLength = 32;

    public static string Generate() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyLength));

    /// <summary>The HMAC key: the base64 after the prefix, decoded. The prefix itself is optional.</summary>
    public static byte[] KeyBytes(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var encoded = secret.StartsWith(Prefix, StringComparison.Ordinal) ? secret[Prefix.Length..] : secret;
        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException e)
        {
            throw new ArgumentException("A webhook secret is base64, optionally after whsec_.", nameof(secret), e);
        }
    }
}
