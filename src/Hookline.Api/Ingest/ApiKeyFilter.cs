using System.Security.Cryptography;
using System.Text;

namespace Hookline.Api.Ingest;

/// <summary>
/// The API is for one sending app, which proves itself with a bearer key from configuration. Both sides are
/// hashed before they are compared, so the comparison takes the same time whatever the key's length.
/// </summary>
public sealed class ApiKeyFilter(IConfiguration configuration) : IEndpointFilter
{
    private readonly byte[] _expected = Hash(configuration["Hookline:ApiKey"]
        ?? throw new InvalidOperationException("Set Hookline:ApiKey to the key the sending app will use."));

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        var given = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : "";

        if (given.Length == 0 || !CryptographicOperations.FixedTimeEquals(Hash(given), _expected))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Send the API key as a bearer token.");
        }

        return await next(context);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
