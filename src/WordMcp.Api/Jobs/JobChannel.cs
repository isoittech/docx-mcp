using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;

namespace WordMcp.Jobs;

public sealed class JobChannel
{
    private readonly Channel<string> channel;

    public JobChannel(IOptions<WordMcpOptions> options)
    {
        // Recovery may enqueue the previous running set in addition to the configured queued set.
        var recoveryCapacity = options.Value.MaxQueueDepth + options.Value.MaxConcurrentJobs;
        channel = Channel.CreateBounded<string>(new BoundedChannelOptions(recoveryCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken) =>
        channel.Writer.WriteAsync(jobId, cancellationToken);

    public bool TryEnqueue(string jobId) => channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}
