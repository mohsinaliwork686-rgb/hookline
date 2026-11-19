using Microsoft.Extensions.Options;

namespace Hookline.Api.Worker;

public static class WorkerSetup
{
    public static IServiceCollection AddDeliveryWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DeliveryOptions>()
            .Bind(configuration.GetSection(DeliveryOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DeliveryOptions>, DeliveryOptionsValidator>();

        // Redirects aren't followed: a signed request isn't forwarded somewhere the subscriber didn't register.
        // The sender applies its own timeout to each attempt.
        services.AddHttpClient(DeliverySender.ClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("hookline/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddScoped<DeliveryStore>();
        services.AddSingleton<DeliverySender>();
        services.AddSingleton<DeliveryWorker>();
        services.AddHostedService<DeliveryService>();
        return services;
    }

    private sealed class DeliveryOptionsValidator : IValidateOptions<DeliveryOptions>
    {
        public ValidateOptionsResult Validate(string? name, DeliveryOptions options) =>
            options.Problem() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
    }
}
