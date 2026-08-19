using System.Text.Json;
using Microsoft.Extensions.Options;
using WordMcp.Artifacts;
using WordMcp.Domain;
using WordMcp.Drafts;
using WordMcp.Jobs;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class JobServiceTests
{
    [Fact]
    public async Task SubmissionCreatesImmutableSnapshotBeforeReturningReceiptAndBoundsQueue()
    {
        using var environment = new TestEnvironment();
        using var fixture = new ServiceFixture(environment);
        using var document = DocxTestPackage.Create();
        var caller = new CallerContext("user-a", "conversation-a", null);
        var uploadDirectory = Path.Combine(environment.Root, "uploads", caller.UserId);
        Directory.CreateDirectory(uploadDirectory);
        File.Copy(document.Path, Path.Combine(uploadDirectory, "fileA__report.docx"));

        var receipts = new List<JobReceipt>();
        for (var index = 0; index < environment.Options.Value.MaxQueueDepth; index++)
        {
            receipts.Add(await fixture.Service.SubmitAnalyzeAsync(caller, "fileA", CancellationToken.None));
        }

        var first = await fixture.Jobs.GetAsync(receipts[0].JobId, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(JobState.Queued, first.State);
        Assert.NotNull(first.InputPath);
        Assert.True(File.Exists(first.InputPath));
        Assert.True(first.ReservedBytes > environment.Options.Value.MaxFileBytes);
        Assert.StartsWith(
            Path.GetFullPath(fixture.Jobs.GetJobDirectory(first.Id)) + Path.DirectorySeparatorChar,
            Path.GetFullPath(first.InputPath),
            StringComparison.Ordinal);
        Assert.Equal(45, receipts[0].RecommendedWaitSeconds);

        var full = await Assert.ThrowsAsync<WordMcpException>(() =>
            fixture.Service.SubmitAnalyzeAsync(caller, "fileA", CancellationToken.None));
        Assert.Equal("queue_full", full.Code);
        Assert.Equal(environment.Options.Value.MaxQueueDepth, (await fixture.Jobs.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task LatestJobAndExplicitOwnershipAreConversationScoped()
    {
        using var environment = new TestEnvironment();
        using var fixture = new ServiceFixture(environment);
        var owner = new CallerContext("user-a", "conversation-a", null);
        var otherConversation = new CallerContext("user-a", "conversation-b", null);
        var ownerScope = fixture.Scopes.Create(owner);
        var otherScope = fixture.Scopes.Create(otherConversation);
        var older = CreateJob("job_AAAAAAAAAAAAAAAAAAAAAAAA", ownerScope, JobState.Succeeded, environment.Time.GetUtcNow());
        var latest = CreateJob(
            "job_BBBBBBBBBBBBBBBBBBBBBBBB",
            ownerScope,
            JobState.Running,
            environment.Time.GetUtcNow().AddMinutes(1));
        var foreign = CreateJob(
            "job_CCCCCCCCCCCCCCCCCCCCCCCC",
            otherScope,
            JobState.Succeeded,
            environment.Time.GetUtcNow().AddMinutes(2));
        await fixture.Jobs.CreateAsync(older, CancellationToken.None);
        await fixture.Jobs.CreateAsync(latest, CancellationToken.None);
        await fixture.Jobs.CreateAsync(foreign, CancellationToken.None);

        var view = await fixture.Service.GetAsync(owner, "latest", CancellationToken.None);
        Assert.Equal(latest.Id, view.JobId);
        Assert.Equal("running", view.Status);

        var hidden = await Assert.ThrowsAsync<WordMcpException>(() =>
            fixture.Service.GetAsync(otherConversation, latest.Id, CancellationToken.None));
        Assert.Equal("job_not_found", hidden.Code);
    }

    [Fact]
    public async Task WaitResolvesLatestToConcreteJobOnlyOnce()
    {
        using var environment = new TestEnvironment();
        using var fixture = new ServiceFixture(environment);
        var caller = new CallerContext("user-a", "conversation-a", null);
        var scope = fixture.Scopes.Create(caller);
        var initial = CreateJob(
            "job_DDDDDDDDDDDDDDDDDDDDDDDD",
            scope,
            JobState.Running,
            environment.Time.GetUtcNow());
        await fixture.Jobs.CreateAsync(initial, CancellationToken.None);

        var wait = fixture.Service.WaitAsync(
            caller,
            "latest",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var newer = CreateJob(
            "job_EEEEEEEEEEEEEEEEEEEEEEEE",
            scope,
            JobState.Succeeded,
            environment.Time.GetUtcNow().AddMinutes(1));
        await fixture.Jobs.CreateAsync(newer, CancellationToken.None);
        await fixture.Jobs.UpdateAsync(
            initial.Id,
            current => current with
            {
                State = JobState.Succeeded,
                UpdatedAt = environment.Time.GetUtcNow(),
            },
            CancellationToken.None);

        var result = await wait;
        Assert.Equal(initial.Id, result.JobId);
        Assert.Equal("succeeded", result.Status);
    }

    [Fact]
    public async Task CancellationIsAtomicAndRequiresConcreteOwnedIdentifier()
    {
        using var environment = new TestEnvironment();
        using var fixture = new ServiceFixture(environment);
        var caller = new CallerContext("user-a", "conversation-a", null);
        var scope = fixture.Scopes.Create(caller);
        var job = CreateJob(
            "job_FFFFFFFFFFFFFFFFFFFFFFFF",
            scope,
            JobState.Queued,
            environment.Time.GetUtcNow());
        await fixture.Jobs.CreateAsync(job, CancellationToken.None);

        var latestError = await Assert.ThrowsAsync<WordMcpException>(() =>
            fixture.Service.CancelAsync(caller, "latest", CancellationToken.None));
        Assert.Equal("concrete_job_id_required", latestError.Code);
        var previewLatestError = await Assert.ThrowsAsync<WordMcpException>(() =>
            fixture.Service.GetPreviewImagesAsync(caller, "latest", [1], CancellationToken.None));
        Assert.Equal("concrete_job_id_required", previewLatestError.Code);

        var first = await fixture.Service.CancelAsync(caller, job.Id, CancellationToken.None);
        var second = await fixture.Service.CancelAsync(caller, job.Id, CancellationToken.None);
        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal("canceled", first.Status);
        var stored = await fixture.Jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.Equal(JobState.Canceled, stored?.State);
        Assert.Equal("canceled_by_user", stored?.Error?.Code);
    }

    [Fact]
    public async Task RepeatedFinishReturnsTheOriginalJobWithoutCreatingDuplicates()
    {
        using var environment = new TestEnvironment();
        using var fixture = new ServiceFixture(environment);
        var caller = new CallerContext("user-a", "conversation-a", null);
        var definition = new DocumentDefinition(
            "業務報告書",
            "状況を共有する",
            "関係者",
            "週次報告",
            "ja-JP",
            1,
            "none",
            new DocumentLayoutSpec(),
            new DocumentThemeSpec(),
            new DocumentDesignSpec(),
            new HeaderFooterPolicy(),
            []);
        var draft = await fixture.Drafts.StartAsync(caller, definition, false, CancellationToken.None);
        await fixture.Drafts.AddSectionsAsync(
            caller,
            draft.DraftId,
            1,
            [new LogicalSectionSpec(
                "summary",
                "概要",
                [new DocumentBlock(DocumentBlockKind.Paragraph, Text: "本文です。")])],
            CancellationToken.None);

        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fixture.Service.SubmitFinishDocumentAsync(caller, draft.DraftId, CancellationToken.None)));

        var jobId = Assert.Single(receipts.Select(receipt => receipt.JobId).Distinct(StringComparer.Ordinal));
        Assert.All(receipts, receipt =>
        {
            Assert.Equal("queued", receipt.Status);
            Assert.Equal("word_wait_for_job", receipt.NextTool);
        });
        var stored = Assert.Single(await fixture.Jobs.ListAsync(CancellationToken.None));
        Assert.Equal(jobId, stored.Id);
        Assert.Equal(draft.DraftId, stored.DraftId);

        await fixture.Jobs.UpdateAsync(
            jobId,
            current => current with
            {
                State = JobState.Succeeded,
                UpdatedAt = environment.Time.GetUtcNow(),
            },
            CancellationToken.None);
        var completedReplay = await fixture.Service.SubmitFinishDocumentAsync(
            caller,
            draft.DraftId,
            CancellationToken.None);
        Assert.Equal(jobId, completedReplay.JobId);
        Assert.Equal("succeeded", completedReplay.Status);
        Assert.Equal(0, completedReplay.RecommendedWaitSeconds);
        Assert.Equal("word_get_preview_images", completedReplay.NextTool);
        Assert.Single(await fixture.Jobs.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulDeclarativeJobViewReturnsExactSectionKeys()
    {
        using var environment = new TestEnvironment();
        using var fixture = new ServiceFixture(environment);
        var caller = new CallerContext("user-a", "conversation-a", null);
        var scope = fixture.Scopes.Create(caller);
        var now = environment.Time.GetUtcNow();
        var definition = new DocumentDefinition(
            "週次報告書",
            "進捗を共有する",
            "プロジェクト責任者",
            "Aurora",
            "ja-JP",
            2,
            "none",
            new DocumentLayoutSpec(),
            new DocumentThemeSpec(),
            new DocumentDesignSpec(),
            new HeaderFooterPolicy(),
            [
                new LogicalSectionSpec(
                    "executive-summary",
                    "エグゼクティブサマリー",
                    [new DocumentBlock(DocumentBlockKind.Paragraph, Text: "概要です。")]),
                new LogicalSectionSpec(
                    "risks",
                    "課題とリスク",
                    [new DocumentBlock(DocumentBlockKind.Paragraph, Text: "リスクです。")]),
            ]);
        var job = new WordJob(
            "job_sections00000000000000000",
            scope.UserScope,
            scope.ConversationScope,
            JobKind.FinishDocument,
            JobState.Succeeded,
            JsonSerializer.SerializeToElement(new FinishDocumentPayload(definition), JsonFileStore.Options),
            null,
            null,
            "job_sections00000000000000000",
            null,
            0,
            [],
            now,
            now,
            now.AddDays(7),
            Result: new JobResult(PageCount: 3),
            DocumentDefinition: definition);
        await fixture.Jobs.CreateAsync(job, TestContext.Current.CancellationToken);

        var view = await fixture.Service.GetAsync(caller, job.Id, TestContext.Current.CancellationToken);

        Assert.Equal(["executive-summary", "risks"], view.Result?.SectionKeys);
    }

    [Fact]
    public async Task RefinementLineageRejectsOldBranchesAndAThirdAutomaticRound()
    {
        using var environment = new TestEnvironment();
        using var fixture = new ServiceFixture(environment);
        var caller = new CallerContext("user-a", "conversation-a", null);
        var scope = fixture.Scopes.Create(caller);
        var rootId = "job_lineage00000000000000000";
        var now = environment.Time.GetUtcNow();
        var definition = new DocumentDefinition(
            "週次報告書",
            "進捗を共有する",
            "プロジェクト責任者",
            "架空案件",
            "ja-JP",
            1,
            "none",
            new DocumentLayoutSpec(),
            new DocumentThemeSpec(),
            new DocumentDesignSpec(),
            new HeaderFooterPolicy(),
            [new LogicalSectionSpec(
                "summary",
                "概要",
                [new DocumentBlock(DocumentBlockKind.Paragraph, Text: "初版です。")])]);
        var root = new WordJob(
            rootId,
            scope.UserScope,
            scope.ConversationScope,
            JobKind.FinishDocument,
            JobState.Succeeded,
            JsonSerializer.SerializeToElement(new FinishDocumentPayload(definition), JsonFileStore.Options),
            null,
            null,
            rootId,
            null,
            0,
            [],
            now,
            now,
            now.AddDays(7),
            Result: new JobResult(PageCount: 2),
            DocumentDefinition: definition);
        var rootDirectory = fixture.Jobs.CreateJobDirectory(rootId);
        await JsonFileStore.WriteAtomicAsync(
            Path.Combine(rootDirectory, "generation-inputs.json"),
            new GenerationJobPayload(definition, null, []),
            TestContext.Current.CancellationToken);
        await fixture.Jobs.CreateAsync(root, TestContext.Current.CancellationToken);

        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var firstReceipt = await fixture.Service.SubmitRefineSectionAsync(
            caller,
            rootId,
            Replacement("第1巡です。"),
            userRequestedEdit: false,
            TestContext.Current.CancellationToken);
        var first = await fixture.Jobs.GetAsync(firstReceipt.JobId, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Equal(rootId, first.RootJobId);
        Assert.Equal(rootId, first.ParentJobId);
        Assert.Equal(1, first.RevisionRound);
        Assert.Equal(["summary"], first.RevisedSections);
        await MarkSucceededAsync(fixture.Jobs, first.Id, environment.Time.GetUtcNow());

        var staleRoot = await Assert.ThrowsAsync<WordMcpException>(() =>
            fixture.Service.SubmitRefineSectionAsync(
                caller,
                rootId,
                Replacement("旧枝からの分岐です。"),
                userRequestedEdit: false,
                TestContext.Current.CancellationToken));
        Assert.Equal("document_job_superseded", staleRoot.Code);

        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var secondReceipt = await fixture.Service.SubmitRefineSectionAsync(
            caller,
            first.Id,
            Replacement("第2巡です。"),
            userRequestedEdit: false,
            TestContext.Current.CancellationToken);
        var second = await fixture.Jobs.GetAsync(secondReceipt.JobId, TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        Assert.Equal(rootId, second.RootJobId);
        Assert.Equal(first.Id, second.ParentJobId);
        Assert.Equal(2, second.RevisionRound);
        Assert.Equal(["summary"], second.RevisedSections);
        await MarkSucceededAsync(fixture.Jobs, second.Id, environment.Time.GetUtcNow());

        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var thirdRound = await Assert.ThrowsAsync<WordMcpException>(() =>
            fixture.Service.SubmitRefineSectionAsync(
                caller,
                second.Id,
                Replacement("第3巡は禁止です。"),
                userRequestedEdit: false,
                TestContext.Current.CancellationToken));
        Assert.Equal("section_refinement_limit_reached", thirdRound.Code);
        Assert.Equal(3, (await fixture.Jobs.ListAsync(TestContext.Current.CancellationToken)).Count);

        static LogicalSectionSpec Replacement(string text) => new(
            "summary",
            "概要",
            [new DocumentBlock(DocumentBlockKind.Paragraph, Text: text)]);
    }

    private static Task<WordJob> MarkSucceededAsync(
        FileJobRepository jobs,
        string jobId,
        DateTimeOffset now) => jobs.UpdateAsync(
        jobId,
        current => current with
        {
            State = JobState.Succeeded,
            Result = new JobResult(PageCount: 2),
            UpdatedAt = now,
        },
        TestContext.Current.CancellationToken);

    private static WordJob CreateJob(
        string id,
        CallerScope scope,
        JobState state,
        DateTimeOffset createdAt) => new(
        id,
        scope.UserScope,
        scope.ConversationScope,
        JobKind.Analyze,
        state,
        JsonSerializer.SerializeToElement(new AnalyzePayload("source")),
        null,
        null,
        null,
        null,
        0,
        [],
        createdAt,
        createdAt,
        createdAt.AddDays(7));

    private sealed class ServiceFixture : IDisposable
    {
        private readonly TemplateRegistry templates;

        public ServiceFixture(TestEnvironment environment)
        {
            Jobs = new FileJobRepository(environment.Options);
            var draftRepository = new DraftRepository(environment.Options);
            var analysisRepository = new AnalysisRepository(environment.Options);
            Scopes = new ScopeIdService(environment.Options);
            var validator = new DocumentSpecValidator(environment.Options);
            var quota = new StorageQuotaService(
                Jobs,
                draftRepository,
                analysisRepository,
                environment.Options,
                environment.Time);
            Drafts = new DraftService(
                draftRepository,
                Jobs,
                Scopes,
                validator,
                quota,
                environment.Options,
                environment.Time);
            var guard = new DocxPackageGuard(environment.Options);
            templates = new TemplateRegistry(environment.Options, guard);
            var retention = new RetentionPolicy(environment.Options);
            var tokenService = new ArtifactTokenService(environment.Options, environment.Time);
            var artifactService = new ArtifactService(
                Jobs,
                tokenService,
                retention,
                environment.Options,
                environment.Time);
            Service = new JobService(
                Jobs,
                new InputFileResolver(environment.Options, Jobs),
                templates,
                Drafts,
                analysisRepository,
                Scopes,
                validator,
                quota,
                new JobChannel(environment.Options),
                new JobCancellationRegistry(),
                artifactService,
                environment.Options,
                environment.Time);
        }

        public FileJobRepository Jobs { get; }

        public ScopeIdService Scopes { get; }

        public DraftService Drafts { get; }

        public JobService Service { get; }

        public void Dispose()
        {
            Service.Dispose();
            templates.Dispose();
        }
    }
}
