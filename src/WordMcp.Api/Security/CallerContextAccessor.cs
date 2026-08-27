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
            OptionalHeader(request, "X-LibreChat-Message-ID"),
            ReadAttachmentFileIds(request));
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

    private static HashSet<string>? ReadAttachmentFileIds(HttpRequest request)
    {
        const string name = "X-LibreChat-Attachment-File-IDs";
        var values = request.Headers[name];
        if (values.Count == 0)
        {
            // Local clients may omit this header. Production LibreChat injects
            // either the current message file IDs or '-' when there are none.
            return null;
        }

        var raw = values.Count == 1 ? values[0] : null;
        if (raw == "-")
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4_128)
        {
            throw InvalidAttachmentHeader(name);
        }

        var fileIds = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fileIds.Length is < 1 or > 32
            || fileIds.Any(static fileId => !IsSafeFileId(fileId)))
        {
            throw InvalidAttachmentHeader(name);
        }

        return fileIds.ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsSafeFileId(string value) =>
        value.Length is >= 1 and <= 128
        && value.All(static character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-');

    private static WordMcpException InvalidAttachmentHeader(string name) => new(
        "trusted_header_invalid",
        $"$headers.{name}",
        $"Trusted header '{name}' is invalid.",
        "Configure the trusted reverse proxy to inject '-' or a comma-separated list of opaque file IDs.");
}
