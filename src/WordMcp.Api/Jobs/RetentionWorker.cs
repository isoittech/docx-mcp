using WordMcp.Artifacts;
using WordMcp.Domain;
using WordMcp.Storage;

namespace WordMcp.Jobs;

public sealed class RetentionWorker(
    FileJobRepository jobs,
    DraftRepository drafts,
    AnalysisRepository analyses,
    RetentionPolicy retention,
    TimeProvider timeProvider,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, string, Exception?> LogDeleted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2001, nameof(LogDeleted)),
            "Deleted expired Word {ItemKind} {ItemId}.");
    private static readonly Action<ILogger, string, string, Exception?> LogDeleteFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2002, nameof(LogDeleteFailure)),
            "Could not delete expired Word {ItemKind} {ItemId}.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var jobRecords = await jobs.ListForRetentionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (job.State is JobState.Queued or JobState.Running || retention.EffectiveJobExpiry(job) > now)
            {
                continue;
            }

            try
            {
                await jobs.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
                LogDeleted(logger, "job", job.Id, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogDeleteFailure(logger, "job", job.Id, null);
            }
        }

        var draftRecords = await drafts.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var draft in draftRecords.Where(item => item.ExpiresAt <= now))
        {
            try
            {
                await drafts.DeleteAsync(draft.Id, cancellationToken).ConfigureAwait(false);
                LogDeleted(logger, "draft", draft.Id, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogDeleteFailure(logger, "draft", draft.Id, null);
            }
        }

        var analysisRecords = await analyses.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var analysis in analysisRecords.Where(item => item.ExpiresAt <= now))
        {
            try
            {
                await analyses.DeleteAsync(analysis.Id, cancellationToken).ConfigureAwait(false);
                LogDeleted(logger, "analysis", analysis.Id, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogDeleteFailure(logger, "analysis", analysis.Id, null);
            }
        }
    }

}
