using WordMcp.Analysis;
using WordMcp.Domain;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class AnalysisQueryServiceTests
{
    [Fact]
    public async Task GetChunkPaginatesAtFiftyWithOpaqueScopedCursor()
    {
        using var environment = new TestEnvironment();
        using var repository = new AnalysisRepository(environment.Options);
        var scopes = new ScopeIdService(environment.Options);
        var caller = new CallerContext("user-a", "conversation-a", null);
        var scope = scopes.Create(caller);
        var snapshot = Snapshot(scope, environment.Time.GetUtcNow().AddMinutes(30), 61);
        await repository.SaveAsync(snapshot, CancellationToken.None);
        var service = new AnalysisQueryService(
            repository,
            scopes,
            new CursorTokenService(environment.Options),
            environment.Time);

        var first = await service.GetChunkAsync(
            caller,
            snapshot.Id,
            "blocks",
            null,
            50,
            CancellationToken.None);
        var second = await service.GetChunkAsync(
            caller,
            snapshot.Id,
            "blocks",
            first.NextCursor,
            50,
            CancellationToken.None);

        Assert.Equal(50, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.StartsWith("cur_", first.NextCursor, StringComparison.Ordinal);
        Assert.Equal(11, second.Items.Count);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Items.Select(item => item.TargetId).Intersect(second.Items.Select(item => item.TargetId)));
    }

    [Fact]
    public async Task GetChunkHidesAnotherConversationAndRejectsTamperedCursor()
    {
        using var environment = new TestEnvironment();
        using var repository = new AnalysisRepository(environment.Options);
        var scopes = new ScopeIdService(environment.Options);
        var owner = new CallerContext("user-a", "conversation-a", null);
        var foreign = new CallerContext("user-a", "conversation-b", null);
        var snapshot = Snapshot(scopes.Create(owner), environment.Time.GetUtcNow().AddMinutes(30), 2);
        await repository.SaveAsync(snapshot, CancellationToken.None);
        var service = new AnalysisQueryService(
            repository,
            scopes,
            new CursorTokenService(environment.Options),
            environment.Time);
        var first = await service.GetChunkAsync(owner, snapshot.Id, "blocks", null, 1, CancellationToken.None);
        var tampered = string.Concat(first.NextCursor![..^1], first.NextCursor[^1] == 'A' ? "B" : "A");

        var hidden = await Assert.ThrowsAsync<WordMcpException>(() =>
            service.GetChunkAsync(foreign, snapshot.Id, "blocks", null, 1, CancellationToken.None));
        var invalid = await Assert.ThrowsAsync<WordMcpException>(() =>
            service.GetChunkAsync(owner, snapshot.Id, "blocks", tampered, 1, CancellationToken.None));

        Assert.Equal("analysis_not_found", hidden.Code);
        Assert.Equal("invalid_cursor", invalid.Code);
    }

    [Fact]
    public async Task GetChunkRejectsExpiredSnapshot()
    {
        using var environment = new TestEnvironment();
        using var repository = new AnalysisRepository(environment.Options);
        var scopes = new ScopeIdService(environment.Options);
        var caller = new CallerContext("user-a", "conversation-a", null);
        var snapshot = Snapshot(scopes.Create(caller), environment.Time.GetUtcNow().AddMinutes(-1), 1);
        await repository.SaveAsync(snapshot, CancellationToken.None);
        var service = new AnalysisQueryService(
            repository,
            scopes,
            new CursorTokenService(environment.Options),
            environment.Time);

        var exception = await Assert.ThrowsAsync<WordMcpException>(() =>
            service.GetChunkAsync(caller, snapshot.Id, "blocks", null, 1, CancellationToken.None));

        Assert.Equal("analysis_expired", exception.Code);
    }

    private static AnalysisSnapshot Snapshot(CallerScope scope, DateTimeOffset expiresAt, int count)
    {
        const string id = "ana_AAAAAAAAAAAAAAAAAAAAAAAA";
        var items = Enumerable.Range(1, count)
            .Select(index => new AnalysisItem(
                "paragraph",
                $"tgt_{index:D24}",
                "main",
                new Dictionary<string, object?> { ["snippet"] = $"block-{index}" }))
            .ToArray();
        var summary = new AnalysisSummary(
            id,
            new string('a', 64),
            new Dictionary<string, string?>(),
            "ja-JP",
            count,
            count,
            1,
            ["main"],
            [],
            [],
            new Dictionary<string, int> { ["blocks"] = count },
            expiresAt);
        return new AnalysisSnapshot(
            id,
            scope.UserScope,
            scope.ConversationScope,
            summary.SourceSha256,
            "/internal/source.docx",
            "source.docx",
            summary,
            new Dictionary<string, IReadOnlyList<AnalysisItem>> { ["blocks"] = items },
            new Dictionary<string, TargetRecord>(),
            expiresAt.AddHours(-1),
            expiresAt);
    }
}
