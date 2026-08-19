using Microsoft.Extensions.Options;
using WordMcp.Configuration;

namespace WordMcp.Middleware;

public sealed class OriginValidationMiddleware(RequestDelegate next, IOptions<WordMcpOptions> options)
{
    private readonly HashSet<string> allowed = options.Value.AllowedOrigins.ToHashSet(StringComparer.Ordinal);
    private readonly bool localDevelopment = options.Value.LocalDevelopment;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal)
            || !context.Request.Headers.TryGetValue("Origin", out var origins)
            || origins.Count == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var origin = origins.Count == 1 ? origins[0] : null;
        var accepted = origin is not null
                       && (allowed.Contains(origin) || (localDevelopment && IsLoopbackOrigin(origin)));
        if (!accepted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { status = "origin_not_allowed" }).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.IsLoopback
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
