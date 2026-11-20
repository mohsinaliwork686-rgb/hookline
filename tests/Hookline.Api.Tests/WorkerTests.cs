using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Hookline.Api.Data;
using Hookline.Api.Ingest;
using Hookline.Api.Worker;
using Hookline.Core;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Npgsql;

namespace Hookline.Api.Tests;

/// <summary>
/// The delivery worker against real PostgreSQL and a real HTTP receiver. Passes are run one at a time on a
/// fake clock, so the retry schedule is checked without waiting for it.
/// </summary>
public sealed class WorkerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(Http.Now);
    private Receiver _receiver = null!;
    private HooklineApp? _app;
    private HttpClient _client = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private HooklineApp App => _app ?? throw new InvalidOperationException("Call StartAsync first.");

    public async ValueTask InitializeAsync() => _receiver = await Receiver.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _receiver.DisposeAsync();
    }

    [Fact]
    public async Task A_delivery_is_signed_sent_with_the_saved_body_and_never_sent_again()
    {
        await StartAsync();
        _receiver.Answer("/ok", 200, "thanks");
        var subscription = await SubscribeAsync("/ok");
        var eventId = await SendEventAsync();

        Assert.Equal(1, await PassAsync());

        var request = Assert.Single(_receiver.To("/ok"));
        string saved;
        await using (var db = App.Db())
        {
            saved = (await db.Events.SingleAsync(Cancel)).Body;
        }

        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json; charset=utf-8", request.Headers["Content-Type"]);
        Assert.Equal(saved, Encoding.UTF8.GetString(request.Body));
        Assert.Equal(eventId, request.Headers[WebhookSignature.IdHeader]);
        Assert.Equal(Http.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), request.Headers[WebhookSignature.TimestampHeader]);
        Assert.Null(Verify(subscription.Secret, request, Http.Now));

        var delivery = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Delivered, 1, Http.Now), (delivery.Status, delivery.Attempts, delivery.DeliveredAt));
        Assert.Null(delivery.LockedUntil);
        Assert.Null(delivery.LeaseId);
        Assert.Null(delivery.LastError);
        var attempt = Assert.Single(delivery.AttemptLog);
        Assert.Equal((1, 200, "thanks", Http.Now), (attempt.Number, attempt.StatusCode, attempt.ResponseExcerpt, attempt.StartedAt));

        _clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(0, await PassAsync());
        Assert.Single(_receiver.Requests);
    }

    [Fact]
    public async Task A_receiver_that_fails_twice_gets_it_on_the_third_attempt_on_schedule()
    {
        await StartAsync();
        _receiver.FailThenSucceed("/flaky", failures: 2);
        var subscription = await SubscribeAsync("/flaky");
        await SendEventAsync();

        Assert.Equal(1, await PassAsync());
        var first = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Pending, 1, "Answered 500."), (first.Status, first.Attempts, first.LastError));
        Assert.InRange(first.NextAttemptAt, Http.Now + TimeSpan.FromSeconds(5), Http.Now + TimeSpan.FromSeconds(5.5));

        // Not due yet, so a pass takes nothing.
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, await PassAsync());

        _clock.SetUtcNow(first.NextAttemptAt);
        Assert.Equal(1, await PassAsync());
        var second = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Pending, 2), (second.Status, second.Attempts));
        Assert.InRange(second.NextAttemptAt, first.NextAttemptAt + TimeSpan.FromMinutes(5), first.NextAttemptAt + TimeSpan.FromMinutes(5.5));

        _clock.SetUtcNow(second.NextAttemptAt);
        Assert.Equal(1, await PassAsync());
        var third = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Delivered, 3, second.NextAttemptAt), (third.Status, third.Attempts, third.DeliveredAt));
        Assert.Equal(new int?[] { 500, 500, 200 }, third.AttemptLog.Select(a => a.StatusCode));

        // The same id on every attempt, so the receiver can drop repeats, and a fresh timestamp each time,
        // each with a signature that checks out at the moment it was sent.
        var requests = _receiver.To("/flaky");
        Assert.Equal(3, requests.Count);
        Assert.Single(requests.Select(r => r.Headers[WebhookSignature.IdHeader]).Distinct());
        Assert.Equal(3, requests.Select(r => r.Headers[WebhookSignature.TimestampHeader]).Distinct().Count());
        Assert.All(requests, r => Assert.Null(Verify(subscription.Secret, r, SentAt(r))));
    }

    [Fact]
    public async Task After_the_last_attempt_the_delivery_is_dead_and_stays_dead()
    {
        await StartAsync(o =>
        {
            o.Schedule = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)];
            o.Jitter = 0;
        });
        _receiver.Answer("/down", 500);
        await SubscribeAsync("/down");
        await SendEventAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(1, await PassAsync());
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var delivery = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Dead, 3), (delivery.Status, delivery.Attempts));
        Assert.Equal("Answered 500. No attempts left after 3.", delivery.LastError);

        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(0, await PassAsync());
        Assert.Equal(3, _receiver.To("/down").Count);
    }

    [Fact]
    public async Task A_redirect_is_a_failed_attempt_and_isnt_followed()
    {
        await StartAsync();
        _receiver.Answer("/ok", 200);
        _receiver.Answer("/moved", 307, null, ("Location", _receiver.Url("/ok")));
        await SubscribeAsync("/moved");
        await SendEventAsync();

        await PassAsync();

        var delivery = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Pending, "Answered 307. Redirects aren't followed."), (delivery.Status, delivery.LastError));
        Assert.Equal(307, Assert.Single(delivery.AttemptLog).StatusCode);
        Assert.Empty(_receiver.To("/ok"));
    }

    [Fact]
    public async Task Gone_ends_the_delivery_at_once_and_disables_the_subscription()
    {
        await StartAsync(o => o.BatchSize = 1);
        _receiver.Answer("/gone", 410);
        await SubscribeAsync("/gone");
        await SendEventAsync(new { orderId = 1 });
        await SendEventAsync(new { orderId = 2 });

        Assert.Equal(1, await PassAsync());

        Assert.Single(_receiver.To("/gone"));
        await using (var db = App.Db())
        {
            var subscription = await db.Subscriptions.SingleAsync(Cancel);
            Assert.Equal((false, "The receiver answered 410 Gone."), (subscription.Enabled, subscription.DisabledReason));

            var deliveries = await db.Deliveries.Include(d => d.AttemptLog).ToListAsync(Cancel);
            Assert.All(deliveries, d => Assert.Equal(DeliveryStatus.Dead, d.Status));
            var sent = Assert.Single(deliveries, d => d.Attempts == 1);
            Assert.Equal("Answered 410 Gone, so the subscription is disabled.", sent.LastError);
            Assert.Equal(410, Assert.Single(sent.AttemptLog).StatusCode);

            // The other event's delivery was waiting for the same URL, so it ends without being sent.
            var waiting = Assert.Single(deliveries, d => d.Attempts == 0);
            Assert.Equal(DeliveryStore.NotSentDisabled, waiting.LastError);
            Assert.Empty(waiting.AttemptLog);
        }

        var later = await (await _client.PostJsonAsync("/api/events", new { type = "order.paid", data = 3 })).ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted);
        Assert.Equal(0, later.Deliveries);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await PassAsync());
        Assert.Single(_receiver.To("/gone"));
    }

    [Fact]
    public async Task A_delivery_whose_subscription_was_disabled_ends_without_being_sent()
    {
        await StartAsync();
        _receiver.Answer("/ok", 200);
        await SubscribeAsync("/ok");
        await SendEventAsync();
        await using (var db = App.Db())
        {
            await db.Subscriptions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false), Cancel);
        }

        Assert.Equal(1, await PassAsync());

        Assert.Empty(_receiver.Requests);
        var delivery = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Dead, 0, DeliveryStore.NotSentDisabled), (delivery.Status, delivery.Attempts, delivery.LastError));
        Assert.Empty(delivery.AttemptLog);
    }

    [Fact]
    public async Task Retry_after_is_honoured_on_429_and_503_up_to_the_longest_wait()
    {
        await StartAsync();
        _receiver.Answer("/busy", 429, null, ("Retry-After", "120"));
        _receiver.Answer("/unavailable", 503, null, ("Retry-After", "31536000"));
        _receiver.Answer("/error", 500, null, ("Retry-After", "120"));
        var busy = await SubscribeAsync("/busy");
        var unavailable = await SubscribeAsync("/unavailable");
        var error = await SubscribeAsync("/error");
        await SendEventAsync();

        Assert.Equal(3, await PassAsync());

        await using var db = App.Db();
        var next = await db.Deliveries.ToDictionaryAsync(d => d.SubscriptionId, d => d.NextAttemptAt, Cancel);
        Assert.Equal(Http.Now + TimeSpan.FromMinutes(2), next[busy.Id]);
        Assert.Equal(Http.Now + TimeSpan.FromHours(24), next[unavailable.Id]);
        Assert.InRange(next[error.Id], Http.Now + TimeSpan.FromSeconds(5), Http.Now + TimeSpan.FromSeconds(5.5));
    }

    [Fact]
    public async Task A_slow_receiver_doesnt_hold_up_the_rest_of_the_batch()
    {
        await StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiver.Hold("/slow", release.Task);
        _receiver.Answer("/ok", 200);
        var slow = await SubscribeAsync("/slow", "order.paid");
        var ok = await SubscribeAsync("/ok", "order.refunded");

        // The slow receiver's delivery is due first, so it is sent first.
        await SendEventAsync(type: "order.paid");
        _clock.Advance(TimeSpan.FromSeconds(1));
        await SendEventAsync(type: "order.refunded");

        var pass = PassAsync();
        try
        {
            // The quick receiver's result is saved while the slow one still has its request open.
            await WaitUntilAsync(async () => await StatusAsync(ok.Id) == DeliveryStatus.Delivered);
            Assert.False(pass.IsCompleted);
            Assert.Equal(DeliveryStatus.Pending, await StatusAsync(slow.Id));
        }
        finally
        {
            release.SetResult();
        }

        Assert.Equal(2, await pass);
        Assert.Equal(DeliveryStatus.Delivered, await StatusAsync(slow.Id));
    }

    [Fact]
    public async Task An_attempt_that_outlasts_the_timeout_fails_and_is_tried_again()
    {
        await StartAsync(o => o.Timeout = TimeSpan.FromSeconds(1));
        _receiver.Hold("/hang", new TaskCompletionSource().Task);
        await SubscribeAsync("/hang");
        await SendEventAsync();

        await PassAsync();

        var delivery = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Pending, "Timed out after 1 s."), (delivery.Status, delivery.LastError));
        var attempt = Assert.Single(delivery.AttemptLog);
        Assert.Null(attempt.StatusCode);
        Assert.Equal("Timed out after 1 s.", attempt.Error);
        Assert.InRange(attempt.DurationMs, 900, 10_000);
    }

    [Fact]
    public async Task A_receiver_that_isnt_listening_is_a_failed_attempt()
    {
        await StartAsync();
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }

        await (await _client.PostJsonAsync("/api/subscriptions", new { url = $"http://127.0.0.1:{port}/hooks" })).ReadAsync<CreatedSubscription>(HttpStatusCode.Created);
        await SendEventAsync();

        await PassAsync();

        var delivery = await OnlyDeliveryAsync();
        var attempt = Assert.Single(delivery.AttemptLog);
        Assert.Null(attempt.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(attempt.Error));
        Assert.Equal((DeliveryStatus.Pending, attempt.Error), (delivery.Status, delivery.LastError));
    }

    [Fact]
    public async Task Only_the_start_of_the_answer_is_kept_and_it_is_safe_to_store()
    {
        await StartAsync();
        _receiver.Answer("/chatty", 200, "ok\0" + new string('x', 10_000));
        await SubscribeAsync("/chatty");
        await SendEventAsync();

        await PassAsync();

        var attempt = Assert.Single((await OnlyDeliveryAsync()).AttemptLog);
        Assert.Equal(1000, attempt.ResponseExcerpt?.Length);
        Assert.StartsWith("ok�xxx", attempt.ResponseExcerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delivery_held_by_a_worker_that_died_is_sent_once_its_lease_runs_out()
    {
        await StartAsync();
        _receiver.Answer("/ok", 200);
        await SubscribeAsync("/ok");
        await SendEventAsync();

        // A worker takes the delivery, then dies before sending it.
        await using var scope = App.Services.CreateAsyncScope();
        var firstWorker = scope.ServiceProvider.GetRequiredService<DeliveryStore>();
        var held = Assert.Single(await firstWorker.ClaimAsync(10, Cancel));

        // Nobody else takes it while the lease lasts, and someone does as soon as it runs out.
        _clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await PassAsync());
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await PassAsync());
        Assert.Single(_receiver.Requests);

        // If the first worker turns up with a result after all, it is dropped and the newer result stands.
        var late = new AttemptResult(Http.Now, 10, 500, null, null, null);
        Assert.False(await firstWorker.RecordAsync(held, late, Cancel));
        var delivery = await OnlyDeliveryAsync();
        Assert.Equal((DeliveryStatus.Delivered, 1), (delivery.Status, delivery.Attempts));
        Assert.Equal(200, Assert.Single(delivery.AttemptLog).StatusCode);
    }

    [Fact]
    public async Task A_delivery_another_worker_is_taking_at_this_instant_is_skipped_not_waited_for()
    {
        await StartAsync();
        _receiver.Answer("/ok", 200);
        await SubscribeAsync("/ok");
        var taken = await SendEventAsync(new { orderId = 1 });
        var free = await SendEventAsync(new { orderId = 2 });

        // Another worker is in the middle of its claim: it has one row locked and hasn't committed yet.
        await using var other = new NpgsqlConnection(App.ConnectionString);
        await other.OpenAsync(Cancel);
        await using var claim = await other.BeginTransactionAsync(Cancel);
        await using (var lockRow = new NpgsqlCommand("SELECT id FROM deliveries WHERE event_id = @event FOR UPDATE", other, claim))
        {
            lockRow.Parameters.AddWithValue("event", taken);
            await lockRow.ExecuteScalarAsync(Cancel);
        }

        Assert.Equal(1, await PassAsync().WaitAsync(TimeSpan.FromSeconds(10), Cancel));
        Assert.Equal(free, Assert.Single(_receiver.Requests).Headers[WebhookSignature.IdHeader]);

        await claim.RollbackAsync(Cancel);
    }

    [Fact]
    public async Task Several_workers_on_one_database_send_each_delivery_once()
    {
        static void Small(DeliveryOptions o)
        {
            o.BatchSize = 5;
            o.Parallelism = 5;
        }

        await StartAsync(Small);
        _receiver.On("/ok", async context =>
        {
            await Task.Delay(10, context.RequestAborted);
            context.Response.StatusCode = 200;
        });
        await SubscribeAsync("/ok");
        var events = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            events.Add(await SendEventAsync(new { orderId = i }));
        }

        // Two more apps on the same database: separate processes, as far as the database can tell.
        await using var second = new HooklineApp(App.ConnectionString, _clock, s => s.Configure<DeliveryOptions>(Small));
        await using var third = new HooklineApp(App.ConnectionString, _clock, s => s.Configure<DeliveryOptions>(Small));
        HooklineApp[] workers = [App, second, third];
        await Task.WhenAll(workers.Select(app => Task.Run(async () =>
        {
            while (await PassAsync(app) > 0)
            {
            }
        })));

        await using var db = App.Db();
        Assert.Equal(60, await db.Deliveries.CountAsync(d => d.Status == DeliveryStatus.Delivered && d.Attempts == 1, Cancel));
        Assert.Equal(60, await db.DeliveryAttempts.CountAsync(Cancel));
        var sent = _receiver.To("/ok").Select(r => r.Headers[WebhookSignature.IdHeader]);
        Assert.Equal(events.Order(StringComparer.Ordinal), sent.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_background_worker_delivers_on_its_own()
    {
        await StartAsync(o => o.Enabled = true);
        _receiver.Answer("/ok", 200);
        await SubscribeAsync("/ok");
        await SendEventAsync();

        // When nothing is due the worker rests on the app's clock, so moving the clock on wakes it.
        await WaitUntilAsync(async () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            return (await OnlyDeliveryAsync()).Status == DeliveryStatus.Delivered;
        });
        Assert.Single(_receiver.Requests);
    }

    [Fact]
    public async Task The_background_worker_keeps_sending_while_one_receiver_hangs()
    {
        await StartAsync(o => o.Enabled = true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiver.Hold("/slow", release.Task);
        _receiver.Answer("/ok", 200);
        var slow = await SubscribeAsync("/slow", "order.paid");
        var ok = await SubscribeAsync("/ok", "order.refunded");

        try
        {
            await SendEventAsync(type: "order.paid");
            await WaitUntilAsync(() =>
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                return Task.FromResult(_receiver.To("/slow").Count == 1);
            });

            // This one comes due while the slow request is still open, and goes out without waiting for it.
            await SendEventAsync(type: "order.refunded");
            await WaitUntilAsync(async () =>
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                return await StatusAsync(ok.Id) == DeliveryStatus.Delivered;
            });
            Assert.Equal(DeliveryStatus.Pending, await StatusAsync(slow.Id));
        }
        finally
        {
            release.SetResult();
        }

        await WaitUntilAsync(async () => await StatusAsync(slow.Id) == DeliveryStatus.Delivered);
    }

    [Fact]
    public async Task A_lease_no_longer_than_the_timeout_is_refused_at_startup()
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
            postgres.NewAppAsync(_clock, s => s.Configure<DeliveryOptions>(o => o.Lease = o.Timeout)));

        Assert.Contains("Delivery:Lease must be longer than Delivery:Timeout", refused.ToString(), StringComparison.Ordinal);
    }

    private async Task StartAsync(Action<DeliveryOptions>? configure = null)
    {
        _app = await postgres.NewAppAsync(_clock, s => s.Configure<DeliveryOptions>(o => configure?.Invoke(o)));
        _client = _app.ApiClient();
    }

    private Task<int> PassAsync(HooklineApp? app = null) =>
        (app ?? App).Services.GetRequiredService<DeliveryWorker>().RunOnceAsync(Cancel);

    private async Task<CreatedSubscription> SubscribeAsync(string path, params string[] eventTypes) =>
        await (await _client.PostJsonAsync("/api/subscriptions", new { url = _receiver.Url(path), eventTypes })).ReadAsync<CreatedSubscription>(HttpStatusCode.Created);

    private async Task<string> SendEventAsync(object? data = null, string type = "order.paid") =>
        (await (await _client.PostJsonAsync("/api/events", new { type, data = data ?? new { orderId = 1042 } }))
            .ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted)).Id;

    private async Task<Delivery> OnlyDeliveryAsync()
    {
        await using var db = App.Db();
        return await db.Deliveries.Include(d => d.AttemptLog.OrderBy(a => a.Number)).SingleAsync(Cancel);
    }

    private async Task<DeliveryStatus> StatusAsync(string subscriptionId)
    {
        await using var db = App.Db();
        return await db.Deliveries.Where(d => d.SubscriptionId == subscriptionId).Select(d => d.Status).SingleAsync(Cancel);
    }

    private static VerifyFailure? Verify(string secret, ReceivedRequest request, DateTimeOffset now) =>
        WebhookSignature.Verify(
            secret,
            request.Headers[WebhookSignature.IdHeader],
            request.Headers[WebhookSignature.TimestampHeader],
            request.Headers[WebhookSignature.SignatureHeader],
            request.Body,
            now,
            TimeSpan.FromMinutes(5));

    private static DateTimeOffset SentAt(ReceivedRequest request) =>
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(request.Headers[WebhookSignature.TimestampHeader], CultureInfo.InvariantCulture));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var waiting = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(waiting.Elapsed < TimeSpan.FromSeconds(15), "The condition didn't come true within 15 seconds.");
            await Task.Delay(50, Cancel);
        }
    }
}
