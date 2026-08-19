using System.Text.Json;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Storage;

public sealed class StorageQuotaService(
    FileJobRepository jobs,
    DraftRepository drafts,
    AnalysisRepository analyses,
    IOptions<WordMcpOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private const long RenderWorkspaceReservationBytes = 16L * 1024 * 1024;
    private readonly WordMcpOptions options = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task EnsureCanCreateAsync(CallerScope scope, CancellationToken cancellationToken) =>
        EnsureCanCreateAsync(scope, JobKind.Analyze, cancellationToken);

    public async Task EnsureCanCreateAsync(
        CallerScope scope,
        JobKind kind,
        CancellationToken cancellationToken)
    {
        var incoming = new StoredItem(
            StorageKind.Job,
            "incoming-job",
            scope.UserScope,
            scope.ConversationScope,
            Bytes: GetJobReservationBytes(kind),
            LastAccessedAt: timeProvider.GetUtcNow(),
            ExpiresAt: DateTimeOffset.MaxValue,
            Stale: false,
            Evictable: false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EvictUntilFitsAsync([incoming], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task StoreJobAsync(
        WordJob job,
        Func<CancellationToken, Task> persist,
        CancellationToken cancellationToken)
    {
        var requiredReservation = GetJobReservationBytes(job.Kind);
        if (job.State != JobState.Queued || job.ReservedBytes != requiredReservation)
        {
            throw new InvalidOperationException("A new queued job must persist its complete capacity reservation.");
        }

        return StoreReusableAsync(
            [new StoredItem(
                StorageKind.Job,
                job.Id,
                job.UserScope,
                job.ConversationScope,
                job.ReservedBytes,
                job.UpdatedAt,
                job.ExpiresAt,
                Stale: false,
                Evictable: false)],
            persist,
            cancellationToken);
    }

    public async Task EnsureJobWithinReservationAsync(string jobId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
                ?? throw new WordMcpException(
                    "job_not_found",
                    "$.job_id",
                    "The job was not found.",
                    "Use a job_id returned in this conversation.");
            var reservedBytes = EffectiveReservation(job);
            if (DirectoryBytes(jobs.GetJobDirectory(job.Id)) > reservedBytes)
            {
                throw new WordMcpException(
                    "job_storage_reservation_exceeded",
                    "$",
                    "The job exceeded its reserved persistent output capacity.",
                    "Reduce document or preview complexity before retrying.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public long GetJobReservationBytes(JobKind kind)
    {
        var metadata = checked(options.MaxRequestBodyBytes * 2);
        var renderOutputs = checked(
            options.MaxPdfBytes
            + options.MaxPreviewBytes
            + options.MaxFileBytes
            + RenderWorkspaceReservationBytes);
        return kind switch
        {
            JobKind.Analyze => checked(options.MaxFileBytes + metadata),
            JobKind.RenderPreview => checked(options.MaxFileBytes + renderOutputs + metadata),
            JobKind.ReplaceText or JobKind.ApplyEdits or JobKind.PopulateTemplate => checked(
                options.MaxFileBytes
                + options.MaxFileBytes
                + renderOutputs
                + metadata),
            JobKind.FinishDocument or JobKind.InsertSections or JobKind.RefineSection => checked(
                options.MaxFileBytes
                + options.MaxTotalImageBytes
                + options.MaxFileBytes
                + renderOutputs
                + metadata),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported job kind."),
        };
    }

    public Task StoreDraftAsync(
        DraftRecord draft,
        Func<CancellationToken, Task> persist,
        CancellationToken cancellationToken) =>
        StoreReusableAsync(
            [CreateStoredItem(StorageKind.Draft, draft.Id, draft.UserScope, draft.ConversationScope, draft,
                draft.LastAccessedAt ?? draft.CreatedAt, draft.ExpiresAt, stale: false)],
            persist,
            cancellationToken);

    public Task StoreAnalysisAsync(
        AnalysisSnapshot snapshot,
        AnalysisCacheRecord? cache,
        Func<CancellationToken, Task> persist,
        CancellationToken cancellationToken)
    {
        if (cache is not null
            && (cache.UserScope != snapshot.UserScope
                || cache.ConversationScope != snapshot.ConversationScope
                || !string.Equals(cache.SourceSha256, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("An analysis cache reservation must match its snapshot scope and content hash.");
        }

        var incoming = new List<StoredItem>
        {
            CreateStoredItem(
                StorageKind.Analysis,
                snapshot.Id,
                snapshot.UserScope,
                snapshot.ConversationScope,
                snapshot,
                snapshot.LastAccessedAt ?? snapshot.CreatedAt,
                snapshot.ExpiresAt,
                snapshot.InvalidatedAt is not null),
        };
        if (cache is not null)
        {
            incoming.Add(CreateStoredItem(
                StorageKind.AnalysisCache,
                cache.Id,
                cache.UserScope,
                cache.ConversationScope,
                cache,
                cache.LastAccessedAt ?? cache.CreatedAt,
                cache.ExpiresAt,
                cache.InvalidatedAt is not null));
        }

        return StoreReusableAsync(incoming, persist, cancellationToken);
    }

    private async Task StoreReusableAsync(
        IReadOnlyList<StoredItem> incoming,
        Func<CancellationToken, Task> persist,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EvictUntilFitsAsync(incoming, cancellationToken).ConfigureAwait(false);
            await persist(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EvictUntilFitsAsync(
        IReadOnlyList<StoredItem> incoming,
        CancellationToken cancellationToken)
    {
        var allJobsTask = jobs.ListAsync(cancellationToken);
        var allDraftsTask = drafts.ListAsync(cancellationToken);
        var allAnalysesTask = analyses.ListAsync(cancellationToken);
        var allCacheTask = analyses.ListCacheAsync(cancellationToken);
        await Task.WhenAll(allJobsTask, allDraftsTask, allAnalysesTask, allCacheTask).ConfigureAwait(false);

        var allJobs = await allJobsTask.ConfigureAwait(false);
        var protectedIds = ActiveReferences(allJobs);
        var activeJobRoots = allJobs
            .Where(job => job.State is JobState.Queued or JobState.Running)
            .Select(job => Path.GetFullPath(jobs.GetJobDirectory(job.Id)) + Path.DirectorySeparatorChar)
            .ToArray();
        var items = new List<StoredItem>();
        items.AddRange(allJobs.Select(job => new StoredItem(
            StorageKind.Job,
            job.Id,
            job.UserScope,
            job.ConversationScope,
            job.State is JobState.Queued or JobState.Running
                ? Math.Max(DirectoryBytes(jobs.GetJobDirectory(job.Id)), EffectiveReservation(job))
                : DirectoryBytes(jobs.GetJobDirectory(job.Id)),
            job.UpdatedAt,
            job.ExpiresAt,
            Stale: false,
            Evictable: false)));
        items.AddRange((await allDraftsTask.ConfigureAwait(false)).Select(draft => CreateStoredItem(
            StorageKind.Draft,
            draft.Id,
            draft.UserScope,
            draft.ConversationScope,
            draft,
            draft.LastAccessedAt ?? draft.CreatedAt,
            draft.ExpiresAt,
            stale: false,
            evictable: !protectedIds.Contains(draft.Id))));
        items.AddRange((await allAnalysesTask.ConfigureAwait(false)).Select(snapshot => CreateStoredItem(
            StorageKind.Analysis,
            snapshot.Id,
            snapshot.UserScope,
            snapshot.ConversationScope,
            snapshot,
            snapshot.LastAccessedAt ?? snapshot.CreatedAt,
            snapshot.ExpiresAt,
            snapshot.InvalidatedAt is not null,
            evictable: !protectedIds.Contains(snapshot.Id)
                       && !IsWithinAnyRoot(snapshot.SourcePath, activeJobRoots))));
        items.AddRange((await allCacheTask.ConfigureAwait(false)).Select(cache => CreateStoredItem(
            StorageKind.AnalysisCache,
            cache.Id,
            cache.UserScope,
            cache.ConversationScope,
            cache,
            cache.LastAccessedAt ?? cache.CreatedAt,
            cache.ExpiresAt,
            cache.InvalidatedAt is not null)));

        var incomingKeys = incoming.Select(Key).ToHashSet(StringComparer.Ordinal);
        items.RemoveAll(item => incomingKeys.Contains(Key(item)));
        var now = timeProvider.GetUtcNow();
        foreach (var obsolete in items
                     .Where(item => item.Evictable && (item.ExpiresAt <= now || item.Stale))
                     .OrderBy(item => item.ExpiresAt)
                     .ThenBy(item => item.LastAccessedAt)
                     .ThenBy(item => item.Id, StringComparer.Ordinal)
                     .ToArray())
        {
            await DeleteAsync(obsolete, cancellationToken).ConfigureAwait(false);
            items.Remove(obsolete);
        }

        items.AddRange(incoming);
        while (Violations(items, incoming[0]) is { } violation)
        {
            var candidate = items
                .Where(item => item.Evictable && !incomingKeys.Contains(Key(item)))
                .Where(item => violation.Level switch
                {
                    QuotaLevel.Conversation => item.UserScope == incoming[0].UserScope
                                               && item.ConversationScope == incoming[0].ConversationScope,
                    QuotaLevel.User => item.UserScope == incoming[0].UserScope,
                    _ => true,
                })
                .OrderBy(item => item.LastAccessedAt)
                .ThenBy(item => item.CreatedOrder)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate is null)
            {
                throw QuotaExceeded();
            }

            await DeleteAsync(candidate, cancellationToken).ConfigureAwait(false);
            items.Remove(candidate);
        }
    }

    private QuotaViolation? Violations(IReadOnlyCollection<StoredItem> items, StoredItem incomingScope)
    {
        var conversation = items.Where(item => item.UserScope == incomingScope.UserScope
                                               && item.ConversationScope == incomingScope.ConversationScope).ToArray();
        if (conversation.Length > options.MaxStoredItemsPerConversation
            || SumBytes(conversation) > options.MaxStoredBytesPerConversation)
        {
            return new QuotaViolation(QuotaLevel.Conversation);
        }

        var user = items.Where(item => item.UserScope == incomingScope.UserScope).ToArray();
        if (user.Length > options.MaxStoredItemsPerUser || SumBytes(user) > options.MaxStoredBytesPerUser)
        {
            return new QuotaViolation(QuotaLevel.User);
        }

        if (items.Count > options.MaxStoredItemsTotal || SumBytes(items) > options.MaxStoredBytesTotal)
        {
            return new QuotaViolation(QuotaLevel.Total);
        }

        return null;
    }

    private async Task DeleteAsync(StoredItem item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case StorageKind.Draft:
                await drafts.DeleteAsync(item.Id, cancellationToken).ConfigureAwait(false);
                break;
            case StorageKind.Analysis:
                await analyses.DeleteAsync(item.Id, cancellationToken).ConfigureAwait(false);
                break;
            case StorageKind.AnalysisCache:
                await analyses.DeleteCacheAsync(item.Id, cancellationToken).ConfigureAwait(false);
                break;
            case StorageKind.Job:
            default:
                throw new InvalidOperationException("Only reusable storage records may be evicted.");
        }
    }

    private static HashSet<string> ActiveReferences(IReadOnlyList<WordJob> allJobs)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in allJobs.Where(item => item.State is JobState.Queued or JobState.Running))
        {
            if (job.DraftId is not null)
            {
                result.Add(job.DraftId);
            }

            if (job.Kind is JobKind.ReplaceText or JobKind.ApplyEdits
                && job.Payload.ValueKind == JsonValueKind.Object
                && job.Payload.TryGetProperty("analysis_id", out var analysisId)
                && analysisId.ValueKind == JsonValueKind.String
                && analysisId.GetString() is { } value)
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static StoredItem CreateStoredItem<T>(
        StorageKind kind,
        string id,
        string userScope,
        string conversationScope,
        T value,
        DateTimeOffset lastAccessedAt,
        DateTimeOffset expiresAt,
        bool stale,
        bool evictable = true) => new(
            kind,
            id,
            userScope,
            conversationScope,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonFileStore.Options).LongLength,
            lastAccessedAt,
            expiresAt,
            stale,
            evictable);

    private static long DirectoryBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            total = checked(total + new FileInfo(path).Length);
        }

        return total;
    }

    private static long SumBytes(IEnumerable<StoredItem> items) =>
        items.Aggregate(0L, static (total, item) => checked(total + item.Bytes));

    private long EffectiveReservation(WordJob job) =>
        Math.Max(job.ReservedBytes, GetJobReservationBytes(job.Kind));

    private static bool IsWithinAnyRoot(string path, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return roots.Any(root => fullPath.StartsWith(root, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Key(StoredItem item) => $"{item.Kind}:{item.Id}";

    private static WordMcpException QuotaExceeded() => new(
        "storage_quota_exceeded",
        "$",
        "The persistent Word workspace has reached an item or byte quota.",
        "Wait for retention cleanup or download the current artifact instead of starting more jobs.");

    public void Dispose()
    {
        gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private enum StorageKind
    {
        Job,
        Draft,
        Analysis,
        AnalysisCache,
    }

    private enum QuotaLevel
    {
        Conversation,
        User,
        Total,
    }

    private sealed record QuotaViolation(QuotaLevel Level);

    private sealed record StoredItem(
        StorageKind Kind,
        string Id,
        string UserScope,
        string ConversationScope,
        long Bytes,
        DateTimeOffset LastAccessedAt,
        DateTimeOffset ExpiresAt,
        bool Stale,
        bool Evictable)
    {
        public long CreatedOrder => LastAccessedAt.UtcTicks;
    }
}
