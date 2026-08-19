using WordMcp.Domain;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Analysis;

public sealed class AnalysisQueryService(
    AnalysisRepository repository,
    ScopeIdService scopes,
    CursorTokenService cursors,
    TimeProvider timeProvider)
{
    public async Task<AnalysisChunk> GetChunkAsync(
        CallerContext caller,
        string analysisId,
        string kind,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50)
        {
            throw new WordMcpException(
                "analysis_chunk_limit",
                "$.limit",
                "Analysis chunk limit must be between 1 and 50.",
                "Use a limit of 50 or less and follow next_cursor.");
        }

        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 64)
        {
            throw new WordMcpException(
                "invalid_analysis_kind",
                "$.kind",
                "Analysis kind is missing or invalid.",
                "Choose one of available_kinds from the analysis summary.");
        }

        var scope = scopes.Create(caller);
        var snapshot = await repository.GetOwnedAsync(
            scope,
            analysisId,
            timeProvider,
            cancellationToken,
            permitInvalidated: true).ConfigureAwait(false);
        if (!snapshot.Items.TryGetValue(kind, out var items))
        {
            throw new WordMcpException(
                "analysis_kind_not_found",
                "$.kind",
                "The requested analysis kind is unavailable.",
                "Choose one of available_kinds from the analysis summary.");
        }

        var offset = cursor is null ? 0 : cursors.Parse(cursor, analysisId, kind, scope);
        if (offset > items.Count)
        {
            throw new WordMcpException(
                "cursor_out_of_range",
                "$.cursor",
                "The cursor is outside this analysis result.",
                "Restart this kind from an empty cursor.");
        }

        var page = items.Skip(offset).Take(limit).ToArray();
        var nextOffset = offset + page.Length;
        var next = nextOffset < items.Count ? cursors.Create(analysisId, kind, nextOffset, scope) : null;
        return new AnalysisChunk(analysisId, kind, page, next, Truncated(page));
    }

    private static bool Truncated(IEnumerable<AnalysisItem> items) => items.Any(item =>
        item.Data.TryGetValue("snippet_truncated", out var value) && value is true);
}
