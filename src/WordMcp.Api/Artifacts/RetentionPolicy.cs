using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Artifacts;

public sealed class RetentionPolicy(IOptions<WordMcpOptions> options)
{
    private readonly TimeSpan creationLifetime = TimeSpan.FromDays(options.Value.RetentionDays);
    private readonly TimeSpan downloadLifetime = TimeSpan.FromHours(options.Value.RetentionHoursAfterDownload);

    public DateTimeOffset EffectiveJobExpiry(WordJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Result?.Artifacts is not { Count: > 0 } artifacts)
        {
            return job.ExpiresAt;
        }

        var creationExpiry = artifacts.Min(artifact => artifact.CreatedAt.Add(creationLifetime));
        var firstDocumentDownload = artifacts
            .Where(artifact => artifact.Kind == "document" && artifact.FirstDownloadedAt is not null)
            .Select(artifact => artifact.FirstDownloadedAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        return firstDocumentDownload == DateTimeOffset.MaxValue
            ? creationExpiry
            : Min(creationExpiry, firstDocumentDownload.Add(downloadLifetime));
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
