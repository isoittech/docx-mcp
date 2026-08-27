using System.Text.RegularExpressions;

namespace WordMcp.Configuration;

public sealed partial class WordMcpOptions
{
    public const string SectionName = "WordMcp";

    public string SharedSecret { get; init; } = string.Empty;

    public string ArtifactSigningKey { get; init; } = string.Empty;

    public string ScopeHmacKey { get; init; } = string.Empty;

    public string PublicBaseUrl { get; init; } = "http://127.0.0.1:18081";

    public bool LocalDevelopment { get; init; }

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public string StorageRoot { get; init; } = "/data/word-mcp";

    public string LibreChatUploadsRoot { get; init; } = "/data/librechat-uploads";

    public string TemplatesRoot { get; init; } = "/data/word-templates";

    public string DefaultTemplateId { get; init; } = string.Empty;

    public string FirstAssistantNotice { get; init; } = string.Empty;

    public string LibreOfficePath { get; init; } = "/usr/bin/libreoffice";

    public string PythonPath { get; init; } = "/usr/bin/python3";

    public string UnoScriptPath { get; init; } = "/app/scripts/update-word-indexes.py";

    public string PdfInfoPath { get; init; } = "/usr/bin/pdfinfo";

    public string PdfToTextPath { get; init; } = "/usr/bin/pdftotext";

    public string PdfToPngPath { get; init; } = "/usr/bin/pdftoppm";

    public long MaxRequestBodyBytes { get; init; } = 2L * 1024 * 1024;

    public int MaxJsonDepth { get; init; } = 32;

    public int MaxJsonStringCharacters { get; init; } = 200_000;

    public int MaxJsonTotalStringCharacters { get; init; } = 400_000;

    public long MaxFileBytes { get; init; } = 30L * 1024 * 1024;

    public int MaxZipEntries { get; init; } = 5_000;

    public long MaxUncompressedBytes { get; init; } = 300L * 1024 * 1024;

    public int MaxCompressionRatio { get; init; } = 250;

    public long MaxXmlPartBytes { get; init; } = 32L * 1024 * 1024;

    public int MaxXmlDepth { get; init; } = 64;

    public int MaxXmlAttributesPerElement { get; init; } = 128;

    public int MaxRelationshipsPerPart { get; init; } = 2_000;

    public int MaxBlocks { get; init; } = 1_000;

    public int MaxCharacters { get; init; } = 200_000;

    public int MaxTableCells { get; init; } = 10_000;

    public int MaxImages { get; init; } = 40;

    public int MaxExplicitPageBreaks { get; init; } = 50;

    public long MaxImageBytes { get; init; } = 12L * 1024 * 1024;

    public long MaxTotalImageBytes { get; init; } = 40L * 1024 * 1024;

    public int MaxImageDimension { get; init; } = 12_000;

    public long MaxImagePixels { get; init; } = 80_000_000;

    public long MaxTotalImagePixels { get; init; } = 200_000_000;

    public int MaxRenderedPages { get; init; } = 50;

    public long MaxPdfBytes { get; init; } = 100L * 1024 * 1024;

    public long MaxPreviewBytes { get; init; } = 250L * 1024 * 1024;

    public int MaxConcurrentJobs { get; init; } = 3;

    public int MaxQueueDepth { get; init; } = 12;

    public int JobTimeoutMinutes { get; init; } = 10;

    public int DraftLifetimeMinutes { get; init; } = 60;

    public int AnalysisLifetimeMinutes { get; init; } = 60;

    public int AnalysisCacheLifetimeMinutes { get; init; } = 60;

    public int ArtifactUrlMinutes { get; init; } = 15;

    public int RetentionDays { get; init; } = 7;

    public int RetentionHoursAfterDownload { get; init; } = 24;

    public int MaxStoredItemsPerConversation { get; init; } = 128;

    public long MaxStoredBytesPerConversation { get; init; } = 512L * 1024 * 1024;

    public int MaxStoredItemsPerUser { get; init; } = 512;

    public long MaxStoredBytesPerUser { get; init; } = 2L * 1024 * 1024 * 1024;

    public int MaxStoredItemsTotal { get; init; } = 4_096;

    public long MaxStoredBytesTotal { get; init; } = 10L * 1024 * 1024 * 1024;

