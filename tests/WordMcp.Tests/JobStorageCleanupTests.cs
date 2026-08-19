using System.Text.Json;
using WordMcp.Domain;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class JobStorageCleanupTests
{
    [Fact]
    public async Task CleanupRemovesOnlyValidatedUnpublishedRunAndPreservesPublishedRun()
    {
        using var environment = new TestEnvironment();
        using var jobs = new FileJobRepository(environment.Options);
        const string jobId = "job_cleanupAAAAAAAAAAAAAAAAAA";
        const string publishedRunId = "run_PPPPPPPPPPPPPPPPPPPPPPPP";
        const string staleRunId = "run_SSSSSSSSSSSSSSSSSSSSSSSS";
        var jobDirectory = jobs.CreateJobDirectory(jobId);
        var runsDirectory = Path.Combine(jobDirectory, "runs");
        var publishedDirectory = Path.Combine(runsDirectory, publishedRunId);
        var staleDirectory = Path.Combine(runsDirectory, staleRunId);
        Directory.CreateDirectory(publishedDirectory);
        Directory.CreateDirectory(staleDirectory);
        var publishedPath = Path.Combine(publishedDirectory, "document.docx");
        await File.WriteAllBytesAsync(publishedPath, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(staleDirectory, "partial.bin"),
            [2],
            TestContext.Current.CancellationToken);
        var now = environment.Time.GetUtcNow();
        var artifact = new ArtifactRecord(
            "art_AAAAAAAAAAAAAAAAAAAAAAAA",
            "document",
            "document.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            publishedPath,
            1,
            now,
            now.AddDays(7));
        await jobs.CreateAsync(
            CreateJob(
                jobId,
                JobState.Succeeded,
                now,
                new JobResult(Artifacts: [artifact]),
                publishedRunId),
            TestContext.Current.CancellationToken);

        var deleted = await jobs.CleanupUnpublishedRunsAsync(jobId, TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.True(Directory.Exists(publishedDirectory));
        Assert.True(File.Exists(publishedPath));
        Assert.False(Directory.Exists(staleDirectory));
    }

    [Fact]
    public async Task CleanupRejectsLinkedRunWithoutTouchingItsTarget()
    {
        using var environment = new TestEnvironment();
        using var jobs = new FileJobRepository(environment.Options);
        const string jobId = "job_cleanupBBBBBBBBBBBBBBBBBB";
        const string linkedRunId = "run_LLLLLLLLLLLLLLLLLLLLLLLL";
        var jobDirectory = jobs.CreateJobDirectory(jobId);
        var runsDirectory = Path.Combine(jobDirectory, "runs");
        Directory.CreateDirectory(runsDirectory);
        var outsideDirectory = Path.Combine(environment.Root, "outside-run-target");
        Directory.CreateDirectory(outsideDirectory);
        var marker = Path.Combine(outsideDirectory, "keep.bin");
        await File.WriteAllBytesAsync(marker, [3], TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(Path.Combine(runsDirectory, linkedRunId), outsideDirectory);
        await jobs.CreateAsync(
            CreateJob(jobId, JobState.Failed, environment.Time.GetUtcNow(), result: null, publishedRunId: null),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            jobs.CleanupUnpublishedRunsAsync(jobId, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(marker));
        Assert.True(Directory.Exists(outsideDirectory));
    }

    [Fact]
    public async Task CleanupAfterRepositoryReopenRemovesFailedJobRun()
    {
        using var environment = new TestEnvironment();
        const string jobId = "job_cleanupCCCCCCCCCCCCCCCCCC";
        const string failedRunId = "run_FFFFFFFFFFFFFFFFFFFFFFFF";
        string failedRunDirectory;
        using (var writer = new FileJobRepository(environment.Options))
        {
            var jobDirectory = writer.CreateJobDirectory(jobId);
            failedRunDirectory = Path.Combine(jobDirectory, "runs", failedRunId);
            Directory.CreateDirectory(failedRunDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(failedRunDirectory, "partial.bin"),
                [4],
                TestContext.Current.CancellationToken);
            await writer.CreateAsync(
                CreateJob(jobId, JobState.Failed, environment.Time.GetUtcNow(), result: null, publishedRunId: null),
                TestContext.Current.CancellationToken);
        }

        using var restarted = new FileJobRepository(environment.Options);
        var deleted = await restarted.CleanupUnpublishedRunsAsync(
            jobId,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(failedRunDirectory));
        Assert.NotNull(await restarted.GetAsync(jobId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingArtifactFailsOnlyExactHydrationAndDoesNotPoisonBroadConsumers()
    {
        using var environment = new TestEnvironment();
        using var jobs = new FileJobRepository(environment.Options);
        const string brokenJobId = "job_missingAAAAAAAAAAAAAAAAAA";
        const string healthyJobId = "job_healthyBBBBBBBBBBBBBBBBBB";
        const string publishedRunId = "run_MMMMMMMMMMMMMMMMMMMMMMMM";
        var publishedDirectory = Path.Combine(
            jobs.CreateJobDirectory(brokenJobId),
            "runs",
            publishedRunId);
        Directory.CreateDirectory(publishedDirectory);
        var artifactPath = Path.Combine(publishedDirectory, "document.docx");
        await File.WriteAllBytesAsync(artifactPath, [1], TestContext.Current.CancellationToken);
        var now = environment.Time.GetUtcNow();
        var artifact = new ArtifactRecord(
            "art_MMMMMMMMMMMMMMMMMMMMMMMM",
            "document",
            "document.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            artifactPath,
            1,
            now,
            now.AddDays(7));
        await jobs.CreateAsync(
            CreateJob(
                brokenJobId,
                JobState.Succeeded,
                now,
                new JobResult(Artifacts: [artifact]),
                publishedRunId),
            TestContext.Current.CancellationToken);
        await jobs.CreateAsync(
            CreateJob(healthyJobId, JobState.Queued, now.AddMinutes(1), result: null, publishedRunId: null),
            TestContext.Current.CancellationToken);
        File.Delete(artifactPath);

        var listed = await jobs.ListAsync(TestContext.Current.CancellationToken);
        var latest = await jobs.LatestAsync(
            new CallerScope("user-scope", "conversation-scope"),
            successfulDeclarativeOnly: false,
            TestContext.Current.CancellationToken);
        var cleaned = await jobs.CleanupUnpublishedRunsAsync(
            brokenJobId,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, listed.Count);
        Assert.Equal(healthyJobId, latest?.Id);
        Assert.Equal(0, cleaned);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            jobs.GetAsync(brokenJobId, TestContext.Current.CancellationToken));
        Assert.NotNull(await jobs.GetAsync(healthyJobId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupDoesNotDeleteUnexpectedRunsChild()
    {
        using var environment = new TestEnvironment();
        using var jobs = new FileJobRepository(environment.Options);
        const string jobId = "job_cleanupDDDDDDDDDDDDDDDDDD";
        var unexpectedDirectory = Path.Combine(
            jobs.CreateJobDirectory(jobId),
            "runs",
            "operator-notes");
        Directory.CreateDirectory(unexpectedDirectory);
        var marker = Path.Combine(unexpectedDirectory, "keep.bin");
        await File.WriteAllBytesAsync(marker, [5], TestContext.Current.CancellationToken);
        await jobs.CreateAsync(
            CreateJob(jobId, JobState.Failed, environment.Time.GetUtcNow(), result: null, publishedRunId: null),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            jobs.CleanupUnpublishedRunsAsync(jobId, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(marker));
    }

    private static WordJob CreateJob(
        string id,
        JobState state,
        DateTimeOffset now,
        JobResult? result,
        string? publishedRunId) => new(
        id,
        "user-scope",
        "conversation-scope",
        JobKind.FinishDocument,
        state,
        JsonSerializer.SerializeToElement(new FinishDocumentPayload(Definition()), JsonFileStore.Options),
        null,
        null,
        id,
        null,
        0,
        [],
        now,
        now,
        now.AddDays(7),
        Result: result,
        DocumentDefinition: Definition(),
        PublishedRunId: publishedRunId);

    private static DocumentDefinition Definition() => new(
        "報告書",
        "共有",
        "関係者",
        null,
        "ja-JP",
        1,
        "none",
        new DocumentLayoutSpec(),
        new DocumentThemeSpec(),
        new DocumentDesignSpec(),
        new HeaderFooterPolicy(),
        []);
}
