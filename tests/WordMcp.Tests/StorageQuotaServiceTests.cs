using System.Text.Json;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class StorageQuotaServiceTests
{
    [Fact]
    public async Task AnalysisCacheHitIsScopeBoundAndUpdatesLastAccess()
    {
        using var environment = new TestEnvironment();
        var owner = new CallerScope("user-a", "conversation-a");
        var cached = CreateCache(owner, new string('a', 64), environment.Time.GetUtcNow());
        using (var writer = new AnalysisRepository(environment.Options))
        {
            await writer.SaveCacheAsync(cached, TestContext.Current.CancellationToken);
        }

        environment.Time.Advance(TimeSpan.FromMinutes(5));
        using var repository = new AnalysisRepository(environment.Options);
        var hit = await repository.TryGetCacheAsync(
            owner,
            cached.SourceSha256,
            environment.Time,
            TestContext.Current.CancellationToken);
        var otherConversation = await repository.TryGetCacheAsync(
            owner with { ConversationScope = "conversation-b" },
            cached.SourceSha256,
            environment.Time,
            TestContext.Current.CancellationToken);
        var otherUser = await repository.TryGetCacheAsync(
            owner with { UserScope = "user-b" },
            cached.SourceSha256,
            environment.Time,
            TestContext.Current.CancellationToken);

        Assert.NotNull(hit);
        Assert.Equal(environment.Time.GetUtcNow(), hit.LastAccessedAt);
        Assert.Null(otherConversation);
        Assert.Null(otherUser);
    }

    [Fact]
    public async Task ExpiredOrInvalidatedAnalysisCacheIsNeverReused()
    {
        using var environment = new TestEnvironment();
        using var repository = new AnalysisRepository(environment.Options);
        var scope = new CallerScope("user-a", "conversation-a");
        var expired = CreateCache(scope, new string('b', 64), environment.Time.GetUtcNow());
        await repository.SaveCacheAsync(expired, TestContext.Current.CancellationToken);
        environment.Time.Advance(TimeSpan.FromMinutes(61));

        Assert.Null(await repository.TryGetCacheAsync(
            scope,
            expired.SourceSha256,
            environment.Time,
            TestContext.Current.CancellationToken));

        var current = CreateCache(scope, new string('c', 64), environment.Time.GetUtcNow());
        await repository.SaveCacheAsync(current, TestContext.Current.CancellationToken);
        await repository.SaveAsync(
            new AnalysisSnapshot(
                current.Summary.AnalysisId,
                scope.UserScope,
                scope.ConversationScope,
                current.SourceSha256,
                "/internal/source.docx",
                "source.docx",
                current.Summary,
                current.Items,
                current.Targets,
                current.CreatedAt,
                current.ExpiresAt,
                LastAccessedAt: current.CreatedAt),
            TestContext.Current.CancellationToken);
        await repository.InvalidateAsync(
            current.Summary.AnalysisId,
            environment.Time.GetUtcNow(),
            TestContext.Current.CancellationToken);
        Assert.Null(await repository.TryGetCacheAsync(
            scope,
            current.SourceSha256,
            environment.Time,
            TestContext.Current.CancellationToken));
        Assert.Empty(await repository.ListCacheAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConversationCountLimitEvictsLeastRecentlyUsedRecordInThatConversation()
    {
        using var environment = new TestEnvironment();
        var options = CreateQuotaOptions(environment, maxConversationItems: 2);
        using var drafts = new DraftRepository(options);
        using var analyses = new AnalysisRepository(options);
        using var jobs = new FileJobRepository(options);
        using var quota = new StorageQuotaService(jobs, drafts, analyses, options, environment.Time);
        var scope = new CallerScope("user-a", "conversation-a");
        var first = CreateDraft("draft_AAAAAAAAAAAAAAAAAAAAAAAA", scope, environment.Time.GetUtcNow());
        await StoreDraftAsync(quota, drafts, first);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var second = CreateDraft("draft_BBBBBBBBBBBBBBBBBBBBBBBB", scope, environment.Time.GetUtcNow());
        await StoreDraftAsync(quota, drafts, second);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        _ = await drafts.GetOwnedAsync(scope, first.Id, environment.Time, TestContext.Current.CancellationToken);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var third = CreateDraft("draft_CCCCCCCCCCCCCCCCCCCCCCCC", scope, environment.Time.GetUtcNow());

        await StoreDraftAsync(quota, drafts, third);

        Assert.Equal(
            [first.Id, third.Id],
            (await drafts.ListAsync(TestContext.Current.CancellationToken))
            .Select(item => item.Id)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ByteLimitEvictsReusableDataBeforeRejectingNewStorage()
    {
        using var environment = new TestEnvironment();
        var scope = new CallerScope("user-a", "conversation-a");
        var first = CreateDraft("draft_DDDDDDDDDDDDDDDDDDDDDDDD", scope, environment.Time.GetUtcNow());
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var second = CreateDraft("draft_EEEEEEEEEEEEEEEEEEEEEEEE", scope, environment.Time.GetUtcNow());
        var twoDraftBytes = SerializedBytes(first) + SerializedBytes(second);
        var options = CreateQuotaOptions(environment, maxConversationBytes: twoDraftBytes);
        using var drafts = new DraftRepository(options);
        using var analyses = new AnalysisRepository(options);
        using var jobs = new FileJobRepository(options);
        using var quota = new StorageQuotaService(jobs, drafts, analyses, options, environment.Time);
        await StoreDraftAsync(quota, drafts, first);
        await StoreDraftAsync(quota, drafts, second);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var third = CreateDraft("draft_FFFFFFFFFFFFFFFFFFFFFFFF", scope, environment.Time.GetUtcNow());

        await StoreDraftAsync(quota, drafts, third);

        var stored = await drafts.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, stored.Count);
        Assert.DoesNotContain(stored, item => item.Id == first.Id);
        Assert.Contains(stored, item => item.Id == second.Id);
        Assert.Contains(stored, item => item.Id == third.Id);
    }

    [Fact]
    public async Task JobReservationEvictsLeastRecentlyUsedAnalysisCacheFirst()
    {
        using var environment = new TestEnvironment();
        var options = CreateQuotaOptions(
            environment,
            maxConversationItems: 2,
            maxUserItems: 2,
            maxTotalItems: 2);
        using var drafts = new DraftRepository(options);
        using var analyses = new AnalysisRepository(options);
        using var jobs = new FileJobRepository(options);
        using var quota = new StorageQuotaService(jobs, drafts, analyses, options, environment.Time);
        var scope = new CallerScope("user-a", "conversation-a");
        var older = CreateCache(scope, new string('d', 64), environment.Time.GetUtcNow());
        await analyses.SaveCacheAsync(older, TestContext.Current.CancellationToken);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var newer = CreateCache(scope, new string('e', 64), environment.Time.GetUtcNow());
        await analyses.SaveCacheAsync(newer, TestContext.Current.CancellationToken);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        _ = await analyses.TryGetCacheAsync(
            scope,
            older.SourceSha256,
            environment.Time,
            TestContext.Current.CancellationToken);

        await quota.EnsureCanCreateAsync(scope, TestContext.Current.CancellationToken);

        var remaining = Assert.Single(await analyses.ListCacheAsync(TestContext.Current.CancellationToken));
        Assert.Equal(older.Id, remaining.Id);
    }

    [Fact]
    public async Task AnalysisGetRefreshesLruWithoutExtendingTtl()
    {
        using var environment = new TestEnvironment();
        var options = CreateQuotaOptions(
            environment,
            maxConversationItems: 2,
            maxUserItems: 2,
            maxTotalItems: 2);
        using var drafts = new DraftRepository(options);
        using var analyses = new AnalysisRepository(options);
        using var jobs = new FileJobRepository(options);
        using var quota = new StorageQuotaService(jobs, drafts, analyses, options, environment.Time);
        var scope = new CallerScope("user-a", "conversation-a");
        var first = CreateAnalysis(
            "ana_LLLLLLLLLLLLLLLLLLLLLLLL",
            scope,
            new string('1', 64),
            environment.Time.GetUtcNow());
        await StoreAnalysisAsync(quota, analyses, first);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var second = CreateAnalysis(
            "ana_MMMMMMMMMMMMMMMMMMMMMMMM",
            scope,
            new string('2', 64),
            environment.Time.GetUtcNow());
        await StoreAnalysisAsync(quota, analyses, second);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var accessed = await analyses.GetOwnedAsync(
            scope,
            first.Id,
            environment.Time,
            TestContext.Current.CancellationToken);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var third = CreateAnalysis(
            "ana_NNNNNNNNNNNNNNNNNNNNNNNN",
            scope,
            new string('3', 64),
            environment.Time.GetUtcNow());

        await StoreAnalysisAsync(quota, analyses, third);

        Assert.Equal(first.ExpiresAt, accessed.ExpiresAt);
        Assert.Equal(
            [first.Id, third.Id],
            (await analyses.ListAsync(TestContext.Current.CancellationToken))
            .Select(item => item.Id)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task JobAndArtifactBytesRemainNonEvictableAfterReusableDataIsExhausted()
    {
        using var environment = new TestEnvironment();
        var options = CreateQuotaOptions(environment, maxConversationBytes: 1_024);
        using var drafts = new DraftRepository(options);
        using var analyses = new AnalysisRepository(options);
        using var jobs = new FileJobRepository(options);
        using var quota = new StorageQuotaService(jobs, drafts, analyses, options, environment.Time);
        var scope = new CallerScope("user-a", "conversation-a");
        var jobId = "job_OOOOOOOOOOOOOOOOOOOOOOOO";
        var directory = jobs.CreateJobDirectory(jobId);
        var artifactPath = Path.Combine(directory, "artifact.bin");
        await File.WriteAllBytesAsync(
            artifactPath,
            new byte[2_048],
            TestContext.Current.CancellationToken);
        var now = environment.Time.GetUtcNow();
        await jobs.CreateAsync(
            new WordJob(
                jobId,
                scope.UserScope,
                scope.ConversationScope,
                JobKind.Analyze,
                JobState.Succeeded,
                JsonSerializer.SerializeToElement(new AnalyzePayload("source"), JsonFileStore.Options),
                null,
                null,
                null,
                null,
                0,
                [],
                now,
                now,
                now.AddDays(7)),
            TestContext.Current.CancellationToken);
        var cached = CreateCache(scope, new string('f', 64), now);
        await analyses.SaveCacheAsync(cached, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<WordMcpException>(() =>
            quota.EnsureCanCreateAsync(scope, TestContext.Current.CancellationToken));

        Assert.Equal("storage_quota_exceeded", error.Code);
        Assert.True(File.Exists(artifactPath));
        Assert.NotNull(await jobs.GetAsync(jobId, TestContext.Current.CancellationToken));
        Assert.Empty(await analyses.ListCacheAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentQueuedJobsAtomicallyPersistOnlyCapacityBackedReservations()
    {
        using var environment = new TestEnvironment();
        var options = CreateQuotaOptions(environment, maxConversationBytes: 50L * 1024 * 1024);
        using var drafts = new DraftRepository(options);
        using var analyses = new AnalysisRepository(options);
        using var jobs = new FileJobRepository(options);
        using var quota = new StorageQuotaService(jobs, drafts, analyses, options, environment.Time);
        var scope = new CallerScope("user-a", "conversation-a");
        var reservation = quota.GetJobReservationBytes(JobKind.Analyze);
        var first = CreateReservedJob(
            "job_PPPPPPPPPPPPPPPPPPPPPPPP",
            scope,
            reservation,
            environment.Time.GetUtcNow());
        var second = CreateReservedJob(
            "job_QQQQQQQQQQQQQQQQQQQQQQQQ",
            scope,
            reservation,
            environment.Time.GetUtcNow());

        var attempts = await Task.WhenAll(TryStoreJobAsync(first), TryStoreJobAsync(second));

        Assert.Single(attempts, static accepted => accepted);
        Assert.Single(attempts, static accepted => !accepted);
        var active = Assert.Single(await jobs.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(reservation, active.ReservedBytes);

        await jobs.UpdateAsync(
            active.Id,
            current => current with
            {
                State = JobState.Succeeded,
                UpdatedAt = environment.Time.GetUtcNow(),
            },
            TestContext.Current.CancellationToken);
        var third = CreateReservedJob(
            "job_RRRRRRRRRRRRRRRRRRRRRRRR",
            scope,
            reservation,
            environment.Time.GetUtcNow());
        await quota.StoreJobAsync(
            third,
            token => jobs.CreateAsync(third, token),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, (await jobs.ListAsync(TestContext.Current.CancellationToken)).Count);

        async Task<bool> TryStoreJobAsync(WordJob job)
        {
            try
            {
                await quota.StoreJobAsync(
                    job,
                    token => jobs.CreateAsync(job, token),
                    TestContext.Current.CancellationToken);
                return true;
            }
            catch (WordMcpException exception) when (exception.Code == "storage_quota_exceeded")
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task JobCannotPublishWhenItsDirectoryExceedsPersistedReservation()
    {
        using var environment = new TestEnvironment();
        using var drafts = new DraftRepository(environment.Options);
        using var analyses = new AnalysisRepository(environment.Options);
        using var jobs = new FileJobRepository(environment.Options);
        using var quota = new StorageQuotaService(
            jobs,
            drafts,
            analyses,
            environment.Options,
            environment.Time);
        var scope = new CallerScope("user-a", "conversation-a");
        var reservation = quota.GetJobReservationBytes(JobKind.Analyze);
        var job = CreateReservedJob(
            "job_TTTTTTTTTTTTTTTTTTTTTTTT",
            scope,
            reservation,
            environment.Time.GetUtcNow());
        await quota.StoreJobAsync(
            job,
            token => jobs.CreateAsync(job, token),
            TestContext.Current.CancellationToken);
        var oversizedPath = Path.Combine(jobs.GetJobDirectory(job.Id), "oversized.bin");
        await using (var stream = new FileStream(
                         oversizedPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4_096,
                         FileOptions.Asynchronous))
        {
            stream.SetLength(reservation + 1);
            await stream.FlushAsync(TestContext.Current.CancellationToken);
        }

        var error = await Assert.ThrowsAsync<WordMcpException>(() =>
            quota.EnsureJobWithinReservationAsync(job.Id, TestContext.Current.CancellationToken));

        Assert.Equal("job_storage_reservation_exceeded", error.Code);
    }

    [Fact]
    public async Task UserAndTotalLimitsEvictOnlyFromTheRequiredBoundary()
    {
        using var environment = new TestEnvironment();
        var options = CreateQuotaOptions(environment, maxUserItems: 2, maxTotalItems: 3);
        using var drafts = new DraftRepository(options);
        using var analyses = new AnalysisRepository(options);
        using var jobs = new FileJobRepository(options);
        using var quota = new StorageQuotaService(jobs, drafts, analyses, options, environment.Time);
        var foreign = CreateDraft(
            "draft_GGGGGGGGGGGGGGGGGGGGGGGG",
            new CallerScope("user-b", "conversation-z"),
            environment.Time.GetUtcNow());
        await StoreDraftAsync(quota, drafts, foreign);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var first = CreateDraft(
            "draft_HHHHHHHHHHHHHHHHHHHHHHHH",
            new CallerScope("user-a", "conversation-a"),
            environment.Time.GetUtcNow());
        await StoreDraftAsync(quota, drafts, first);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var second = CreateDraft(
            "draft_IIIIIIIIIIIIIIIIIIIIIIII",
            new CallerScope("user-a", "conversation-b"),
            environment.Time.GetUtcNow());
        await StoreDraftAsync(quota, drafts, second);
        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var third = CreateDraft(
            "draft_JJJJJJJJJJJJJJJJJJJJJJJJ",
            new CallerScope("user-a", "conversation-c"),
            environment.Time.GetUtcNow());

        await StoreDraftAsync(quota, drafts, third);

        var afterUserEviction = await drafts.ListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(afterUserEviction, item => item.Id == foreign.Id);
        Assert.DoesNotContain(afterUserEviction, item => item.Id == first.Id);
        Assert.Contains(afterUserEviction, item => item.Id == second.Id);
        Assert.Contains(afterUserEviction, item => item.Id == third.Id);

        environment.Time.Advance(TimeSpan.FromMinutes(1));
        var globalIncoming = CreateDraft(
            "draft_KKKKKKKKKKKKKKKKKKKKKKKK",
            new CallerScope("user-c", "conversation-y"),
            environment.Time.GetUtcNow());
        await StoreDraftAsync(quota, drafts, globalIncoming);

        var afterTotalEviction = await drafts.ListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(afterTotalEviction, item => item.Id == foreign.Id);
        Assert.Contains(afterTotalEviction, item => item.Id == second.Id);
        Assert.Contains(afterTotalEviction, item => item.Id == third.Id);
        Assert.Contains(afterTotalEviction, item => item.Id == globalIncoming.Id);
    }

    private static Task StoreDraftAsync(StorageQuotaService quota, DraftRepository repository, DraftRecord draft) =>
        quota.StoreDraftAsync(
            draft,
            token => repository.SaveAsync(draft, token),
            TestContext.Current.CancellationToken);

    private static Task StoreAnalysisAsync(
        StorageQuotaService quota,
        AnalysisRepository repository,
        AnalysisSnapshot snapshot) => quota.StoreAnalysisAsync(
            snapshot,
            cache: null,
            token => repository.SaveAsync(snapshot, token),
            TestContext.Current.CancellationToken);

    private static AnalysisCacheRecord CreateCache(CallerScope scope, string sha256, DateTimeOffset now)
    {
        var analysisId = "ana_AAAAAAAAAAAAAAAAAAAAAAAA";
        return new AnalysisCacheRecord(
            AnalysisRepository.CreateCacheId(scope, sha256),
            scope.UserScope,
            scope.ConversationScope,
            sha256,
            new AnalysisSummary(
                analysisId,
                sha256,
                new Dictionary<string, string?>(),
                "ja-JP",
                0,
                0,
                1,
                ["main"],
                [],
                [],
                new Dictionary<string, int>(),
                now.AddHours(1)),
            new Dictionary<string, IReadOnlyList<AnalysisItem>>(),
            new Dictionary<string, TargetRecord>(),
            now,
            now.AddHours(1),
            LastAccessedAt: now);
    }

    private static DraftRecord CreateDraft(string id, CallerScope scope, DateTimeOffset now) => new(
        id,
        scope.UserScope,
        scope.ConversationScope,
        new DocumentDefinition(
            "報告書",
            "共有",
            "担当者",
            null,
            "ja-JP",
            1,
            "none",
            new DocumentLayoutSpec(),
            new DocumentThemeSpec(),
            new DocumentDesignSpec(),
            new HeaderFooterPolicy(),
            []),
        now,
        now.AddHours(1),
        LastAccessedAt: now);

    private static AnalysisSnapshot CreateAnalysis(
        string id,
        CallerScope scope,
        string sha256,
        DateTimeOffset now)
    {
        var summary = new AnalysisSummary(
            id,
            sha256,
            new Dictionary<string, string?>(),
            "ja-JP",
            0,
            0,
            1,
            ["main"],
            [],
            [],
            new Dictionary<string, int>(),
            now.AddHours(1));
        return new AnalysisSnapshot(
            id,
            scope.UserScope,
            scope.ConversationScope,
            sha256,
            "/internal/source.docx",
            "source.docx",
            summary,
            new Dictionary<string, IReadOnlyList<AnalysisItem>>(),
            new Dictionary<string, TargetRecord>(),
            now,
            now.AddHours(1),
            LastAccessedAt: now);
    }

    private static WordJob CreateReservedJob(
        string id,
        CallerScope scope,
        long reservedBytes,
        DateTimeOffset now) => new(
        id,
        scope.UserScope,
        scope.ConversationScope,
        JobKind.Analyze,
        JobState.Queued,
        JsonSerializer.SerializeToElement(new AnalyzePayload("source"), JsonFileStore.Options),
        null,
        null,
        null,
        null,
        0,
        [],
        now,
        now,
        now.AddDays(7),
        ReservedBytes: reservedBytes);

    private static IOptions<WordMcpOptions> CreateQuotaOptions(
        TestEnvironment environment,
        int maxConversationItems = 128,
        int maxUserItems = 512,
        int maxTotalItems = 4_096,
        long maxConversationBytes = 512L * 1024 * 1024) => Options.Create(new WordMcpOptions
    {
        StorageRoot = Path.Combine(environment.Root, $"quota-{Guid.NewGuid():N}"),
        MaxStoredItemsPerConversation = maxConversationItems,
        MaxStoredItemsPerUser = maxUserItems,
        MaxStoredItemsTotal = maxTotalItems,
        MaxStoredBytesPerConversation = maxConversationBytes,
        MaxStoredBytesPerUser = Math.Max(maxConversationBytes, 2L * 1024 * 1024 * 1024),
        MaxStoredBytesTotal = Math.Max(maxConversationBytes, 10L * 1024 * 1024 * 1024),
    });

    private static long SerializedBytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonFileStore.Options).LongLength;
}
