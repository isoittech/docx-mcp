using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WordMcp.Artifacts;
using WordMcp.Domain;
using WordMcp.Jobs;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class RetentionPolicyTests
{
    [Fact]
    public void EffectiveJobExpiryUsesEarlierOfGenerationAndFirstDocumentDownload()
    {
        using var environment = new TestEnvironment();
        var policy = new RetentionPolicy(environment.Options);
        var created = environment.Time.GetUtcNow();
        var neverDownloaded = CreateJob(
            "job_retained0000000000000000",
            JobState.Succeeded,
            created.AddHours(-1),
            created.AddDays(6),
            [Artifact(created, null), PreviewArtifact(created)]);
        var downloaded = CreateJob(
            "job_downloaded00000000000000",
            JobState.Succeeded,
            created,
            created.AddDays(7),
            [Artifact(created, created.AddHours(2)), PreviewArtifact(created)]);
        var downloadedNearCreationExpiry = CreateJob(
            "job_nearexpiry00000000000000",
            JobState.Succeeded,
            created,
            created.AddDays(7),
            [Artifact(created, created.AddDays(6).AddHours(12)), PreviewArtifact(created)]);

        Assert.Equal(created.AddDays(7), policy.EffectiveJobExpiry(neverDownloaded));
        Assert.Equal(created.AddHours(26), policy.EffectiveJobExpiry(downloaded));
        Assert.Equal(created.AddDays(7), policy.EffectiveJobExpiry(downloadedNearCreationExpiry));
    }

    [Fact]
    public async Task MarkingDocumentDownloadDoesNotStartPreviewTimerAndIsIdempotent()
    {
        using var environment = new TestEnvironment();
        var jobs = new FileJobRepository(environment.Options);
        var policy = new RetentionPolicy(environment.Options);
        var artifactService = new ArtifactService(
            jobs,
            new ArtifactTokenService(environment.Options, environment.Time),
            policy,
            environment.Options,
            environment.Time);
        var now = environment.Time.GetUtcNow();
        var directory = jobs.CreateJobDirectory("job_download0000000000000000");
        var documentPath = Path.Combine(directory, "document.docx");
        var previewPath = Path.Combine(directory, "page-1.png");
        await File.WriteAllBytesAsync(documentPath, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(previewPath, [2], TestContext.Current.CancellationToken);
        var document = artifactService.CreateRecord("document", documentPath, "document.docx");
        var preview = artifactService.CreateRecord("preview", previewPath, "page-1.png");
        var job = CreateJob(
            "job_download0000000000000000",
            JobState.Succeeded,
            now,
            now.AddDays(7),
            [document, preview]);
        await jobs.CreateAsync(job, CancellationToken.None);

        await Task.WhenAll(
            artifactService.MarkDocumentDownloadedAsync(job.Id, document.ArtifactId, CancellationToken.None),
            artifactService.MarkDocumentDownloadedAsync(job.Id, document.ArtifactId, CancellationToken.None));
        var stored = await jobs.GetAsync(job.Id, CancellationToken.None);
        var storedDocument = Assert.Single(stored!.Result!.Artifacts!, item => item.Kind == "document");
        var storedPreview = Assert.Single(stored.Result.Artifacts!, item => item.Kind == "preview");

        Assert.Equal(now, storedDocument.FirstDownloadedAt);
        Assert.Null(storedPreview.FirstDownloadedAt);
        Assert.Equal(now.AddHours(24), policy.EffectiveJobExpiry(stored));

        environment.Time.Advance(TimeSpan.FromHours(24));
        Assert.Empty(artifactService.CreateLinks(stored));
    }

    [Fact]
    public async Task SweepRemovesExpiredTerminalDataButPreservesRunningJobs()
    {
        using var environment = new TestEnvironment();
        var jobs = new FileJobRepository(environment.Options);
        var drafts = new DraftRepository(environment.Options);
        var analyses = new AnalysisRepository(environment.Options);
        var policy = new RetentionPolicy(environment.Options);
        var now = environment.Time.GetUtcNow();
        var downloadedArtifact = Artifact(now.AddDays(-2), now.AddHours(-25));
        var expired = CreateJob(
            "job_expired00000000000000000",
            JobState.Succeeded,
            now.AddDays(-2),
            now.AddDays(5),
            [downloadedArtifact]);
        var running = CreateJob(
            "job_running00000000000000000",
            JobState.Running,
            now.AddDays(-8),
            now.AddMinutes(-1),
            []);
        await jobs.CreateAsync(expired, CancellationToken.None);
        await jobs.CreateAsync(running, CancellationToken.None);
        await drafts.SaveAsync(
            new DraftRecord(
                "draft_expired000000000000000",
                "user-scope",
                "conversation-scope",
                Definition(),
                now.AddHours(-2),
                now.AddMinutes(-1)),
            CancellationToken.None);
        await analyses.SaveAsync(Analysis(now.AddMinutes(-1)), CancellationToken.None);
        using var worker = new RetentionWorker(
            jobs,
            drafts,
            analyses,
            policy,
            environment.Time,
            NullLogger<RetentionWorker>.Instance);

        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Null(await jobs.GetAsync(expired.Id, CancellationToken.None));
        Assert.NotNull(await jobs.GetAsync(running.Id, CancellationToken.None));
        Assert.Empty(await drafts.ListAsync(CancellationToken.None));
        Assert.Empty(await analyses.ListAsync(CancellationToken.None));
    }

    private static ArtifactRecord Artifact(DateTimeOffset createdAt, DateTimeOffset? firstDownloadedAt) => new(
        "art_AAAAAAAAAAAAAAAAAAAAAAAA",
        "document",
        "document.docx",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "/internal/not-exposed/document.docx",
        1,
        createdAt,
        createdAt.AddDays(7),
        firstDownloadedAt);

    private static ArtifactRecord PreviewArtifact(DateTimeOffset createdAt) => new(
        "art_BBBBBBBBBBBBBBBBBBBBBBBB",
        "preview",
        "page-1.png",
        "image/png",
        "/internal/not-exposed/page-1.png",
        1,
        createdAt,
        createdAt.AddDays(7));

    private static WordJob CreateJob(
        string id,
        JobState state,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        IReadOnlyList<ArtifactRecord> artifacts) => new(
        id,
        "user-scope",
        "conversation-scope",
        JobKind.FinishDocument,
        state,
        JsonSerializer.SerializeToElement(new FinishDocumentPayload(Definition())),
        null,
        null,
        id,
        null,
        0,
        [],
        createdAt,
        createdAt,
        expiresAt,
        Result: new JobResult(Artifacts: artifacts),
        DocumentDefinition: Definition());

    private static AnalysisSnapshot Analysis(DateTimeOffset expiresAt)
    {
        const string id = "ana_AAAAAAAAAAAAAAAAAAAAAAAA";
        var summary = new AnalysisSummary(
            id,
            new string('a', 64),
            new Dictionary<string, string?>(),
            "ja-JP",
            0,
            0,
            1,
            ["main"],
            [],
            [],
            new Dictionary<string, int>(),
            expiresAt);
        return new AnalysisSnapshot(
            id,
            "user-scope",
            "conversation-scope",
            summary.SourceSha256,
            "/internal/source.docx",
            "source.docx",
            summary,
            new Dictionary<string, IReadOnlyList<AnalysisItem>>(),
            new Dictionary<string, TargetRecord>(),
            expiresAt.AddHours(-1),
            expiresAt);
    }

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
        [new LogicalSectionSpec(
            "section-1",
            "概要",
            [new DocumentBlock(DocumentBlockKind.Paragraph, Text: "本文")])]);
}
