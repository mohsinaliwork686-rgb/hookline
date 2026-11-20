using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Hookline.Api.Data;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Hookline.Api.Dashboard;

/// <summary>The dashboard's settings, from the <c>Hookline</c> section of configuration.</summary>
public sealed class DashboardOptions
{
    /// <summary>The one password that opens the dashboard. Without one, the dashboard stays shut.</summary>
    public string? AdminPassword { get; set; }

    public bool Enabled => !string.IsNullOrEmpty(AdminPassword);

    /// <summary>Both sides are hashed first, so the comparison takes the same time whatever was typed.</summary>
    public bool Matches(string? password) =>
        Enabled
        && !string.IsNullOrEmpty(password)
        && CryptographicOperations.FixedTimeEquals(Hash(password), Hash(AdminPassword!));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

public sealed record DeliveryRow(
    string Id,
    string EventId,
    string EventType,
    string Url,
    DeliveryStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? DeliveredAt,
    string? LastError,
    DateTimeOffset CreatedAt);

public sealed record DeliveryDetail(
    DeliveryRow Delivery,
    string Body,
    bool SubscriptionEnabled,
    string? DisabledReason,
    IReadOnlyList<DeliveryAttempt> Attempts);

public enum SendAgainResult
{
    Queued,
    NotFound,
    NotDead,
    SubscriptionDisabled,
}

/// <summary>What the dashboard reads, and its one action.</summary>
public sealed class DashboardData(HooklineDbContext db, TimeProvider clock)
{
    public const int PageSize = 100;

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public DateTimeOffset Now => clock.GetUtcNow();

    public async Task<IReadOnlyDictionary<DeliveryStatus, int>> CountsAsync(CancellationToken cancellationToken)
    {
        var counts = await db.Deliveries.GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken);
        return Enum.GetValues<DeliveryStatus>().ToDictionary(s => s, s => counts.GetValueOrDefault(s));
    }

    /// <summary>The newest deliveries, optionally only those with one status.</summary>
    public async Task<IReadOnlyList<DeliveryRow>> ListAsync(DeliveryStatus? status, CancellationToken cancellationToken) =>
        await Rows(db.Deliveries
                .Where(d => status == null || d.Status == status)
                .OrderByDescending(d => d.CreatedAt)
                .ThenByDescending(d => d.Id)
                .Take(PageSize))
            .ToListAsync(cancellationToken);

    public async Task<DeliveryDetail?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var found = await db.Deliveries.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new { d.Event.Body, d.Subscription.Enabled, d.Subscription.DisabledReason })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return null;
        }

        var row = await Rows(db.Deliveries.Where(d => d.Id == id)).SingleAsync(cancellationToken);
        var attempts = await db.DeliveryAttempts.AsNoTracking()
            .Where(a => a.DeliveryId == id)
            .OrderBy(a => a.Number)
            .ToListAsync(cancellationToken);
        return new DeliveryDetail(row, Pretty(found.Body), found.Enabled, found.DisabledReason, attempts);
    }

    /// <summary>
    /// Puts a dead delivery back in the queue, due now. Its attempt count is kept, so if this attempt fails too
    /// it is dead again straight away rather than starting a new three-day schedule.
    /// </summary>
    public async Task<SendAgainResult> SendAgainAsync(string id, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var queued = await db.Deliveries
            .Where(d => d.Id == id && d.Status == DeliveryStatus.Dead && d.Subscription.Enabled)
            .ExecuteUpdateAsync(
                set => set.SetProperty(d => d.Status, DeliveryStatus.Pending)
                    .SetProperty(d => d.NextAttemptAt, now)
                    .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(d => d.LeaseId, (Guid?)null),
                cancellationToken);
        if (queued == 1)
        {
            return SendAgainResult.Queued;
        }

        var found = await db.Deliveries.Where(d => d.Id == id)
            .Select(d => new { d.Status, d.Subscription.Enabled })
            .SingleOrDefaultAsync(cancellationToken);
        return found switch
        {
            null => SendAgainResult.NotFound,
            { Status: not DeliveryStatus.Dead } => SendAgainResult.NotDead,
            _ => SendAgainResult.SubscriptionDisabled,
        };
    }

    private static IQueryable<DeliveryRow> Rows(IQueryable<Delivery> deliveries) =>
        deliveries.AsNoTracking().Select(d => new DeliveryRow(
            d.Id,
            d.EventId,
            d.Event.Type,
            d.Subscription.Url,
            d.Status,
            d.Attempts,
            d.NextAttemptAt,
            d.DeliveredAt,
            d.LastError,
            d.CreatedAt));

    private static string Pretty(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, IndentedJson);
    }
}

/// <summary>How the dashboard writes times.</summary>
public static class Display
{
    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Relative(DateTimeOffset at, DateTimeOffset now)
    {
        var gap = at - now;
        var size = gap.Duration() switch
        {
            { TotalSeconds: < 1 } => null,
            { TotalMinutes: < 1 } d => $"{(int)d.TotalSeconds} s",
            { TotalHours: < 1 } d => $"{(int)d.TotalMinutes} min",
            { TotalDays: < 1 } d => $"{(int)d.TotalHours} h {d.Minutes} min",
            var d => $"{(int)d.TotalDays} d {d.Hours} h",
        };
        return size is null ? (gap >= TimeSpan.Zero ? "now" : "just now") : gap > TimeSpan.Zero ? $"in {size}" : $"{size} ago";
    }

    public static string When(DeliveryRow row, DateTimeOffset now) => row.Status switch
    {
        DeliveryStatus.Pending => $"next attempt {Relative(row.NextAttemptAt, now)}",
        DeliveryStatus.Delivered when row.DeliveredAt is { } at => $"delivered {Relative(at, now)}",
        _ => $"created {Relative(row.CreatedAt, now)}",
    };

    public static string SendAgain(SendAgainResult result) => result switch
    {
        SendAgainResult.Queued => "Back in the queue, due now. The worker sends it on its next round.",
        SendAgainResult.NotDead => "Only a dead delivery can be sent again.",
        SendAgainResult.SubscriptionDisabled => "Its subscription is disabled, so it can't be sent again.",
        _ => "There's no such delivery.",
    };
}

public static class DashboardSetup
{
    public static IServiceCollection AddDashboard(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DashboardOptions>(configuration.GetSection("Hookline"));
        services.AddScoped<DashboardData>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.LoginPath = "/login";
                cookie.Cookie.Name = "hookline-admin";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
                cookie.SlidingExpiration = true;
            });
        services.AddAuthorization();
        services.AddRazorPages(pages =>
        {
            pages.Conventions.AuthorizeFolder("/");
            pages.Conventions.AllowAnonymousToPage("/Login");
        });
        return services;
    }
}
