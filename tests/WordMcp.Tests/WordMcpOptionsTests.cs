using WordMcp.Configuration;

namespace WordMcp.Tests;

public sealed class WordMcpOptionsTests
{
    [Fact]
    public void ValidateAcceptsSecureLocalConfiguration()
    {
        using var environment = new TestEnvironment();

        environment.Options.Value.Validate(requireSecrets: true);
    }

    [Fact]
    public void ValidateRejectsReusedSecrets()
    {
        using var environment = new TestEnvironment();
        var value = environment.Options.Value;
        var options = new WordMcpOptions
        {
            SharedSecret = value.SharedSecret,
            ArtifactSigningKey = value.SharedSecret,
            ScopeHmacKey = value.ScopeHmacKey,
            PublicBaseUrl = value.PublicBaseUrl,
            LocalDevelopment = true,
            StorageRoot = value.StorageRoot,
            LibreChatUploadsRoot = value.LibreChatUploadsRoot,
            TemplatesRoot = value.TemplatesRoot,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate(requireSecrets: true));
    }

    [Fact]
    public void ValidateRejectsHttpOutsideLocalDevelopment()
    {
        var options = new WordMcpOptions
        {
            SharedSecret = "shared-secret-at-least-24-characters",
            ArtifactSigningKey = "artifact-signing-key-at-least-32-characters",
            ScopeHmacKey = "scope-hmac-key-independent-at-least-32-characters",
            PublicBaseUrl = "http://example.test",
            LocalDevelopment = false,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate(requireSecrets: true));
    }

    [Fact]
    public void ValidateRejectsResourceLimitAboveHardCeiling()
    {
        using var environment = new TestEnvironment();
        var value = environment.Options.Value;
        var options = new WordMcpOptions
        {
            SharedSecret = value.SharedSecret,
            ArtifactSigningKey = value.ArtifactSigningKey,
            ScopeHmacKey = value.ScopeHmacKey,
            PublicBaseUrl = value.PublicBaseUrl,
            LocalDevelopment = true,
            StorageRoot = value.StorageRoot,
            LibreChatUploadsRoot = value.LibreChatUploadsRoot,
            TemplatesRoot = value.TemplatesRoot,
            MaxQueueDepth = 13,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate(requireSecrets: true));
    }

    [Theory]
    [InlineData("storage", "storage/uploads", "templates")]
    [InlineData("storage/uploads", "storage", "templates")]
    [InlineData("storage", "uploads", "storage/templates")]
    public void ValidateRejectsOverlappingStorageBoundaries(
        string storageSuffix,
        string uploadsSuffix,
        string templatesSuffix)
    {
        using var environment = new TestEnvironment();
        var value = environment.Options.Value;
        var root = Path.Combine(Path.GetTempPath(), $"word-mcp-options-{Guid.NewGuid():N}");
        var options = new WordMcpOptions
        {
            SharedSecret = value.SharedSecret,
            ArtifactSigningKey = value.ArtifactSigningKey,
            ScopeHmacKey = value.ScopeHmacKey,
            PublicBaseUrl = value.PublicBaseUrl,
            LocalDevelopment = true,
            StorageRoot = Path.Combine(root, storageSuffix),
            LibreChatUploadsRoot = Path.Combine(root, uploadsSuffix),
            TemplatesRoot = Path.Combine(root, templatesSuffix),
        };

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate(requireSecrets: true));

        Assert.Contains("non-overlapping", error.Message, StringComparison.Ordinal);
    }
}
