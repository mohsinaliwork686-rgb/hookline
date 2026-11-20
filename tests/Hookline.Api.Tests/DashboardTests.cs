using System.Net;
using System.Text.RegularExpressions;

using Hookline.Api.Dashboard;
using Hookline.Api.Data;
using Hookline.Api.Ingest;
using Hookline.Api.Worker;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Hookline.Api.Tests;

/// <summary>The dashboard through its pages, as a browser uses it: the login form, the lists and Send again.</summary>
public sealed partial class DashboardTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(Http.Now);
    private Receiver _receiver = null!;
    private HooklineApp _app = null!;
    private HttpClient _api = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _receiver = await Receiver.StartAsync();

        // Two attempts, a second apart, so a delivery dies quickly.
        _app = await postgres.NewAppAsync(_clock, s => s.Configure<DeliveryOptions>(o =>
        {
            o.Schedule = [TimeSpan.Zero, TimeSpan.FromSeconds(1)];
            o.Jitter = 0;
        }));
        _api = _app.ApiClient();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    [Fact]
    public async Task The_dashboard_needs_the_admin_password()
    {
        var browser = Browser();

        Assert.Equal("/login?ReturnUrl=%2F", await RedirectAsync(browser, "/"));
        Assert.StartsWith("/login", await RedirectAsync(browser, "/deliveries/dlv_anything"), StringComparison.Ordinal);

        var wrong = await SignInAsync(browser, "not-it");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("That isn&#x27;t the admin password.", await wrong.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.StartsWith("/login", await RedirectAsync(browser, "/"), StringComparison.Ordinal);

        // Somewhere else to go after signing in is only followed if it is on this site.
        Assert.Equal("/", (await SignInAsync(browser, HooklineApp.AdminPassword, "https://elsewhere.example/")).Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/", Cancel)).StatusCode);

        var signOut = await PostFormAsync(browser, "/", "/logout", []);
        Assert.Equal("/login", signOut.Headers.Location?.ToString());
        Assert.StartsWith("/login", await RedirectAsync(browser, "/"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_admin_password_the_dashboard_stays_shut()
    {
        await using var app = await postgres.NewAppAsync(_clock, s => s.Configure<DashboardOptions>(o => o.AdminPassword = null));
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var login = await browser.GetStringAsync("/login", Cancel);
        Assert.Contains("The dashboard is off.", login, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", login, StringComparison.Ordinal);
        Assert.StartsWith("/login", await RedirectAsync(browser, "/"), StringComparison.Ordinal);

        var shut = new DashboardOptions();
        Assert.False(shut.Matches(""));
        Assert.False(shut.Matches(null));
        Assert.False(new DashboardOptions { AdminPassword = "secret" }.Matches("Secret"));
        Assert.True(new DashboardOptions { AdminPassword = "secret" }.Matches("secret"));
    }

    [Fact]
    public async Task The_lists_count_and_filter_by_status_and_a_delivery_shows_its_attempts()
    {
        _receiver.Answer("/ok", 200, "thanks");
        _receiver.Answer("/down", 500, "broken");
        var ok = await SubscribeAsync("/ok");
        var down = await SubscribeAsync("/down");
        await SendEventAsync();
        await PassAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        await PassAsync();

        var browser = await SignedInAsync();
        var all = await browser.GetStringAsync("/", Cancel);
        Assert.Contains("All<span>2</span>", all, StringComparison.Ordinal);
        Assert.Contains("Pending<span>0</span>", all, StringComparison.Ordinal);
        Assert.Contains("Delivered<span>1</span>", all, StringComparison.Ordinal);
        Assert.Contains("Dead<span>1</span>", all, StringComparison.Ordinal);

        var dead = await browser.GetStringAsync("/?status=dead", Cancel);
        Assert.Contains(_receiver.Url("/down"), dead, StringComparison.Ordinal);
        Assert.DoesNotContain(_receiver.Url("/ok"), dead, StringComparison.Ordinal);
        Assert.Contains("Send again", dead, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/?status=lost", Cancel)).StatusCode);

        var detail = await browser.GetStringAsync($"/deliveries/{await DeliveryIdAsync(down.Id)}", Cancel);
        Assert.Contains("Answered 500. No attempts left after 2.", detail, StringComparison.Ordinal);
        Assert.Equal(2, AttemptRow().Matches(detail).Count);
        Assert.Contains("broken", detail, StringComparison.Ordinal);
        Assert.Contains("&quot;orderId&quot;: 1042", detail, StringComparison.Ordinal);

        var delivered = await browser.GetStringAsync($"/deliveries/{await DeliveryIdAsync(ok.Id)}", Cancel);
        Assert.DoesNotContain("Send again", delivered, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/deliveries/dlv_nope", Cancel)).StatusCode);
    }

    [Fact]
    public async Task Send_again_puts_a_dead_delivery_back_and_the_next_pass_delivers_it()
    {
        _receiver.FailThenSucceed("/flaky", failures: 2);
        var flaky = await SubscribeAsync("/flaky");
        await SendEventAsync();
        await PassAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        await PassAsync();
        var id = await DeliveryIdAsync(flaky.Id);
        Assert.Equal(DeliveryStatus.Dead, (await DeliveryAsync(id)).Status);

        _clock.Advance(TimeSpan.FromHours(1));
        var browser = await SignedInAsync();
        var sent = await PostFormAsync(browser, $"/deliveries/{id}", $"/deliveries/{id}?handler=SendAgain", []);
        Assert.Equal($"/deliveries/{id}", sent.Headers.Location?.ToString());
        Assert.Contains("Back in the queue, due now.", await browser.GetStringAsync($"/deliveries/{id}", Cancel), StringComparison.Ordinal);

        var queued = await DeliveryAsync(id);
        Assert.Equal((DeliveryStatus.Pending, 2, _clock.GetUtcNow()), (queued.Status, queued.Attempts, queued.NextAttemptAt));

        Assert.Equal(1, await PassAsync());
        var delivered = await DeliveryAsync(id);
        Assert.Equal((DeliveryStatus.Delivered, 3), (delivered.Status, delivered.Attempts));

        // Once delivered, it can't be sent again.
        await PostFormAsync(browser, "/?status=dead", "/?handler=SendAgain", new() { ["id"] = id });
        Assert.Contains("Only a dead delivery can be sent again.", await browser.GetStringAsync("/", Cancel), StringComparison.Ordinal);
        Assert.Equal((DeliveryStatus.Delivered, 3), ((await DeliveryAsync(id)).Status, (await DeliveryAsync(id)).Attempts));
        Assert.Equal(3, _receiver.To("/flaky").Count);
    }

    [Fact]
    public async Task A_sent_again_delivery_that_fails_is_dead_after_one_more_attempt()
    {
        _receiver.Answer("/down", 500);
        var down = await SubscribeAsync("/down");
        await SendEventAsync();
        await PassAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        await PassAsync();
        var id = await DeliveryIdAsync(down.Id);

        Assert.Equal(SendAgainResult.Queued, await SendAgainAsync(id));
        Assert.Equal(1, await PassAsync());

        var delivery = await DeliveryAsync(id);
        Assert.Equal((DeliveryStatus.Dead, 3), (delivery.Status, delivery.Attempts));
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await PassAsync());
        Assert.Equal(3, _receiver.To("/down").Count);
    }

    [Fact]
    public async Task Send_again_is_refused_while_the_subscription_is_disabled()
    {
        _receiver.Answer("/gone", 410);
        var gone = await SubscribeAsync("/gone");
        await SendEventAsync();
        await PassAsync();
        var id = await DeliveryIdAsync(gone.Id);

        Assert.Equal(SendAgainResult.SubscriptionDisabled, await SendAgainAsync(id));
        Assert.Equal(SendAgainResult.NotFound, await SendAgainAsync("dlv_nope"));
        Assert.Equal(DeliveryStatus.Dead, (await DeliveryAsync(id)).Status);

        var detail = await (await SignedInAsync()).GetStringAsync($"/deliveries/{id}", Cancel);
        Assert.Contains("subscription disabled: The receiver answered 410 Gone.", detail, StringComparison.Ordinal);
        Assert.Contains("can't be sent again while its subscription is disabled", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=SendAgain", detail, StringComparison.Ordinal);
    }

    private HttpClient Browser() => _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> SignedInAsync()
    {
        var browser = Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await SignInAsync(browser, HooklineApp.AdminPassword)).StatusCode);
        return browser;
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string password, string? returnUrl = null) =>
        await PostFormAsync(browser, "/login", returnUrl is null ? "/login" : $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}", new() { ["Password"] = password });

    /// <summary>Opens a page, takes the antiforgery token from its form, and posts a form as the browser would.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string formPage, string action, Dictionary<string, string> fields)
    {
        var page = await browser.GetStringAsync(formPage, Cancel);
        var token = AntiforgeryToken().Match(page);
        Assert.True(token.Success, $"No antiforgery token on {formPage}.");
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value);
        return await browser.PostAsync(action, new FormUrlEncodedContent(fields), Cancel);
    }

    private static async Task<string?> RedirectAsync(HttpClient browser, string url)
    {
        var response = await browser.GetAsync(url, Cancel);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location?.PathAndQuery();
    }

    private Task<int> PassAsync() => _app.Services.GetRequiredService<DeliveryWorker>().RunOnceAsync(Cancel);

    private async Task<SendAgainResult> SendAgainAsync(string id)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DashboardData>().SendAgainAsync(id, Cancel);
    }

    private async Task<CreatedSubscription> SubscribeAsync(string path) =>
        await (await _api.PostJsonAsync("/api/subscriptions", new { url = _receiver.Url(path) })).ReadAsync<CreatedSubscription>(HttpStatusCode.Created);

    private async Task SendEventAsync() =>
        await (await _api.PostJsonAsync("/api/events", new { type = "order.paid", data = new { orderId = 1042 } })).ReadAsync<AcceptedEvent>(HttpStatusCode.Accepted);

    private async Task<string> DeliveryIdAsync(string subscriptionId)
    {
        await using var db = _app.Db();
        return await db.Deliveries.Where(d => d.SubscriptionId == subscriptionId).Select(d => d.Id).SingleAsync(Cancel);
    }

    private async Task<Delivery> DeliveryAsync(string id)
    {
        await using var db = _app.Db();
        return await db.Deliveries.SingleAsync(d => d.Id == id, Cancel);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();

    [GeneratedRegex("""<td class="num">\d+ ms</td>""")]
    private static partial Regex AttemptRow();
}

internal static class UriExtensions
{
    /// <summary>The path and query of a redirect, whether the server sent it relative or absolute.</summary>
    public static string PathAndQuery(this Uri uri) => uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
}
