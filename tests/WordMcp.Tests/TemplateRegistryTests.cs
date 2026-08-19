using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class TemplateRegistryTests
{
    [Theory]
    [InlineData(false, ".docx", ResolvedInputFormat.Docx)]
    [InlineData(true, ".dotx", ResolvedInputFormat.Dotx)]
    public async Task SnapshotsAndValidatesConfiguredDefault(
        bool template,
        string extension,
        ResolvedInputFormat expectedFormat)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        const string templateId = "corporate-default";
        using var package = DocxTestPackage.Create(template: template, extension: extension);
        File.Copy(package.Path, Path.Combine(environment.Options.Value.TemplatesRoot, $"{templateId}{extension}"));
        var options = TemplateOptions(environment, templateId);
        var registry = new TemplateRegistry(options, new DocxPackageGuard(options));

        var snapshot = await registry.SnapshotDefaultAsync(
            NewSnapshotDirectory(environment),
            CancellationToken.None);
        var inspection = await registry.ValidateConfiguredDefaultAsync(CancellationToken.None);

        Assert.True(registry.HasDefault);
        Assert.Equal(expectedFormat, snapshot.Format);
        Assert.True(File.Exists(snapshot.Path));
        Assert.NotNull(inspection);
        Assert.Equal(template, inspection.IsTemplate);
        Assert.True(registry.Readiness.IsReady);
        Assert.Equal(TemplateReadinessState.Ready, registry.Readiness.State);
    }

    [Fact]
    public async Task NoConfiguredDefaultIsReadyButCannotBeResolved()
    {
        using var environment = new TestEnvironment();
        var options = TemplateOptions(environment, string.Empty);
        var registry = new TemplateRegistry(options, new DocxPackageGuard(options));

        var inspection = await registry.ValidateConfiguredDefaultAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<WordMcpException>(() => registry.SnapshotDefaultAsync(
            NewSnapshotDirectory(environment),
            CancellationToken.None));

        Assert.Null(inspection);
        Assert.False(registry.HasDefault);
        Assert.True(registry.Readiness.IsReady);
        Assert.Equal(TemplateReadinessState.NotConfigured, registry.Readiness.State);
        Assert.Equal("default_template_not_configured", error.Code);
    }

    [Fact]
    public async Task RejectsMissingAndAmbiguousDeploymentTemplateIds()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        const string templateId = "duplicate";
        var options = TemplateOptions(environment, templateId);
        var registry = new TemplateRegistry(options, new DocxPackageGuard(options));
        var missing = await Assert.ThrowsAsync<WordMcpException>(() => registry.SnapshotDefaultAsync(
            NewSnapshotDirectory(environment),
            CancellationToken.None));
        using var document = DocxTestPackage.Create();
        using var template = DocxTestPackage.Create(template: true, extension: ".dotx");
        File.Copy(document.Path, Path.Combine(options.Value.TemplatesRoot, $"{templateId}.docx"));
        File.Copy(template.Path, Path.Combine(options.Value.TemplatesRoot, $"{templateId}.dotx"));

        var ambiguous = await Assert.ThrowsAsync<WordMcpException>(() => registry.SnapshotDefaultAsync(
            NewSnapshotDirectory(environment),
            CancellationToken.None));

        Assert.Equal("template_not_found", missing.Code);
        Assert.Equal("ambiguous_template_id", ambiguous.Code);
    }

    [Fact]
    public async Task RejectsSymlinkAndHardlinkDeploymentTemplates()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        using var package = DocxTestPackage.Create();
        var symlinkOptions = TemplateOptions(environment, "symlink-template");
        File.CreateSymbolicLink(
            Path.Combine(symlinkOptions.Value.TemplatesRoot, "symlink-template.docx"),
            package.Path);
        var symlinkRegistry = new TemplateRegistry(symlinkOptions, new DocxPackageGuard(symlinkOptions));
        var symlinkError = await Assert.ThrowsAsync<WordMcpException>(() => symlinkRegistry.SnapshotDefaultAsync(
            NewSnapshotDirectory(environment),
            CancellationToken.None));
        var hardlinkOptions = TemplateOptions(environment, "hardlink-template");
        Assert.Equal(
            0,
            Link(package.Path, Path.Combine(hardlinkOptions.Value.TemplatesRoot, "hardlink-template.docx")));
        var hardlinkRegistry = new TemplateRegistry(hardlinkOptions, new DocxPackageGuard(hardlinkOptions));
        var hardlinkError = await Assert.ThrowsAsync<WordMcpException>(() => hardlinkRegistry.SnapshotDefaultAsync(
            NewSnapshotDirectory(environment),
            CancellationToken.None));

        Assert.Equal("unsafe_template_file", symlinkError.Code);
        Assert.Equal("unsafe_template_file", hardlinkError.Code);
    }

    [Fact]
    public async Task ExplicitAdministratorTemplateIdIsOpaqueAndIndependentFromDefault()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        using var package = DocxTestPackage.Create();
        File.Copy(package.Path, Path.Combine(environment.Options.Value.TemplatesRoot, "alternate.docx"));
        var options = TemplateOptions(environment, "missing-default");
        var registry = new TemplateRegistry(options, new DocxPackageGuard(options));

        var snapshot = await registry.SnapshotByIdAsync(
            "alternate",
            NewSnapshotDirectory(environment),
            CancellationToken.None);
        var invalid = await Assert.ThrowsAsync<WordMcpException>(() => registry.SnapshotByIdAsync(
            "../alternate.docx",
            NewSnapshotDirectory(environment),
            CancellationToken.None));

        Assert.Equal("alternate", snapshot.SourceId);
        Assert.Equal(ResolvedInputFormat.Docx, snapshot.Format);
        Assert.Equal("invalid_template_id", invalid.Code);
    }

    [Fact]
    public async Task WarmupKeepsServiceNotReadyWhenDefaultPackageIsUnsafe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        const string templateId = "unsafe-default";
        await File.WriteAllBytesAsync(
            Path.Combine(environment.Options.Value.TemplatesRoot, $"{templateId}.docx"),
            [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        var options = TemplateOptions(environment, templateId);
        var registry = new TemplateRegistry(options, new DocxPackageGuard(options));
        var service = new DefaultTemplateWarmupService(
            registry,
            NullLogger<DefaultTemplateWarmupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.False(registry.Readiness.IsReady);
        Assert.Equal(TemplateReadinessState.Failed, registry.Readiness.State);
        Assert.Equal("invalid_zip", registry.Readiness.FailureCode);
    }

    [Fact]
    public async Task WarmupRejectsSchemaInvalidDefaultThatCanStillBeOpened()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        const string templateId = "schema-invalid-default";
        using var package = DocxTestPackage.Create(
            documentXml: DocxTestPackage.DocumentXml(
                "<w:p><w:r><w:t>text before properties</w:t><w:rPr><w:b/></w:rPr></w:r></w:p>"));
        File.Copy(
            package.Path,
            Path.Combine(environment.Options.Value.TemplatesRoot, $"{templateId}.docx"));
        var options = TemplateOptions(environment, templateId);
        var registry = new TemplateRegistry(options, new DocxPackageGuard(options));
        var service = new DefaultTemplateWarmupService(
            registry,
            NullLogger<DefaultTemplateWarmupService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(registry.Readiness.IsReady);
        Assert.Equal(TemplateReadinessState.Failed, registry.Readiness.State);
        Assert.Equal("generated_openxml_invalid", registry.Readiness.FailureCode);
    }

    [Fact]
    public async Task CachesDefaultInspectionOnlyForTheSameContentHash()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        const string templateId = "content-addressed";
        var templatePath = Path.Combine(environment.Options.Value.TemplatesRoot, $"{templateId}.docx");
        using var firstPackage = DocxTestPackage.Create(
            documentXml: DocxTestPackage.DocumentXml("<w:p><w:r><w:t>first</w:t></w:r></w:p>"));
        File.Copy(firstPackage.Path, templatePath);
        var options = TemplateOptions(environment, templateId);
        var registry = new TemplateRegistry(options, new DocxPackageGuard(options));

        var first = await registry.ValidateConfiguredDefaultAsync(TestContext.Current.CancellationToken);
        var cached = await registry.ValidateConfiguredDefaultAsync(TestContext.Current.CancellationToken);

        using var secondPackage = DocxTestPackage.Create(
            documentXml: DocxTestPackage.DocumentXml("<w:p><w:r><w:t>second content</w:t></w:r></w:p>"));
        File.Copy(secondPackage.Path, templatePath, overwrite: true);
        var changed = await registry.ValidateConfiguredDefaultAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, cached);
        Assert.NotSame(first, changed);
        Assert.NotEqual(first!.CharacterCount, changed!.CharacterCount);
    }

    private static IOptions<WordMcpOptions> TemplateOptions(TestEnvironment environment, string defaultTemplateId) =>
        Options.Create(new WordMcpOptions
        {
            StorageRoot = environment.Options.Value.StorageRoot,
            LibreChatUploadsRoot = environment.Options.Value.LibreChatUploadsRoot,
            TemplatesRoot = environment.Options.Value.TemplatesRoot,
            DefaultTemplateId = defaultTemplateId,
            PublicBaseUrl = "http://127.0.0.1:18081",
            LocalDevelopment = true,
        });

    private static string NewSnapshotDirectory(TestEnvironment environment)
    {
        var path = Path.Combine(environment.Root, "template-snapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute", Justification = "The test project intentionally does not enable unsafe blocks.")]
    [SuppressMessage("Security", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "Linux libc path arguments are explicitly marshaled as UTF-8.")]
    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
