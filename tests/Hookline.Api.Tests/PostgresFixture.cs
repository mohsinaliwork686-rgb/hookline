using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hookline.Api.Data;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Npgsql;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Hookline.Api.Tests.PostgresFixture))]

namespace Hookline.Api.Tests;

/// <summary>
/// One real PostgreSQL for the whole run. Each test gets an app on a database of its own, created empty and
/// migrated as the app starts, so tests running in parallel never see each other's subscriptions.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    public async Task<HooklineApp> NewAppAsync(FakeTimeProvider clock, Action<IServiceCollection>? services = null)
    {
        var database = "test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;
        var app = new HooklineApp(connectionString, clock, services);
        _ = app.Server;
        return app;
    }
}

public sealed class HooklineApp(string connectionString, FakeTimeProvider clock, Action<IServiceCollection>? services) : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public FakeTimeProvider Clock => clock;

    public string ConnectionString => connectionString;

    /// <summary>A client that sends the API key, as the sending app does.</summary>
    public HttpClient ApiClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        return client;
    }

    /// <summary>A database context of its own, for checking what was saved.</summary>
    public HooklineDbContext Db() => Services.CreateScope().ServiceProvider.GetRequiredService<HooklineDbContext>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Hookline", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Hookline:ApiKey", ApiKey);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            services?.Invoke(s);
        });
    }
}
