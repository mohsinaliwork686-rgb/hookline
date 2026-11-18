using System.Text;

using Hookline.Core;

namespace Hookline.Core.Tests;

public class WebhookSignatureTests
{
    // The fixed example from the Standard Webhooks reference library's tests (Python, test_sign_function).
    private const string ReferenceSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string ReferenceId = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const long ReferenceTimestamp = 1614265330;

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(ReferenceTimestamp);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly byte[] ReferenceBody = Encoding.UTF8.GetBytes("""{"test": 2432232314}""");

    [Fact]
    public void Signs_the_reference_example_exactly_as_the_spec_libraries_do()
    {
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", WebhookSignature.Sign(ReferenceSecret, ReferenceId, ReferenceTimestamp, ReferenceBody));
    }

    [Fact]
    public void Signs_a_binary_body_byte_for_byte()
    {
        var body = Enumerable.Range(0, 256).Select(b => (byte)b).ToArray();

        Assert.Equal("v1,sEcZ042G8Crqgxbybb3y0eDxQR3TcfYsk11I1XULhSI=", WebhookSignature.Sign(ReferenceSecret, ReferenceId, ReferenceTimestamp, body));
    }

    [Fact]
    public void The_secret_works_with_or_without_its_prefix()
    {
        var withPrefix = WebhookSignature.Sign(ReferenceSecret, ReferenceId, ReferenceTimestamp, ReferenceBody);
        var without = WebhookSignature.Sign(ReferenceSecret["whsec_".Length..], ReferenceId, ReferenceTimestamp, ReferenceBody);

        Assert.Equal(withPrefix, without);
    }

    [Fact]
    public void A_genuine_request_verifies()
    {
        Assert.Null(Verify(Signature(), ReferenceBody));
    }

    [Fact]
    public void A_changed_body_fails()
    {
        Assert.Equal(VerifyFailure.NoMatchingSignature, Verify(Signature(), Encoding.UTF8.GetBytes("""{"test": 2432232315}""")));
    }

    [Fact]
    public void Another_secret_fails()
    {
        var other = WebhookSecret.Generate();

        Assert.Equal(VerifyFailure.NoMatchingSignature, WebhookSignature.Verify(other, ReferenceId, $"{ReferenceTimestamp}", Signature(), ReferenceBody, Now, Tolerance));
    }

    [Fact]
    public void The_id_and_timestamp_are_part_of_what_is_signed()
    {
        var signature = Signature();

        Assert.Equal(VerifyFailure.NoMatchingSignature, WebhookSignature.Verify(ReferenceSecret, "msg_other", $"{ReferenceTimestamp}", signature, ReferenceBody, Now, Tolerance));
        Assert.Equal(VerifyFailure.NoMatchingSignature, WebhookSignature.Verify(ReferenceSecret, ReferenceId, $"{ReferenceTimestamp + 1}", signature, ReferenceBody, Now, Tolerance));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void A_timestamp_more_than_five_minutes_off_fails_even_with_a_valid_signature(int offsetSeconds)
    {
        var failure = WebhookSignature.Verify(ReferenceSecret, ReferenceId, $"{ReferenceTimestamp}", Signature(), ReferenceBody, Now.AddSeconds(offsetSeconds), Tolerance);

        Assert.Equal(VerifyFailure.TimestampOutsideTolerance, failure);
    }

    [Fact]
    public void A_timestamp_exactly_at_the_tolerance_still_verifies()
    {
        Assert.Null(WebhookSignature.Verify(ReferenceSecret, ReferenceId, $"{ReferenceTimestamp}", Signature(), ReferenceBody, Now.AddMinutes(5), Tolerance));
    }

    [Fact]
    public void One_matching_signature_among_several_is_enough()
    {
        var header = $"v1,bm90IHRoZSByaWdodCBvbmU= v2a,somethingelse {Signature()}";

        Assert.Null(Verify(header, ReferenceBody));
    }

    [Theory]
    [InlineData(null, "1614265330", "v1,x")]
    [InlineData("msg_1", null, "v1,x")]
    [InlineData("msg_1", "1614265330", null)]
    [InlineData("", "1614265330", "v1,x")]
    public void Missing_headers_fail(string? id, string? timestamp, string? signature)
    {
        Assert.Equal(VerifyFailure.MissingHeader, WebhookSignature.Verify(ReferenceSecret, id, timestamp, signature, ReferenceBody, Now, Tolerance));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-1614265330")]
    [InlineData("1614265330.5")]
    public void A_timestamp_that_isnt_whole_seconds_fails(string timestamp)
    {
        Assert.Equal(VerifyFailure.BadTimestamp, WebhookSignature.Verify(ReferenceSecret, ReferenceId, timestamp, Signature(), ReferenceBody, Now, Tolerance));
    }

    [Theory]
    [InlineData("g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")]
    [InlineData("v1,not base64!")]
    [InlineData("v1,")]
    public void A_malformed_signature_fails_without_throwing(string header)
    {
        Assert.Equal(VerifyFailure.NoMatchingSignature, Verify(header, ReferenceBody));
    }

    [Fact]
    public void A_generated_secret_has_the_prefix_and_a_32_byte_key()
    {
        var secret = WebhookSecret.Generate();

        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
        Assert.Equal(32, WebhookSecret.KeyBytes(secret).Length);
        Assert.NotEqual(secret, WebhookSecret.Generate());
    }

    [Fact]
    public void A_secret_that_isnt_base64_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WebhookSecret.KeyBytes("whsec_not base64!"));
    }

    private static string Signature() => WebhookSignature.Sign(ReferenceSecret, ReferenceId, ReferenceTimestamp, ReferenceBody);

    private static VerifyFailure? Verify(string signatureHeader, byte[] body) =>
        WebhookSignature.Verify(ReferenceSecret, ReferenceId, $"{ReferenceTimestamp}", signatureHeader, body, Now, Tolerance);
}
