using Hookline.Api.Data;
using Hookline.Core;

using Microsoft.EntityFrameworkCore;

namespace Hookline.Api.Ingest;

public sealed record CreateSubscription(string? Url, string[]? EventTypes);

public sealed record SubscriptionResponse(string Id, string Url, string[] EventTypes, bool Enabled, string? DisabledReason, DateTimeOffset CreatedAt)
{
    public static SubscriptionResponse From(Subscription s) => new(s.Id, s.Url, s.EventTypes, s.Enabled, s.DisabledReason, s.CreatedAt);
}

/// <summary>The only response that includes the secret: it is shown once, when the subscription is made.</summary>
public sealed record CreatedSubscription(string Id, string Url, string[] EventTypes, bool Enabled, DateTimeOffset CreatedAt, string Secret);

public static class SubscriptionEndpoints
{
    public const int MaxEventTypes = 50;
    public const int MaxEventTypeLength = 100;

    public static void MapSubscriptions(this RouteGroupBuilder api)
    {
        var subscriptions = api.MapGroup("/subscriptions");
        subscriptions.MapPost("", CreateAsync);
        subscriptions.MapGet("", ListAsync);
        subscriptions.MapGet("/{id}", GetAsync);
    }

    private static async Task<IResult> CreateAsync(CreateSubscription request, HooklineDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            errors["url"] = ["An absolute http or https URL."];
        }
        else if (request.Url!.Length > 2000)
        {
            errors["url"] = ["At most 2000 characters."];
        }

        var types = request.EventTypes ?? [];
        if (types.Length > MaxEventTypes || types.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > MaxEventTypeLength))
        {
            errors["eventTypes"] = [$"Up to {MaxEventTypes} event types, each 1 to {MaxEventTypeLength} characters. Leave it out for every type."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var subscription = new Subscription
        {
            Id = Ids.New(Ids.Subscription),
            Url = request.Url!,
            EventTypes = [.. types.Distinct(StringComparer.Ordinal)],
            Secret = WebhookSecret.Generate(),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/subscriptions/{subscription.Id}",
            new CreatedSubscription(subscription.Id, subscription.Url, subscription.EventTypes, subscription.Enabled, subscription.CreatedAt, subscription.Secret));
    }

    private static async Task<List<SubscriptionResponse>> ListAsync(HooklineDbContext db, CancellationToken cancellationToken) =>
        (await db.Subscriptions.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken)).ConvertAll(SubscriptionResponse.From);

    private static async Task<IResult> GetAsync(string id, HooklineDbContext db, CancellationToken cancellationToken) =>
        await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is { } subscription
            ? Results.Ok(SubscriptionResponse.From(subscription))
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such subscription.");
}
