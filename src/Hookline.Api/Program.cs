using System.Text.Json;
using System.Text.Json.Serialization;

using Hookline.Api.Dashboard;
using Hookline.Api.Data;
using Hookline.Api.Ingest;
using Hookline.Api.Worker;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// GSS encryption is turned off: Npgsql would otherwise try Kerberos first, which the aspnet image doesn't have.
var connectionString = new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString("Hookline")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Hookline to a PostgreSQL connection string."))
{
    GssEncryptionMode = GssEncryptionMode.Disable,
}.ConnectionString;

// Interceptors registered in the container are added to every context, which lets a test fail a save on purpose.
builder.Services.AddDbContext<HooklineDbContext>((services, options) => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(services.GetServices<IInterceptor>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ApiKeyFilter>();
builder.Services.AddDeliveryWorker(builder.Configuration);
builder.Services.AddDashboard(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<HooklineDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();
api.MapSubscriptions();
api.MapEvents();

app.MapRazorPages();

app.Run();

public partial class Program;
