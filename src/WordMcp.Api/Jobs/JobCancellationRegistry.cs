using System.Collections.Concurrent;

namespace WordMcp.Jobs;

public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> sources = new(StringComparer.Ordinal);

    public CancellationTokenSource Register(string jobId)
    {
        var source = new CancellationTokenSource();
        if (!sources.TryAdd(jobId, source))
        {
            source.Dispose();
            throw new InvalidOperationException("A cancellation source is already registered for this job.");
        }

        return source;
    }

    public bool Cancel(string jobId) => sources.TryGetValue(jobId, out var source) && Cancel(source);

    public void Remove(string jobId)
    {
        if (sources.TryRemove(jobId, out var source))
        {
            source.Dispose();
        }
    }

    private static bool Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
