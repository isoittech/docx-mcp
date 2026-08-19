using WordMcp.Domain;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class AnalysisRepositoryTests
{
    [Fact]
    public async Task SuccessfulMutationInvalidatesEverySiblingSnapshotAndSourceCacheInScope()
    {
        using var environment = new TestEnvironment();
        using var repository = new AnalysisRepository(environment.Options);
        var scope = new CallerScope("user-scope", "conversation-scope");
        var otherScope = new CallerScope("user-scope", "other-conversation-scope");
        var now = environment.Time.GetUtcNow();
        var sourceSha = new string('a', 64);
        var outputSha = new string('b', 64);
        var first = Snapshot("ana_AAAAAAAAAAAAAAAAAAAAAAAA", scope, sourceSha, now);
        var second = Snapshot("ana_BBBBBBBBBBBBBBBBBBBBBBBB", scope, sourceSha, now);
        var foreign = Snapshot("ana_CCCCCCCCCCCCCCCCCCCCCCCC", otherScope, sourceSha, now);
        var output = Snapshot("ana_DDDDDDDDDDDDDDDDDDDDDDDD", scope, outputSha, now);
        await repository.SaveAsync(first, TestContext.Current.CancellationToken);
        await repository.SaveAsync(second, TestContext.Current.CancellationToken);
        await repository.SaveAsync(foreign, TestContext.Current.CancellationToken);
        await repository.SaveAsync(output, TestContext.Current.CancellationToken);
        await repository.SaveCacheAsync(
            Cache(first, scope, sourceSha, now),
            TestContext.Current.CancellationToken);

        var count = await repository.InvalidateSourceAsync(
            scope,
            sourceSha,
            now.AddMinutes(1),
            output.Id,
            preserveSourceCache: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        await AssertStaleAsync(repository, scope, first.Id, environment.Time);
        await AssertStaleAsync(repository, scope, second.Id, environment.Time);
        Assert.Equal(
            output.Id,
            (await repository.GetOwnedAsync(
                scope,
                output.Id,
                environment.Time,
                TestContext.Current.CancellationToken)).Id);
        Assert.Equal(
            foreign.Id,
            (await repository.GetOwnedAsync(
                otherScope,
                foreign.Id,
                environment.Time,
                TestContext.Current.CancellationToken)).Id);
        Assert.Null(await repository.TryGetCacheAsync(
            scope,
            sourceSha,
            environment.Time,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoOpHashMutationPreservesOnlyTheReplacementSnapshotAndCache()
    {
        using var environment = new TestEnvironment();
        using var repository = new AnalysisRepository(environment.Options);
        var scope = new CallerScope("user-scope", "conversation-scope");
        var now = environment.Time.GetUtcNow();
        var sourceSha = new string('c', 64);
        var old = Snapshot("ana_EEEEEEEEEEEEEEEEEEEEEEEE", scope, sourceSha, now);
        var replacement = Snapshot("ana_FFFFFFFFFFFFFFFFFFFFFFFF", scope, sourceSha, now);
        await repository.SaveAsync(old, TestContext.Current.CancellationToken);
        await repository.SaveAsync(replacement, TestContext.Current.CancellationToken);
        await repository.SaveCacheAsync(
            Cache(replacement, scope, sourceSha, now),
            TestContext.Current.CancellationToken);

        var count = await repository.InvalidateSourceAsync(
            scope,
            sourceSha,
            now.AddMinutes(1),
            replacement.Id,
            preserveSourceCache: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        await AssertStaleAsync(repository, scope, old.Id, environment.Time);
        Assert.Equal(
            replacement.Id,
            (await repository.GetOwnedAsync(
                scope,
                replacement.Id,
                environment.Time,
                TestContext.Current.CancellationToken)).Id);
        Assert.NotNull(await repository.TryGetCacheAsync(
            scope,
            sourceSha,
            environment.Time,
            TestContext.Current.CancellationToken));
    }

    private static AnalysisSnapshot Snapshot(
        string id,
        CallerScope scope,
        string sha,
        DateTimeOffset now)
    {
        var summary = new AnalysisSummary(
            id,
            sha,
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
            sha,
            "/safe/source.docx",
            "source.docx",
            summary,
            new Dictionary<string, IReadOnlyList<AnalysisItem>>(),
            new Dictionary<string, TargetRecord>(),
            now,
            now.AddHours(1),
            LastAccessedAt: now);
    }

    private static AnalysisCacheRecord Cache(
        AnalysisSnapshot snapshot,
        CallerScope scope,
        string sha,
        DateTimeOffset now) => new(
        AnalysisRepository.CreateCacheId(scope, sha),
        scope.UserScope,
        scope.ConversationScope,
        sha,
        snapshot.Summary,
        snapshot.Items,
        snapshot.Targets,
        now,
        now.AddHours(1),
        LastAccessedAt: now);

    private static async Task AssertStaleAsync(
        AnalysisRepository repository,
        CallerScope scope,
        string analysisId,
        TimeProvider timeProvider)
    {
        var error = await Assert.ThrowsAsync<WordMcpException>(() => repository.GetOwnedAsync(
            scope,
            analysisId,
            timeProvider,
            TestContext.Current.CancellationToken));
        Assert.Equal("stale_analysis", error.Code);
    }
}
