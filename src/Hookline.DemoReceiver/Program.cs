using System.Collections.Concurrent;

using Hookline.Core;
using Hookline.DemoReceiver;

// The other side of the demo. It plays the sending app, creating one subscription per path below and posting
// a stream of events, and it is the receiver those subscriptions point at, misbehaving on purpose on all but
// one path. It checks every request's signature with Hookline.Core's verifier and logs what it saw.

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Demo"));
builder.Services.AddSingleton<Secrets>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<SendingApp>();

var app = builder.Build();
var secrets = app.Services.GetRequiredService<Secrets>();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("receiver");
var flakySeen = new ConcurrentDictionary<string, int>();

app.MapGet("/", () => "The hookline demo receiver. Paths: " + string.Join(", ", Receiver.Paths));

app.MapPost("/{path}", async (string path, HttpContext context) =>
{
    using var buffer = new MemoryStream();
    await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
    var body = buffer.ToArray();
    var headers = context.Request.Headers;
    var id = headers[WebhookSignature.IdHeader].ToString();

    var signature = secrets.For(path) is { } secret
        ? WebhookSignature.Verify(
            secret,
            id,
            headers[WebhookSignature.TimestampHeader],
            headers[WebhookSignature.SignatureHeader],
            body,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5)) is { } failure ? $"signature BAD ({failure})" : "signature ok"
        : "signature unknown (no secret for this path)";

    var status = path switch
    {
        "ok" => 200,
        "flaky" => flakySeen.AddOrUpdate(id, 1, (_, seen) => seen + 1) <= 2 ? 500 : 200,
        "slow" => await Receiver.SlowAsync(context.RequestAborted),
        "down" => 500,
        "gone" => 410,
        _ => 404,
    };

    Receiver.Log(log, path, id, signature, status);
    return Results.StatusCode(status);
});

app.Run();
