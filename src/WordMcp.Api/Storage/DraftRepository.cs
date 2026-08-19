using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Storage;

public sealed class DraftRepository : IDisposable
{
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);

    public DraftRepository(IOptions<WordMcpOptions> options)
    {
        root = Path.Combine(options.Value.StorageRoot, "drafts");
        Directory.CreateDirectory(root);
    }

    public async Task SaveAsync(DraftRecord draft, CancellationToken cancellationToken)
    {
        if (!Identifier.IsValid(draft.Id, "draft_"))
        {
            throw new InvalidOperationException("Invalid internally generated draft identifier.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonFileStore.WriteAtomicAsync(PathFor(draft.Id), draft, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DraftRecord> GetOwnedAsync(
        CallerScope scope,
        string draftId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!Identifier.IsValid(draftId, "draft_"))
        {
            throw NotFound();
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var draft = await JsonFileStore.ReadAsync<DraftRecord>(PathFor(draftId), cancellationToken).ConfigureAwait(false);
            if (draft is null || draft.UserScope != scope.UserScope || draft.ConversationScope != scope.ConversationScope)
            {
                throw NotFound();
            }

            var now = timeProvider.GetUtcNow();
            if (draft.ExpiresAt <= now)
            {
                throw new WordMcpException(
                    "draft_expired",
                    "$.draft_id",
                    "The document draft has expired.",
                    "Start a new document workflow and add the sections again.");
            }

            var accessed = draft with { LastAccessedAt = now };
            await JsonFileStore.WriteAtomicAsync(PathFor(draftId), accessed, cancellationToken).ConfigureAwait(false);
            return accessed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<DraftRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<DraftRecord>();
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var item = await JsonFileStore.ReadAsync<DraftRecord>(path, cancellationToken).ConfigureAwait(false);
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

    public async Task DeleteAsync(string draftId, CancellationToken cancellationToken)
    {
        if (!Identifier.IsValid(draftId, "draft_"))
        {
            throw NotFound();
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(draftId);
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

    private string PathFor(string id) => Path.Combine(root, $"{id}.json");

    private static WordMcpException NotFound() => new(
        "draft_not_found",
        "$.draft_id",
        "The draft was not found in this caller scope.",
        "Use the draft_id returned by word_start_document in this conversation.");

    public void Dispose() => gate.Dispose();
}
