using System.Data.Common;
using System.Net;
using System.Text.Json;

using Hookline.Api.Data;
using Hookline.Api.Ingest;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Hookline.Api.Tests;

public sealed class EventTests(PostgresFixture postgres) : IAsyncLifetime
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
    public async Task The_migrations_apply_to_an_empty_database()
    {
        await using var db = _app.Db();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Events.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_event_gets_one_delivery_for_each_enabled_subscription_that_wants_it()
    {
        var paid = await SubscribeAsync("https://a.example/hooks", "order.paid");
        var everything = await SubscribeAsync("https://b.example/hooks");
        await SubscribeAsync("https://c.example/hooks", "order.refunded");
        var disabled = await SubscribeAsync("https://d.example/hooks", "order.paid");
        await using (var db = _app.Db())
        {
            await db.Subscriptions.Where(s => s.Id == disabled).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false), TestContext.Current.CancellationToken);
        }

        var response = await _client.PostJsonAsync("/api/events", new { type = "order.paid", data = new { orderId = 1042 } });
        var accepted = await response.ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted);

        Assert.Equal(2, accepted.Deliveries);
        Assert.Equal($"/api/events/{accepted.Id}", response.Headers.Location?.ToString());
        var @event = await _client.GetOkAsync<EventResponse>($"/api/events/{accepted.Id}");
        Assert.Equal(new[] { paid, everything }.Order(StringComparer.Ordinal), @event.Deliveries.Select(d => d.SubscriptionId).Order(StringComparer.Ordinal));
        Assert.All(@event.Deliveries, d =>
        {
            Assert.Equal(DeliveryStatus.Pending, d.Status);
            Assert.Equal(0, d.Attempts);
            Assert.Equal(Http.Now, d.NextAttemptAt);
        });
    }

    [Fact]
    public async Task The_saved_body_is_exactly_what_every_delivery_will_send()
    {
        var accepted = await (await _client.PostJsonAsync("/api/events", new { type = "order.paid", data = new { orderId = 1042, amount = 35.5, tags = new[] { "a" } } }))
            .ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted);

        await using var db = _app.Db();
        var body = (await db.Events.SingleAsync(e => e.Id == accepted.Id, TestContext.Current.CancellationToken)).Body;
        Assert.Equal($$$"""{"id":"{{{accepted.Id}}}","type":"order.paid","timestamp":"2026-11-18T13:00:00Z","data":{"orderId":1042,"amount":35.5,"tags":["a"]}}""", body);
    }

    [Fact]
    public async Task An_event_nobody_wants_is_still_accepted()
    {
        var accepted = await (await _client.PostJsonAsync("/api/events", new { type = "nobody.cares", data = "anything" })).ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted);

        Assert.Equal(0, accepted.Deliveries);
    }

    [Fact]
    public async Task An_event_without_a_type_or_data_is_refused()
    {
        foreach (var body in new object[] { new { data = 1 }, new { type = " ", data = 1 }, new { type = new string('x', 101), data = 1 }, new { type = "order.paid" } })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostJsonAsync("/api/events", body)).StatusCode);
        }
    }

    [Fact]
    public async Task The_same_idempotency_key_returns_the_first_event_without_new_deliveries()
    {
        await SubscribeAsync("https://a.example/hooks", "order.paid");

        var first = await (await PostWithKeyAsync("order-1042-paid", "order.paid", new { orderId = 1042 })).ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted);
        var again = await PostWithKeyAsync("order-1042-paid", "order.paid", new { orderId = 1042 });
        var second = await again.ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted);

        Assert.Equal(first, second);
        Assert.Equal("true", again.Headers.GetValues(EventEndpoints.ReplayedHeader).Single());
        await using var db = _app.Db();
        Assert.Equal(1, await db.Events.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Deliveries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_key_used_again_for_a_different_event_is_a_conflict()
    {
        await (await PostWithKeyAsync("key-1", "order.paid", new { orderId = 1 })).ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted);

        Assert.Equal(HttpStatusCode.Conflict, (await PostWithKeyAsync("key-1", "order.paid", new { orderId = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostWithKeyAsync("key-1", "order.refunded", new { orderId = 1 })).StatusCode);
    }

    [Fact]
    public async Task An_idempotency_key_over_255_characters_is_refused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWithKeyAsync(new string('k', 256), "order.paid", 1)).StatusCode);
    }

    [Fact]
    public async Task Many_requests_with_one_key_at_once_make_one_event()
    {
        await SubscribeAsync("https://a.example/hooks", "order.paid");

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostWithKeyAsync("same-key", "order.paid", new { orderId = 7 })));

        var accepted = await Task.WhenAll(responses.Select(r => r.ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted)));
        Assert.Single(accepted.Select(a => a.Id).Distinct());
        await using var db = _app.Db();
        Assert.Equal(1, await db.Events.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Deliveries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task If_the_deliveries_cant_be_saved_the_event_isnt_saved_either()
    {
        await using var app = await postgres.NewAppAsync(new FakeTimeProvider(Http.Now), s => s.AddSingleton<IInterceptor>(new FailInsertsInto("deliveries")));
        var client = app.ApiClient();
        await (await client.PostJsonAsync("/api/subscriptions", new { url = "https://a.example/hooks" })).ReadAsync<CreatedSubscription>(HttpStatusCode.Created);

        var response = await client.PostJsonAsync("/api/events", new { type = "order.paid", data = 1 });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var db = app.Db();
        Assert.Equal(0, await db.Events.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_event_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/events/evt_nope", TestContext.Current.CancellationToken)).StatusCode);
    }

    private async Task<string> SubscribeAsync(string url, params string[] eventTypes) =>
        (await (await _client.PostJsonAsync("/api/subscriptions", new { url, eventTypes })).ReadAsync<CreatedSubscription>(HttpStatusCode.Created)).Id;

    private Task<HttpResponseMessage> PostWithKeyAsync(string key, string type, object data)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = JsonContent(new { type, data }),
        };
        request.Headers.Add(EventEndpoints.IdempotencyHeader, key);
        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, HooklineApp.Json), System.Text.Encoding.UTF8, "application/json");

    /// <summary>Makes any command that inserts into the named table fail, as a broken database would.</summary>
    private sealed class FailInsertsInto(string table) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Fail(DbCommand command)
        {
            if (command.CommandText.Contains($"INSERT INTO {table}", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Inserts into {table} fail in this test.");
            }
        }
    }
}
