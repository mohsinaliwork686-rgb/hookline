using System.Security.Cryptography;

namespace Hookline.Core;

/// <summary>
/// Ids like <c>evt_3f9c…</c>: a short prefix saying what it is, then 128 random bits in hex. Safe to show,
/// and impossible to guess or count through.
/// </summary>
public static class Ids
{
    public const string Event = "evt";
    public const string Subscription = "sub";
    public const string Delivery = "dlv";

    public static string New(string prefix) =>
        prefix + "_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
