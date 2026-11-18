using System.Net;
using System.Net.Http.Headers;

using Hookline.Api.Ingest;

using Microsoft.Extensions.Time.Testing;

namespace Hookline.Api.Tests;

public sealed class SubscriptionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private HooklineApp _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _app = await postgres.NewAppAsync(new FakeTimeProvider(Http.Now));
        _client = _app.ApiClient();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task The_api_needs_the_key_and_health_doesnt()
    {
        var anonymous = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/subscriptions", TestContext.Current.CancellationToken)).StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/subscriptions", TestContext.Current.CancellationToken)).StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", HooklineApp.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/subscriptions", TestContext.Current.CancellationToken)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_new_subscription_gets_a_secret_that_is_shown_only_once()
    {
        var response = await _client.PostJsonAsync("/api/subscriptions", new { url = "https://receiver.example/hooks", eventTypes = new[] { "order.paid", "order.paid", "order.refunded" } });
        var created = await response.ReadAsync<CreatedSubscription>(HttpStatusCode.Created);

        Assert.Matches("^sub_[0-9a-f]{32}$", created.Id);
        Assert.StartsWith("whsec_", created.Secret, StringComparison.Ordinal);
        Assert.Equal(["order.paid", "order.refunded"], created.EventTypes);
        Assert.Equal($"/api/subscriptions/{created.Id}", response.Headers.Location?.ToString());

        var listed = await _client.GetStringAsync("/api/subscriptions", TestContext.Current.CancellationToken);
        var one = await _client.GetStringAsync($"/api/subscriptions/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Contains(created.Id, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", one, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_event_types_means_every_type()
    {
        var created = await (await _client.PostJsonAsync("/api/subscriptions", new { url = "http://receiver.example/all" })).ReadAsync<CreatedSubscription>(HttpStatusCode.Created);

        Assert.Empty(created.EventTypes);
    }

    [Fact]
    public async Task An_unknown_subscription_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/subscriptions/sub_nope", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://receiver.example/hooks")]
    [InlineData("javascript:alert(1)")]
    public async Task A_url_that_isnt_absolute_http_is_refused(string? url)
    {
        var response = await _client.PostJsonAsync("/api/subscriptions", new { url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"url\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blank_long_or_too_many_event_types_are_refused()
    {
        foreach (var types in new[] { [""], [" "], [new string('x', 101)], Enumerable.Range(0, 51).Select(i => $"type.{i}").ToArray() })
        {
            var response = await _client.PostJsonAsync("/api/subscriptions", new { url = "https://receiver.example/hooks", eventTypes = types });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
