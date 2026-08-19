using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Storage;

public sealed class AnalysisRepository : IDisposable
{
    private readonly string root;
    private readonly string cacheRoot;
    private readonly SemaphoreSlim gate = new(1, 1);

    public AnalysisRepository(IOptions<WordMcpOptions> options)
    {
        root = Path.Combine(options.Value.StorageRoot, "analyses");
        cacheRoot = Path.Combine(options.Value.StorageRoot, "analysis-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cacheRoot);
    }

    public async Task SaveAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken)
    {
        ValidateId(snapshot.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonFileStore.WriteAtomicAsync(PathFor(snapshot.Id), snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AnalysisSnapshot> GetOwnedAsync(
        CallerScope scope,
        string analysisId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        bool permitInvalidated = false)
    {
        ValidateId(analysisId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await JsonFileStore.ReadAsync<AnalysisSnapshot>(PathFor(analysisId), cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null || snapshot.UserScope != scope.UserScope || snapshot.ConversationScope != scope.ConversationScope)
            {
                throw NotFound();
            }

            var now = timeProvider.GetUtcNow();
            if (snapshot.ExpiresAt <= now)
            {
                throw new WordMcpException(
                    "analysis_expired",
                    "$.analysis_id",
                    "The analysis snapshot has expired.",
                    "Run word_analyze again and use the new analysis_id and target_id values.");
            }

            if (!permitInvalidated && snapshot.InvalidatedAt is not null)
            {
                throw new WordMcpException(
                    "stale_analysis",
                    "$.analysis_id",
                    "The analysis snapshot became stale after a successful edit.",
                    "Use output_analysis_id from the latest successful edit.");
            }

            var accessed = snapshot with { LastAccessedAt = now };
            await JsonFileStore.WriteAtomicAsync(PathFor(analysisId), accessed, cancellationToken).ConfigureAwait(false);
            return accessed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task InvalidateAsync(string analysisId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ValidateId(analysisId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(analysisId);
            var current = await JsonFileStore.ReadAsync<AnalysisSnapshot>(path, cancellationToken).ConfigureAwait(false);
            if (current is not null && current.InvalidatedAt is null)
            {
                await JsonFileStore.WriteAtomicAsync(path, current with { InvalidatedAt = at }, cancellationToken)
                    .ConfigureAwait(false);
                var cachePath = CachePathFor(current.UserScope, current.ConversationScope, current.SourceSha256);
                var cached = await JsonFileStore.ReadAsync<AnalysisCacheRecord>(cachePath, cancellationToken)
                    .ConfigureAwait(false);
                if (cached is not null
                    && cached.UserScope == current.UserScope
                    && cached.ConversationScope == current.ConversationScope
                    && string.Equals(cached.SourceSha256, current.SourceSha256, StringComparison.OrdinalIgnoreCase)
                    && cached.InvalidatedAt is null)
                {
                    await JsonFileStore.WriteAtomicAsync(
                        cachePath,
                        cached with { InvalidatedAt = at },
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Invalidates every analysis branch for one caller-scoped source hash. A successful edit
    /// must retire sibling snapshots created from the same bytes, not only the analysis ID used
    /// by that one request.
    /// </summary>
    public async Task<int> InvalidateSourceAsync(
        CallerScope scope,
        string sourceSha256,
        DateTimeOffset at,
        string preserveAnalysisId,
        bool preserveSourceCache,
        CancellationToken cancellationToken)
    {
        ValidateSha256(sourceSha256);
        ValidateId(preserveAnalysisId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var invalidated = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await JsonFileStore.ReadAsync<AnalysisSnapshot>(path, cancellationToken)
                    .ConfigureAwait(false);
                if (current is null
                    || current.Id == preserveAnalysisId
                    || current.InvalidatedAt is not null
                    || current.UserScope != scope.UserScope
                    || current.ConversationScope != scope.ConversationScope
                    || !string.Equals(current.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await JsonFileStore.WriteAtomicAsync(path, current with { InvalidatedAt = at }, cancellationToken)
                    .ConfigureAwait(false);
                invalidated++;
            }

            if (!preserveSourceCache)
            {
                var cachePath = CachePathFor(scope.UserScope, scope.ConversationScope, sourceSha256);
                var cached = await JsonFileStore.ReadAsync<AnalysisCacheRecord>(cachePath, cancellationToken)
                    .ConfigureAwait(false);
                if (cached is not null
                    && cached.UserScope == scope.UserScope
                    && cached.ConversationScope == scope.ConversationScope
                    && string.Equals(cached.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)
                    && cached.InvalidatedAt is null)
                {
                    await JsonFileStore.WriteAtomicAsync(
                            cachePath,
                            cached with { InvalidatedAt = at },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return invalidated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<AnalysisSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<AnalysisSnapshot>();
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var item = await JsonFileStore.ReadAsync<AnalysisSnapshot>(path, cancellationToken).ConfigureAwait(false);
                if (item is not null)
                {
                    result.Add(item);
                }
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string analysisId, CancellationToken cancellationToken)
    {
        ValidateId(analysisId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(analysisId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveCacheAsync(AnalysisCacheRecord cache, CancellationToken cancellationToken)
    {
        ValidateCache(cache);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonFileStore.WriteAtomicAsync(CachePathFor(cache), cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AnalysisCacheRecord?> TryGetCacheAsync(
        CallerScope scope,
        string sourceSha256,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ValidateSha256(sourceSha256);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = CachePathFor(scope.UserScope, scope.ConversationScope, sourceSha256);
            var cached = await JsonFileStore.ReadAsync<AnalysisCacheRecord>(path, cancellationToken).ConfigureAwait(false);
            if (cached is null
                || cached.Id != CreateCacheId(scope, sourceSha256)
                || cached.UserScope != scope.UserScope
                || cached.ConversationScope != scope.ConversationScope
                || !string.Equals(cached.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cached.Summary.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)
                || cached.Targets.Any(pair => pair.Key != pair.Value.TargetId)
                || cached.Items.Values.SelectMany(static items => items)
                    .Any(item => item.TargetId is not null && !cached.Targets.ContainsKey(item.TargetId)))
            {
                if (cached is not null)
                {
                    File.Delete(path);
                }

                return null;
            }

            var now = timeProvider.GetUtcNow();
            if (cached.ExpiresAt <= now || cached.InvalidatedAt is not null)
            {
                File.Delete(path);
                return null;
            }

            var accessed = cached with { LastAccessedAt = now };
            await JsonFileStore.WriteAtomicAsync(path, accessed, cancellationToken).ConfigureAwait(false);
            return accessed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<AnalysisCacheRecord>> ListCacheAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<AnalysisCacheRecord>();
            foreach (var path in Directory.EnumerateFiles(cacheRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                var item = await JsonFileStore.ReadAsync<AnalysisCacheRecord>(path, cancellationToken)
                    .ConfigureAwait(false);
                if (item is not null)
                {
                    result.Add(item);
                }
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteCacheAsync(string cacheId, CancellationToken cancellationToken)
    {
        ValidateCacheId(cacheId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(cacheRoot, $"{cacheId}.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public static string CreateCacheId(CallerScope scope, string sourceSha256)
    {
        ValidateSha256(sourceSha256);
        var canonical = Encoding.UTF8.GetBytes(string.Join(
            '\n',
            "word-mcp:analysis-cache:v1",
            scope.UserScope,
            scope.ConversationScope,
            sourceSha256.ToLowerInvariant()));
        var digest = SHA256.HashData(canonical);
        return string.Concat("cache_", WebEncoders.Base64UrlEncode(digest.AsSpan(0, 18)));
    }

    private string PathFor(string id) => Path.Combine(root, $"{id}.json");

    private string CachePathFor(AnalysisCacheRecord cache) =>
        CachePathFor(cache.UserScope, cache.ConversationScope, cache.SourceSha256);

    private string CachePathFor(string userScope, string conversationScope, string sourceSha256) =>
        Path.Combine(
            cacheRoot,
            $"{CreateCacheId(new CallerScope(userScope, conversationScope), sourceSha256)}.json");

    private static void ValidateId(string id)
    {
        if (!Identifier.IsValid(id, "ana_"))
        {
            throw NotFound();
        }
    }

    private static void ValidateCache(AnalysisCacheRecord cache)
    {
        ValidateCacheId(cache.Id);
        ValidateSha256(cache.SourceSha256);
        var expected = CreateCacheId(
            new CallerScope(cache.UserScope, cache.ConversationScope),
            cache.SourceSha256);
        if (!string.Equals(cache.Id, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The analysis cache identifier does not match its caller scope and content hash.");
        }
    }

    private static void ValidateCacheId(string cacheId)
    {
        if (!Identifier.IsValid(cacheId, "cache_"))
        {
            throw new InvalidOperationException("Invalid internally generated analysis cache identifier.");
        }
    }

    private static void ValidateSha256(string sourceSha256)
    {
        if (sourceSha256.Length != 64 || sourceSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("An analysis cache key must contain a SHA-256 digest.");
        }
    }

    private static WordMcpException NotFound() => new(
        "analysis_not_found",
        "$.analysis_id",
        "The analysis was not found in this caller scope.",
        "Use an analysis_id returned in this conversation.");

    public void Dispose() => gate.Dispose();
}
