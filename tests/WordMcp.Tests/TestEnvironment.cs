using System.Globalization;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;

namespace WordMcp.Tests;

internal sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), $"word-mcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "uploads"));
        Directory.CreateDirectory(Path.Combine(Root, "templates"));
        Options = Microsoft.Extensions.Options.Options.Create(new WordMcpOptions
        {
            SharedSecret = "shared-secret-at-least-24-characters",
            ArtifactSigningKey = "artifact-signing-key-at-least-32-characters",
            ScopeHmacKey = "scope-hmac-key-independent-at-least-32-characters",
            PublicBaseUrl = "http://127.0.0.1:18081",
            LocalDevelopment = true,
            StorageRoot = Path.Combine(Root, "storage"),
            LibreChatUploadsRoot = Path.Combine(Root, "uploads"),
            TemplatesRoot = Path.Combine(Root, "templates"),
            LibreOfficePath = "/usr/bin/libreoffice",
            PythonPath = "/usr/bin/python3",
            UnoScriptPath = "/app/scripts/update-word-indexes.py",
            PdfInfoPath = "/usr/bin/pdfinfo",
            PdfToPngPath = "/usr/bin/pdftoppm",
        });
        Time = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-11T00:00:00Z", CultureInfo.InvariantCulture));
    }

    public string Root { get; }

    public IOptions<WordMcpOptions> Options { get; }

    public MutableTimeProvider Time { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan value) => current = current.Add(value);
}
