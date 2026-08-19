using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class InputFileResolverTests
{
    private static readonly CallerContext Caller = new("user-a", "conversation-a", null);
    private static readonly CallerScope Scope = new("user-scope-a", "conversation-scope-a");

    [Fact]
    public async Task SnapshotsBeforePackageValidationSoUnsafeDocumentsCanBecomeRejectedJobs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var userDirectory = CreateUserDirectory(environment);
        const string fileId = "file-one";
        await File.WriteAllBytesAsync(
            Path.Combine(userDirectory, $"{fileId}__broken.docx"),
            [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        var snapshotDirectory = NewSnapshotDirectory(environment);
        var resolver = CreateResolver(environment);

        var snapshot = await resolver.SnapshotDocumentAsync(
            Caller,
            Scope,
            fileId,
            snapshotDirectory,
            CancellationToken.None);

        Assert.Equal(ResolvedInputFormat.Docx, snapshot.Format);
        Assert.Equal(4, snapshot.Bytes);
        Assert.True(File.Exists(snapshot.Path));
        var guard = new DocxPackageGuard(environment.Options);
        var error = await Assert.ThrowsAsync<WordMcpException>(
            () => guard.ValidateSnapshotAsync(snapshot.Path, CancellationToken.None));
        Assert.True(error.UnsafeDocument);
        Assert.Equal("invalid_zip", error.Code);
    }

    [Fact]
    public async Task LatestIsUserScopedAndExplicitIdAlwaysWins()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var userDirectory = CreateUserDirectory(environment);
        var older = Path.Combine(userDirectory, "older-id__old.docx");
        var newer = Path.Combine(userDirectory, "newer-id__new.dotx");
        await File.WriteAllBytesAsync(older, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(newer, [2], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(-1));
        var otherUserDirectory = Path.Combine(environment.Options.Value.LibreChatUploadsRoot, "other-user");
        Directory.CreateDirectory(otherUserDirectory);
        var otherUserNewest = Path.Combine(otherUserDirectory, "other-id__private.docx");
        await File.WriteAllBytesAsync(otherUserNewest, [3], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(otherUserNewest, DateTime.UtcNow);
        var resolver = CreateResolver(environment);

        var latest = await resolver.SnapshotDocumentAsync(
            Caller,
            Scope,
            null,
            NewSnapshotDirectory(environment),
            CancellationToken.None);
        var explicitOlder = await resolver.SnapshotDocumentAsync(
            Caller,
            Scope,
            "older-id",
            NewSnapshotDirectory(environment),
            CancellationToken.None);

        Assert.Equal("newer-id", latest.SourceId);
        Assert.Equal(ResolvedInputFormat.Dotx, latest.Format);
        Assert.Equal("older-id", explicitOlder.SourceId);
        Assert.Equal(ResolvedInputFormat.Docx, explicitOlder.Format);
        var otherUserError = await Assert.ThrowsAsync<WordMcpException>(() => resolver.SnapshotDocumentAsync(
            Caller,
            Scope,
            "other-id",
            NewSnapshotDirectory(environment),
            CancellationToken.None));
        Assert.Equal("input_file_not_found", otherUserError.Code);
    }

    [Theory]
    [InlineData("picture.png", ResolvedInputFormat.Png)]
    [InlineData("picture.jpeg", ResolvedInputFormat.Jpeg)]
    public async Task SnapshotsOnlyOpaquePngOrJpegImageIds(string originalName, ResolvedInputFormat expected)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var userDirectory = CreateUserDirectory(environment);
        const string fileId = "image-id";
        await File.WriteAllBytesAsync(
            Path.Combine(userDirectory, $"{fileId}__{originalName}"),
            expected == ResolvedInputFormat.Png ? DocxTestPackage.Png(20, 10) : DocxTestPackage.Jpeg(20, 10),
            TestContext.Current.CancellationToken);

        var snapshot = await CreateResolver(environment).SnapshotImageAsync(
            Caller,
            fileId,
            NewSnapshotDirectory(environment),
            CancellationToken.None);

        Assert.Equal(expected, snapshot.Format);
        Assert.True(File.Exists(snapshot.Path));
    }

    [Fact]
    public async Task RejectsSymlinksAndHardlinks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var userDirectory = CreateUserDirectory(environment);
        var outside = Path.Combine(environment.Root, "outside.docx");
        await File.WriteAllBytesAsync(outside, [1, 2, 3], TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(userDirectory, "symlink-id__file.docx"), outside);
        var hardlink = Path.Combine(userDirectory, "hardlink-id__file.docx");
        Assert.Equal(0, Link(outside, hardlink));
        var resolver = CreateResolver(environment);

        var symlinkError = await Assert.ThrowsAsync<WordMcpException>(() => resolver.SnapshotDocumentAsync(
            Caller,
            Scope,
            "symlink-id",
            NewSnapshotDirectory(environment),
            CancellationToken.None));
        var hardlinkError = await Assert.ThrowsAsync<WordMcpException>(() => resolver.SnapshotDocumentAsync(
            Caller,
            Scope,
            "hardlink-id",
            NewSnapshotDirectory(environment),
            CancellationToken.None));

        Assert.Equal("unsafe_input_file", symlinkError.Code);
        Assert.Equal("unsafe_input_file", hardlinkError.Code);
    }

    [Fact]
    public async Task ExpectedIdentityRejectsAReplacementBetweenSelectionAndCopy()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var userDirectory = CreateUserDirectory(environment);
        var source = Path.Combine(userDirectory, "race-id__file.docx");
        await File.WriteAllBytesAsync(source, [1, 2, 3], TestContext.Current.CancellationToken);
        var identity = LinuxFileIdentity.InspectUnderRoot(
            environment.Options.Value.LibreChatUploadsRoot,
            [Caller.UserId, Path.GetFileName(source)]);
        File.Delete(source);
        await File.WriteAllBytesAsync(source, [4, 5, 6, 7], TestContext.Current.CancellationToken);
        var destinationDirectory = NewSnapshotDirectory(environment);

        await Assert.ThrowsAsync<SafeFileOpenException>(() => LinuxFileIdentity.CopySnapshotAsync(
            environment.Options.Value.LibreChatUploadsRoot,
            [Caller.UserId, Path.GetFileName(source)],
            Path.Combine(destinationDirectory, "source.docx"),
            environment.Options.Value.MaxFileBytes,
            identity,
            CancellationToken.None));
    }

    [Fact]
    public async Task ResolvesDocumentArtifactsOnlyInsideTheOwningConversationAndJob()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var repository = new FileJobRepository(environment.Options);
        const string jobId = "job_abcdefghijklmnop";
        const string artifactId = "art_abcdefghijklmnop";
        var jobDirectory = repository.CreateJobDirectory(jobId);
        var documentPath = Path.Combine(jobDirectory, "report.docx");
        await File.WriteAllBytesAsync(documentPath, [1, 2, 3], TestContext.Current.CancellationToken);
        var now = environment.Time.GetUtcNow();
        var artifact = new ArtifactRecord(
            artifactId,
            "document",
            "report.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            documentPath,
            3,
            now,
            now.AddDays(1));
        var job = new WordJob(
            jobId,
            Scope.UserScope,
            Scope.ConversationScope,
            JobKind.Analyze,
            JobState.Succeeded,
            JsonSerializer.SerializeToElement(new { }),
            null,
            null,
            null,
            null,
            0,
            [],
            now,
            now,
            now.AddDays(1),
            new JobResult(Artifacts: [artifact]));
        await repository.CreateAsync(job, CancellationToken.None);
        var resolver = new InputFileResolver(environment.Options, repository);

        var wrongScope = await Assert.ThrowsAsync<WordMcpException>(() => resolver.SnapshotDocumentAsync(
            Caller,
            new CallerScope("other-user", "other-conversation"),
            artifactId,
            NewSnapshotDirectory(environment),
            CancellationToken.None));
        var snapshot = await resolver.SnapshotDocumentAsync(
            Caller,
            Scope,
            artifactId,
            NewSnapshotDirectory(environment),
            CancellationToken.None);

        Assert.Equal("input_file_not_found", wrongScope.Code);
        Assert.Equal(artifactId, snapshot.SourceId);
        Assert.Equal(ResolvedInputFormat.Docx, snapshot.Format);
    }

    [Theory]
    [InlineData("../document")]
    [InlineData("https://example.com/document.docx")]
    [InlineData("")]
    public async Task RejectsPathsUrlsAndEmptyIdentifiers(string value)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        CreateUserDirectory(environment);

        var error = await Assert.ThrowsAsync<WordMcpException>(() => CreateResolver(environment).SnapshotDocumentAsync(
            Caller,
            Scope,
            value,
            NewSnapshotDirectory(environment),
            CancellationToken.None));

        Assert.Equal("invalid_file_id", error.Code);
    }

    private static InputFileResolver CreateResolver(TestEnvironment environment) =>
        new(environment.Options, new FileJobRepository(environment.Options));

    private static string CreateUserDirectory(TestEnvironment environment)
    {
        var path = Path.Combine(environment.Options.Value.LibreChatUploadsRoot, Caller.UserId);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string NewSnapshotDirectory(TestEnvironment environment)
    {
        var path = Path.Combine(environment.Root, "snapshots", Guid.NewGuid().ToString("N"));
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
