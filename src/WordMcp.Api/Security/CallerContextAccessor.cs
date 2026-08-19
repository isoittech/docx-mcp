using WordMcp.Domain;

namespace WordMcp.Security;

public sealed class CallerContextAccessor(IHttpContextAccessor httpContextAccessor)
{
    private const int MaxHeaderLength = 256;

    public CallerContext GetRequired()
    {
        var request = httpContextAccessor.HttpContext?.Request
            ?? throw new WordMcpException(
                "caller_context_unavailable",
                "$headers",
                "The trusted request context is unavailable.",
                "Call the tool through the configured LibreChat or local Codex proxy.");

        return new CallerContext(
            RequiredHeader(request, "X-LibreChat-User-ID"),
            RequiredHeader(request, "X-LibreChat-Conversation-ID"),
            OptionalHeader(request, "X-LibreChat-Message-ID"));
    }

    private static string RequiredHeader(HttpRequest request, string name)
    {
        var value = OptionalHeader(request, name);
        if (value is null)
        {
            throw new WordMcpException(
                "trusted_header_missing",
                $"$headers.{name}",
                $"Trusted header '{name}' is missing or invalid.",
                "Configure the trusted reverse proxy to inject a non-empty resolved identifier.");
        }

        return value;
    }

    private static string? OptionalHeader(HttpRequest request, string name)
    {
        var values = request.Headers[name];
        if (values.Count > 1)
        {
            return null;
        }

        var value = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxHeaderLength
            || value.Any(char.IsControl)
            || (value.StartsWith("{{", StringComparison.Ordinal) && value.EndsWith("}}", StringComparison.Ordinal)))
        {
            return null;
        }

        return value;
    }
}
