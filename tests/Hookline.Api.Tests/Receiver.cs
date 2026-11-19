using System.Collections.Concurrent;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Hookline.Api.Tests;

/// <summary>One request as the receiver saw it.</summary>
public sealed record ReceivedRequest(string Path, string Method, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// A real HTTP server on a free local port, so the worker's requests go over the network: redirects, timeouts
/// and refused connections behave as they would with a real receiver. Each path answers as the test says, and
/// every request is kept.
/// </summary>
public sealed class Receiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Func<HttpContext, Task>> _paths = new();
    private readonly ConcurrentQueue<ReceivedRequest> _requests = new();

    private Receiver(WebApplication app)
    {
        _app = app;
        _app.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            var path = context.Request.Path.Value ?? "";
            _requests.Enqueue(new ReceivedRequest(
                path,
                context.Request.Method,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body.ToArray()));

            if (_paths.TryGetValue(path, out var answer))
            {
                await answer(context);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });
    }

    public string BaseUrl { get; private set; } = "";

    public IReadOnlyList<ReceivedRequest> Requests => [.. _requests];

    public static async Task<Receiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var receiver = new Receiver(builder.Build());
        await receiver._app.StartAsync(TestContext.Current.CancellationToken);
        receiver.BaseUrl = receiver._app.Urls.Single();
        return receiver;
    }

    public string Url(string path) => BaseUrl + path;

    public IReadOnlyList<ReceivedRequest> To(string path) => [.. _requests.Where(r => r.Path == path)];

    /// <summary>Answers requests to this path with the given code.</summary>
    public void Answer(string path, int statusCode, string? body = null, params (string Name, string Value)[] headers) =>
        On(path, async context =>
        {
            context.Response.StatusCode = statusCode;
            foreach (var (name, value) in headers)
            {
                context.Response.Headers[name] = value;
            }

            if (body is not null)
            {
                await context.Response.WriteAsync(body, context.RequestAborted);
            }
        });

    /// <summary>Fails the first <paramref name="failures"/> requests to this path with a 500, then answers 200.</summary>
    public void FailThenSucceed(string path, int failures)
    {
        var seen = 0;
        On(path, context =>
        {
            context.Response.StatusCode = Interlocked.Increment(ref seen) <= failures ? 500 : 200;
            return Task.CompletedTask;
        });
    }

    /// <summary>Holds requests to this path open until <paramref name="release"/> completes, or the sender gives up.</summary>
    public void Hold(string path, Task release) =>
        On(path, async context =>
        {
            await release.WaitAsync(context.RequestAborted);
            context.Response.StatusCode = 200;
        });

    public void On(string path, Func<HttpContext, Task> answer) => _paths[path] = answer;

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
