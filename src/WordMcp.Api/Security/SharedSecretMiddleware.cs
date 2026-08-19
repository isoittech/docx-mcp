using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;

namespace WordMcp.Security;

public sealed class SharedSecretMiddleware(RequestDelegate next, IOptions<WordMcpOptions> options)
{
    private readonly byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.SharedSecret));

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)
            || context.Request.Path.StartsWithSegments("/artifacts", StringComparison.Ordinal))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var authorization = context.Request.Headers.Authorization;
        var value = authorization.Count == 1 ? authorization[0] : null;
        var supplied = value?.StartsWith("Bearer ", StringComparison.Ordinal) == true
            ? value["Bearer ".Length..]
            : string.Empty;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));

        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { status = "unauthorized" }).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