    public void Validate(bool requireSecrets)
    {
        if (requireSecrets)
        {
            ValidateSecret(SharedSecret, 24, "SharedSecret");
            ValidateSecret(ArtifactSigningKey, 32, "ArtifactSigningKey");
            ValidateSecret(ScopeHmacKey, 32, "ScopeHmacKey");
            if (SharedSecret == ArtifactSigningKey || SharedSecret == ScopeHmacKey || ArtifactSigningKey == ScopeHmacKey)
            {
                throw new InvalidOperationException("WordMcp secrets must be independent values.");
            }
        }

        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var publicUri)
            || (publicUri.Scheme != Uri.UriSchemeHttps
                && !(LocalDevelopment && publicUri.Scheme == Uri.UriSchemeHttp))
            || !string.IsNullOrEmpty(publicUri.UserInfo)
            || !string.IsNullOrEmpty(publicUri.Query)
            || !string.IsNullOrEmpty(publicUri.Fragment))
        {
            throw new InvalidOperationException("WordMcp:PublicBaseUrl must be HTTPS, except in explicit local development.");
        }

        foreach (var origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || (uri.Scheme != Uri.UriSchemeHttps
                    && !(LocalDevelopment && uri.Scheme == Uri.UriSchemeHttp)))
            {
                throw new InvalidOperationException("WordMcp:AllowedOrigins must contain normalized HTTPS origins without paths.");
            }
        }

        foreach (var path in new[]
                 {
                     StorageRoot, LibreChatUploadsRoot, TemplatesRoot,
                 })
        {
            if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) == Path.GetPathRoot(path))
            {
                throw new InvalidOperationException("Configured storage paths must be absolute non-root paths.");
            }
        }

        var storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StorageRoot));
        var uploads = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LibreChatUploadsRoot));
        var templates = Path.TrimEndingDirectorySeparator(Path.GetFullPath(TemplatesRoot));
        if (PathsOverlap(storage, uploads)
            || PathsOverlap(storage, templates)
            || PathsOverlap(uploads, templates))
        {
            throw new InvalidOperationException("Storage, upload, and template roots must be distinct and non-overlapping.");
        }

        foreach (var path in new[]
                 {
                     LibreOfficePath, PythonPath, UnoScriptPath, PdfInfoPath, PdfToTextPath, PdfToPngPath,
                 })
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException("Configured executable and script paths must be absolute.");
            }
        }

        if (MaxRequestBodyBytes is <= 0 or > 2L * 1024 * 1024
            || MaxJsonDepth is <= 0 or > 32
            || MaxJsonStringCharacters is <= 0 or > 200_000
            || MaxJsonTotalStringCharacters is <= 0 or > 400_000
            || MaxJsonStringCharacters > MaxJsonTotalStringCharacters)
        {
            throw new InvalidOperationException("Request limits exceed the supported hard ceiling.");
        }

        if (MaxFileBytes is <= 0 or > 30L * 1024 * 1024
            || MaxZipEntries is <= 0 or > 5_000
            || MaxUncompressedBytes is <= 0 or > 300L * 1024 * 1024
            || MaxCompressionRatio is <= 0 or > 250)
        {
            throw new InvalidOperationException("Package limits exceed the supported hard ceiling.");
        }

        if (MaxXmlPartBytes is <= 0 or > 32L * 1024 * 1024
            || MaxXmlDepth is <= 0 or > 64
            || MaxXmlAttributesPerElement is <= 0 or > 128
            || MaxRelationshipsPerPart is <= 0 or > 2_000)
        {
            throw new InvalidOperationException("XML limits exceed the supported hard ceiling.");
        }

        if (MaxBlocks is <= 0 or > 1_000 || MaxCharacters is <= 0 or > 200_000
            || MaxTableCells is <= 0 or > 10_000 || MaxImages is <= 0 or > 40
            || MaxExplicitPageBreaks is <= 0 or > 50 || MaxRenderedPages is <= 0 or > 50)
        {
            throw new InvalidOperationException("Semantic limits exceed the supported hard ceiling.");
        }

        if (MaxImageBytes is <= 0 or > 12L * 1024 * 1024
            || MaxTotalImageBytes is <= 0 or > 40L * 1024 * 1024
            || MaxImageDimension is <= 0 or > 12_000
            || MaxImagePixels is <= 0 or > 80_000_000
            || MaxTotalImagePixels is <= 0 or > 200_000_000
            || MaxPdfBytes is <= 0 or > 100L * 1024 * 1024
            || MaxPreviewBytes is <= 0 or > 250L * 1024 * 1024)
        {
            throw new InvalidOperationException("Media limits exceed the supported hard ceiling.");
        }

        if (MaxConcurrentJobs is <= 0 or > 3 || MaxQueueDepth is <= 0 or > 12
            || JobTimeoutMinutes is <= 0 or > 10)
        {
            throw new InvalidOperationException("Job limits exceed the supported hard ceiling.");
        }

        if (DraftLifetimeMinutes is <= 0 or > 60 || AnalysisLifetimeMinutes is <= 0 or > 60
            || AnalysisCacheLifetimeMinutes is <= 0 or > 60
            || ArtifactUrlMinutes is <= 0 or > 15 || RetentionDays is <= 0 or > 7
            || RetentionHoursAfterDownload is <= 0 or > 24)
        {
            throw new InvalidOperationException("Lifetime settings exceed the supported hard ceiling.");
        }

        if (MaxStoredItemsPerConversation is <= 0 or > 128
            || MaxStoredBytesPerConversation is <= 0 or > 512L * 1024 * 1024
            || MaxStoredItemsPerUser is <= 0 or > 512
            || MaxStoredBytesPerUser is <= 0 or > 2L * 1024 * 1024 * 1024
            || MaxStoredItemsTotal is <= 0 or > 4_096
            || MaxStoredBytesTotal is <= 0 or > 10L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException("Per-conversation storage limits exceed the supported hard ceiling.");
        }

        if (DefaultTemplateId.Length > 128
            || (DefaultTemplateId.Length > 0 && !SafeIdentifier().IsMatch(DefaultTemplateId)))
        {
            throw new InvalidOperationException("DefaultTemplateId must be a safe opaque identifier.");
        }

        if (FirstAssistantNotice.Length > 1_000)
        {
            throw new InvalidOperationException("FirstAssistantNotice must not exceed 1000 characters.");
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(left, right, comparison))
        {
            return true;
        }

        var separator = Path.DirectorySeparatorChar.ToString();
        return left.StartsWith(right + separator, comparison)
               || right.StartsWith(left + separator, comparison);
    }

    private static void ValidateSecret(string value, int minimum, string name)
    {
        if (value.Length < minimum || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"WordMcp:{name} is missing or too short.");
        }
    }

    [GeneratedRegex("\\A[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
