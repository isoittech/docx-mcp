using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WordMcp.Artifacts;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Rendering;
using WordMcp.Storage;
using WordMcp.Word;

namespace WordMcp.Jobs;

public sealed class JobWorker(
    JobChannel queue,
    FileJobRepository repository,
    IWordDocumentEngine engine,
    AnalysisRepository analyses,
    StorageQuotaService quota,
    DocumentRenderer renderer,
    DocxPackageGuard packageGuard,
    ArtifactService artifactService,
    JobCancellationRegistry cancellationRegistry,
    IOptions<WordMcpOptions> options,
    TimeProvider timeProvider,
    ILogger<JobWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonFileStore.Options;
    private static readonly Action<ILogger, string, string, string, Exception?> LogFailure =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Error,
            new EventId(1001, nameof(LogFailure)),
            "Word job {JobId} failed with code {ErrorCode} ({ExceptionType}).");
    private readonly WordMcpOptions settings = options.Value;
    private readonly SemaphoreSlim[] mutationLocks = Enumerable.Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, settings.MaxConcurrentJobs)
            .Select(_ => RunWorkerAsync(stoppingToken))
            .ToArray();
        await RestoreQueuedJobsAsync(stoppingToken).ConfigureAwait(false);
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RestoreQueuedJobsAsync(CancellationToken cancellationToken)
    {
        var storedJobs = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var storedJob in storedJobs)
        {
            await repository.CleanupUnpublishedRunsAsync(storedJob.Id, cancellationToken).ConfigureAwait(false);
        }

        var recoverable = storedJobs
            .Where(job => job.State is JobState.Queued or JobState.Running)
            .OrderBy(job => job.CreatedAt)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var job in recoverable)
        {
            var restored = await repository.UpdateAsync(
                job.Id,
                current => current.State is JobState.Queued or JobState.Running
                    ? current with
                    {
                        State = JobState.Queued,
                        UpdatedAt = timeProvider.GetUtcNow(),
                        Result = null,
                        Error = null,
                        ReservedBytes = quota.GetJobReservationBytes(current.Kind),
                        PublishedRunId = null,
                    }
                    : current,
                cancellationToken).ConfigureAwait(false);
            if (restored.State == JobState.Queued)
            {
                // Readers already run, so even a malformed store with more than the normal
                // queue(12)+running(3) set cannot deadlock recovery or silently drop a job.
                await queue.EnqueueAsync(restored.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(jobId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Do not log exception messages: they can contain document text or local paths.
                LogFailure(logger, jobId, "worker_boundary_failure", exception.GetType().Name, null);
                await TryFailBoundaryAsync(jobId).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(string jobId, CancellationToken stoppingToken)
    {
        var candidate = await repository.GetAsync(jobId, stoppingToken).ConfigureAwait(false);
        if (candidate is null || candidate.State != JobState.Queued)
        {
            return;
        }

        var running = await repository.UpdateAsync(
            jobId,
            current => current.State == JobState.Queued
                ? current with
                {
                    State = JobState.Running,
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Result = null,
                    Error = null,
                    ReservedBytes = quota.GetJobReservationBytes(current.Kind),
                    PublishedRunId = null,
                }
                : current,
            stoppingToken).ConfigureAwait(false);
        if (running.State != JobState.Running)
        {
            return;
        }

        var userCancellation = cancellationRegistry.Register(jobId);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMinutes(settings.JobTimeoutMinutes),
            timeProvider);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            userCancellation.Token,
            timeout.Token);
        SemaphoreSlim? mutationLock = null;
        var mutationLockAcquired = false;
        try
        {
            await repository.CleanupUnpublishedRunsAsync(jobId, execution.Token).ConfigureAwait(false);
            mutationLock = GetMutationLock(running);
            if (mutationLock is not null)
            {
                await mutationLock.WaitAsync(execution.Token).ConfigureAwait(false);
                mutationLockAcquired = true;
            }

            var current = await repository.GetAsync(jobId, execution.Token).ConfigureAwait(false);
            if (current is null || current.State != JobState.Running)
            {
                userCancellation.Cancel();
                return;
            }

            var executionResult = await ExecuteJobAsync(current, execution.Token).ConfigureAwait(false);
            await quota.EnsureJobWithinReservationAsync(jobId, execution.Token).ConfigureAwait(false);
            var completed = await repository.UpdateAsync(
                jobId,
                persisted => persisted.State == JobState.Running
                    ? persisted with
                    {
                        State = JobState.Succeeded,
                        Result = executionResult.Result,
                        Error = null,
                        UpdatedAt = timeProvider.GetUtcNow(),
                        PublishedRunId = executionResult.PublishedRunId,
                    }
                    : persisted,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Preserve Running so startup recovery can atomically return it to Queued.
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await CompleteWithErrorAsync(
                jobId,
                JobState.TimedOut,
                new JobError(
                    "job_timeout",
                    null,
                    "The job exceeded its execution time limit.",
                    "Reduce document complexity or split the operation into smaller jobs."),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var persisted = await repository.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (persisted?.State != JobState.Canceled)
            {
                await CompleteWithErrorAsync(
                    jobId,
                    JobState.Canceled,
                    new JobError(
                        "job_canceled",
                        null,
                        "The job execution was canceled.",
                        "Submit a new job if the operation is still required."),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (TimeoutException exception)
        {
            LogFailure(logger, jobId, "job_timeout", exception.GetType().Name, null);
            await CompleteWithErrorAsync(
                jobId,
                JobState.TimedOut,
                new JobError(
                    "job_timeout",
                    null,
                    "A bounded document subprocess exceeded its execution time limit.",
                    "Reduce document complexity or split the operation into smaller jobs."),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (WordMcpException exception)
        {
            var state = exception.UnsafeDocument ? JobState.RejectedUnsafeDocument : JobState.Failed;
            await CompleteWithErrorAsync(
                jobId,
                state,
                new JobError(
                    exception.Code,
                    PartKind(exception),
                    exception.Message,
                    exception.Correction),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            LogFailure(logger, jobId, "invalid_job_payload", exception.GetType().Name, null);
            await CompleteWithErrorAsync(
                jobId,
                JobState.Failed,
                new JobError(
                    "invalid_job_payload",
                    null,
                    "The persisted job payload is invalid.",
                    "Submit the operation again with a valid bounded request."),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFailure(logger, jobId, "internal_error", exception.GetType().Name, null);
            await CompleteWithErrorAsync(
                jobId,
                JobState.Failed,
                new JobError(
                    "internal_error",
                    null,
                    "The Word job failed unexpectedly.",
                    "Review the structured job error and retry only with a fresh safe input snapshot."),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (mutationLockAcquired)
            {
                mutationLock!.Release();
            }

            cancellationRegistry.Remove(jobId);
            await TryCleanupUnpublishedRunsAsync(jobId).ConfigureAwait(false);
        }
    }

    private async Task<JobExecutionResult> ExecuteJobAsync(WordJob job, CancellationToken cancellationToken)
    {
        DocxPackageInspection? inputInspection = null;
        if (job.InputPath is not null)
        {
            inputInspection = await ValidateDocumentSnapshotAsync(job, cancellationToken).ConfigureAwait(false);
        }

        if (job.Kind == JobKind.Analyze)
        {
            return await AnalyzeAsync(job, cancellationToken).ConfigureAwait(false);
        }

        var directory = repository.GetJobDirectory(job.Id);
        var runId = Identifier.New("run_");
        var runDirectory = Path.Combine(directory, "runs", runId);
        Directory.CreateDirectory(runDirectory);
        var result = job.Kind switch
        {
            JobKind.RenderPreview => await RenderSourceAsync(
                    job,
                    runDirectory,
                    inputInspection ?? throw InvalidPayload("input_snapshot_missing", "The render input was not inspected."),
                    cancellationToken)
                .ConfigureAwait(false),
            JobKind.ReplaceText => await ReplaceTextAsync(job, runDirectory, cancellationToken).ConfigureAwait(false),
            JobKind.ApplyEdits => await ApplyEditsAsync(job, runDirectory, cancellationToken).ConfigureAwait(false),
            JobKind.PopulateTemplate => await PopulateTemplateAsync(job, runDirectory, cancellationToken)
                .ConfigureAwait(false),
            JobKind.FinishDocument or JobKind.InsertSections or JobKind.RefineSection =>
                await GenerateAsync(job, runDirectory, cancellationToken).ConfigureAwait(false),
            _ => throw InvalidPayload("unsupported_job_kind", "The persisted job kind is not supported."),
        };
        return result with { PublishedRunId = runId };
    }

    private async Task<JobExecutionResult> AnalyzeAsync(WordJob job, CancellationToken cancellationToken)
    {
        var inputPath = RequiredInputPath(job);
        var sha256 = RequiredInputSha(job);
        var scope = new CallerScope(job.UserScope, job.ConversationScope);
        var cached = await analyses.TryGetCacheAsync(scope, sha256, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        AnalysisSnapshot snapshot;
        AnalysisCacheRecord? cacheToStore = null;
        if (cached is not null)
        {
            snapshot = CreateSnapshotFromCache(
                cached,
                inputPath,
                LogicalSourceFileName(inputPath),
                timeProvider.GetUtcNow());
        }
        else
        {
            snapshot = engine.Analyze(
                new WordAnalysisRequest(
                    inputPath,
                    LogicalSourceFileName(inputPath),
                    sha256,
                    job.UserScope,
                    job.ConversationScope),
                cancellationToken);
            var now = timeProvider.GetUtcNow();
            snapshot = snapshot with { LastAccessedAt = now };
            cacheToStore = CreateCacheRecord(snapshot, now);
        }

        await PersistAnalysisAsync(snapshot, cacheToStore, cancellationToken).ConfigureAwait(false);
        return new JobExecutionResult(
            new JobResult(
                AnalysisId: snapshot.Id,
                AnalysisSummary: snapshot.Summary,
                SourceSha256: snapshot.SourceSha256));
    }

    private AnalysisSnapshot CreateSnapshotFromCache(
        AnalysisCacheRecord cached,
        string sourcePath,
        string sourceFileName,
        DateTimeOffset now)
    {
        var analysisId = Identifier.New("ana_");
        var expiresAt = now.AddMinutes(settings.AnalysisLifetimeMinutes);
        var targetIds = cached.Targets.Keys.ToDictionary(
            static id => id,
            static _ => Identifier.New("tgt_"),
            StringComparer.Ordinal);
        var targets = cached.Targets.ToDictionary(
            pair => targetIds[pair.Key],
            pair => pair.Value with { TargetId = targetIds[pair.Key] },
            StringComparer.Ordinal);
        var items = cached.Items.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<AnalysisItem>)pair.Value.Select(item => item with
            {
                TargetId = item.TargetId is null ? null : targetIds[item.TargetId],
            }).ToArray(),
            StringComparer.Ordinal);
        var summary = cached.Summary with
        {
            AnalysisId = analysisId,
            SourceSha256 = cached.SourceSha256,
            ExpiresAt = expiresAt,
        };
        return new AnalysisSnapshot(
            analysisId,
            cached.UserScope,
            cached.ConversationScope,
            cached.SourceSha256,
            Path.GetFullPath(sourcePath),
            sourceFileName,
            summary,
            items,
            targets,
            now,
            expiresAt,
            LastAccessedAt: now);
    }

    private AnalysisCacheRecord CreateCacheRecord(AnalysisSnapshot snapshot, DateTimeOffset now) => new(
        AnalysisRepository.CreateCacheId(
            new CallerScope(snapshot.UserScope, snapshot.ConversationScope),
            snapshot.SourceSha256),
        snapshot.UserScope,
        snapshot.ConversationScope,
        snapshot.SourceSha256,
        snapshot.Summary,
        snapshot.Items,
        snapshot.Targets,
        now,
        now.AddMinutes(settings.AnalysisCacheLifetimeMinutes),
        LastAccessedAt: now);

    private Task PersistAnalysisAsync(
        AnalysisSnapshot snapshot,
        AnalysisCacheRecord? cache,
        CancellationToken cancellationToken) => quota.StoreAnalysisAsync(
            snapshot,
            cache,
            async token =>
            {
                if (cache is not null)
                {
                    await analyses.SaveCacheAsync(cache, token).ConfigureAwait(false);
                }

                await analyses.SaveAsync(snapshot, token).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task<JobExecutionResult> RenderSourceAsync(
        WordJob job,
        string runDirectory,
        DocxPackageInspection inspection,
        CancellationToken cancellationToken)
    {
        var render = await renderer.RenderAsync(
            RequiredInputPath(job),
            Path.Combine(runDirectory, "preview"),
            requireIndexUpdate: inspection.HasTocField,
            finalizeDocumentForDistribution: false,
            cancellationToken).ConfigureAwait(false);
        var artifacts = CreateRenderArtifacts(render, includeDocumentPath: null);
        return new JobExecutionResult(
            new JobResult(
                SourceSha256: RequiredInputSha(job),
                PageCount: render.PageCount,
                Artifacts: artifacts,
                Warnings: RenderWarnings(render)));
    }

    private async Task<JobExecutionResult> ReplaceTextAsync(
        WordJob job,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<ReplacePayload>(job);
        var analysis = await GetMutationAnalysisAsync(job, payload.AnalysisId, cancellationToken).ConfigureAwait(false);
        var outputPath = Path.Combine(runDirectory, "document.docx");
        var mutation = engine.ReplaceText(
            new WordMutationRequest(RequiredInputPath(job), outputPath, analysis),
            payload.Replacements,
            cancellationToken);
        return await CompleteMutationAsync(job, mutation, outputPath, runDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JobExecutionResult> ApplyEditsAsync(
        WordJob job,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<ApplyEditsPayload>(job);
        var analysis = await GetMutationAnalysisAsync(job, payload.AnalysisId, cancellationToken).ConfigureAwait(false);
        var outputPath = Path.Combine(runDirectory, "document.docx");
        var mutation = engine.ApplyEdits(
            new WordMutationRequest(RequiredInputPath(job), outputPath, analysis),
            payload.Edits,
            cancellationToken);
        return await CompleteMutationAsync(job, mutation, outputPath, runDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JobExecutionResult> PopulateTemplateAsync(
        WordJob job,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<PopulateTemplatePayload>(job);
        var inputPath = RequiredInputPath(job);
        var outputPath = Path.Combine(runDirectory, "document.docx");
        var mutation = engine.PopulateTemplate(
            new WordTemplatePopulationRequest(
                inputPath,
                outputPath,
                LogicalSourceFileName(inputPath),
                RequiredInputSha(job),
                job.UserScope,
                job.ConversationScope),
            payload.Fields,
            cancellationToken);
        return await CompleteMutationAsync(job, mutation, outputPath, runDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JobExecutionResult> CompleteMutationAsync(
        WordJob job,
        WordMutationResult mutation,
        string outputPath,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        EnsureAccepted(mutation.Validation, "existing_edit_validation_failed");
        var inspection = await packageGuard.ValidateSnapshotAsync(outputPath, cancellationToken).ConfigureAwait(false);
        var render = await renderer.RenderAsync(
            outputPath,
            Path.Combine(runDirectory, "preview"),
            requireIndexUpdate: inspection.HasTocField,
            finalizeDocumentForDistribution: false,
            cancellationToken).ConfigureAwait(false);
        await quota.EnsureJobWithinReservationAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var outputAnalysis = mutation.OutputAnalysis with { LastAccessedAt = now };
        await PersistAnalysisAsync(
            outputAnalysis,
            CreateCacheRecord(outputAnalysis, now),
            cancellationToken).ConfigureAwait(false);
        await analyses.InvalidateSourceAsync(
                new CallerScope(job.UserScope, job.ConversationScope),
                RequiredInputSha(job),
                now,
                outputAnalysis.Id,
                preserveSourceCache: FixedTimeEqualsHex(outputAnalysis.SourceSha256, RequiredInputSha(job)),
                cancellationToken)
            .ConfigureAwait(false);
        var artifacts = CreateRenderArtifacts(render, outputPath);
        return new JobExecutionResult(
            new JobResult(
                OutputAnalysisId: outputAnalysis.Id,
                AnalysisSummary: outputAnalysis.Summary,
                SourceSha256: mutation.OutputSha256,
                PageCount: render.PageCount,
                Artifacts: artifacts,
                Warnings: RenderWarnings(render)));
    }

    private async Task<JobExecutionResult> GenerateAsync(
        WordJob job,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var inputMetadataPath = Path.Combine(repository.GetJobDirectory(job.Id), "generation-inputs.json");
        var payload = await JsonFileStore.ReadAsync<GenerationJobPayload>(inputMetadataPath, cancellationToken)
            .ConfigureAwait(false)
            ?? throw InvalidPayload("invalid_job_payload", "The persisted generation input manifest is missing.");
        if (job.DocumentDefinition is null)
        {
            throw InvalidPayload("invalid_job_payload", "The persisted generation definition is missing.");
        }

        string? templatePath = null;
        if (payload.TemplateRelativePath is not null)
        {
            templatePath = ResolveJobPath(job.Id, payload.TemplateRelativePath);
            if (!string.Equals(templatePath, job.InputPath, StringComparison.Ordinal))
            {
                throw InvalidPayload("input_snapshot_mismatch", "The template manifest does not match the job snapshot.");
            }
        }

        var images = new Dictionary<string, WordImageAsset>(StringComparer.Ordinal);
        long totalImageBytes = 0;
        long totalPixels = 0;
        foreach (var image in payload.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveJobPath(job.Id, image.RelativePath);
            var inspection = await packageGuard.ValidateImageSnapshotAsync(path, image.Sha256, cancellationToken)
                .ConfigureAwait(false);
            totalImageBytes = checked(totalImageBytes + inspection.Bytes);
            totalPixels = checked(totalPixels + inspection.Pixels);
            if (totalImageBytes > settings.MaxTotalImageBytes || totalPixels > settings.MaxTotalImagePixels)
            {
                throw new WordMcpException(
                    "image_total_limit",
                    "$.sections[].blocks[].image_file_id",
                    "The document images exceed the aggregate byte or pixel limit.",
                    "Use fewer or smaller PNG/JPEG image uploads.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!images.TryAdd(
                    image.FileId,
                    new WordImageAsset(image.FileId, bytes, inspection.MediaType, image.Sha256)))
            {
                throw InvalidPayload("duplicate_image_snapshot", "The image manifest contains a duplicate file identifier.");
            }
        }

        var outputPath = Path.Combine(runDirectory, "document.docx");
        var generation = engine.Generate(
            new WordGenerationRequest(
                outputPath,
                job.DocumentDefinition,
                job.UserScope,
                job.ConversationScope,
                templatePath,
                images),
            cancellationToken);
        EnsureAccepted(generation.Validation, "new_document_validation_failed");
        await packageGuard.ValidateSnapshotAsync(outputPath, cancellationToken).ConfigureAwait(false);
        var render = await renderer.RenderAsync(
            outputPath,
            Path.Combine(runDirectory, "preview"),
            job.DocumentDefinition.Design.TableOfContents,
            finalizeDocumentForDistribution: true,
            cancellationToken).ConfigureAwait(false);
        var finalizedDocumentPath = render.FinalizedDocumentPath
                                    ?? throw InvalidPayload(
                                        "finalized_document_missing",
                                        "The renderer did not return the finalized generated document.");
        await packageGuard.ValidateSnapshotAsync(finalizedDocumentPath, cancellationToken).ConfigureAwait(false);
        EnsureAccepted(
            engine.ValidateNewDocument(finalizedDocumentPath, cancellationToken),
            "finalized_document_validation_failed");
        File.Move(finalizedDocumentPath, outputPath, overwrite: true);
        var outputSha256 = await ComputeSha256Async(outputPath, cancellationToken).ConfigureAwait(false);
        var outputAnalysis = engine.Analyze(
            new WordAnalysisRequest(
                outputPath,
                generation.OutputAnalysis.SourceFileName,
                outputSha256,
                job.UserScope,
                job.ConversationScope),
            cancellationToken);
        await quota.EnsureJobWithinReservationAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        outputAnalysis = outputAnalysis with { LastAccessedAt = now };
        await PersistAnalysisAsync(
            outputAnalysis,
            CreateCacheRecord(outputAnalysis, now),
            cancellationToken).ConfigureAwait(false);
        return new JobExecutionResult(
            new JobResult(
                OutputAnalysisId: outputAnalysis.Id,
                AnalysisSummary: outputAnalysis.Summary,
                SourceSha256: outputSha256,
                PageCount: render.PageCount,
                Artifacts: CreateRenderArtifacts(render, outputPath),
                Warnings: RenderWarnings(render),
                SectionKeys: job.DocumentDefinition.Sections
                    .Select(static section => section.SectionKey)
                    .ToArray()));
    }

    private static IReadOnlyList<string>? RenderWarnings(RenderResult render) =>
        render.Warnings.Count == 0 ? null : render.Warnings;

    private async Task<AnalysisSnapshot> GetMutationAnalysisAsync(
        WordJob job,
        string analysisId,
        CancellationToken cancellationToken)
    {
        var scope = new CallerScope(job.UserScope, job.ConversationScope);
        var analysis = await analyses.GetOwnedAsync(scope, analysisId, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        if (!FixedTimeEqualsHex(analysis.SourceSha256, RequiredInputSha(job)))
        {
            throw InvalidPayload("analysis_snapshot_mismatch", "The job input does not match the selected analysis snapshot.");
        }

        return analysis;
    }

    private List<ArtifactRecord> CreateRenderArtifacts(RenderResult render, string? includeDocumentPath)
    {
        var records = new List<ArtifactRecord>();
        if (includeDocumentPath is not null)
        {
            records.Add(artifactService.CreateRecord("document", includeDocumentPath, "document.docx"));
        }

        records.Add(artifactService.CreateRecord("pdf", render.PdfPath, "preview.pdf"));
        records.AddRange(render.PreviewPaths.Select(path => artifactService.CreateRecord(
            "preview",
            path,
            Path.GetFileName(path))));
        return records;
    }

    private async Task<DocxPackageInspection> ValidateDocumentSnapshotAsync(
        WordJob job,
        CancellationToken cancellationToken)
    {
        var path = RequiredInputPath(job);
        if (!File.Exists(path))
        {
            throw InvalidPayload("input_snapshot_missing", "The immutable document snapshot is missing.");
        }

        return await packageGuard.ValidateSnapshotAsync(path, RequiredInputSha(job), cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<WordJob> CompleteWithErrorAsync(
        string jobId,
        JobState state,
        JobError error,
        CancellationToken cancellationToken) =>
        repository.UpdateAsync(
            jobId,
            current => current.State.IsTerminal()
                ? current
                : current with
                {
                    State = state,
                    Result = null,
                    Error = error,
                    UpdatedAt = timeProvider.GetUtcNow(),
                    PublishedRunId = null,
                },
            cancellationToken);

    private async Task TryCleanupUnpublishedRunsAsync(string jobId)
    {
        try
        {
            await repository.CleanupUnpublishedRunsAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogFailure(logger, jobId, "run_cleanup_failure", exception.GetType().Name, null);
        }
    }

    private async Task TryFailBoundaryAsync(string jobId)
    {
        try
        {
            await CompleteWithErrorAsync(
                jobId,
                JobState.Failed,
                new JobError(
                    "worker_boundary_failure",
                    null,
                    "The worker could not complete the persisted job.",
                    "Retry the operation using a fresh job receipt."),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFailure(logger, jobId, "state_persistence_failure", exception.GetType().Name, null);
        }
    }

    private static T Deserialize<T>(WordJob job) =>
        job.Payload.Deserialize<T>(SerializerOptions)
        ?? throw InvalidPayload("invalid_job_payload", "The persisted job payload is missing required values.");

    private static void EnsureAccepted(OpenXmlValidationReport validation, string code)
    {
        if (!validation.IsAccepted)
        {
            throw new WordMcpException(
                code,
                "$",
                "The Open XML validation gate found new document errors.",
                "Use only supported semantic operations and inspect the source for unsupported structures.");
        }
    }

    private string ResolveJobPath(string jobId, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw InvalidPayload("invalid_job_payload", "A persisted snapshot path must be relative.");
        }

        var root = Path.GetFullPath(repository.GetJobDirectory(jobId)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw InvalidPayload("invalid_job_payload", "A persisted snapshot path escaped its job directory.");
        }

        return path;
    }

    private static string RequiredInputPath(WordJob job) => job.InputPath
        ?? throw InvalidPayload("input_snapshot_missing", "The job has no immutable document snapshot.");

    private static string RequiredInputSha(WordJob job) => job.InputSha256
        ?? throw InvalidPayload("input_snapshot_missing", "The job has no immutable input hash.");

    private static string LogicalSourceFileName(string path) =>
        Path.GetExtension(path).Equals(".dotx", StringComparison.OrdinalIgnoreCase)
            ? "source.dotx"
            : "source.docx";

    private static string? PartKind(WordMcpException exception) =>
        exception.FieldPath.StartsWith('$') ? null : exception.FieldPath;

    private SemaphoreSlim? GetMutationLock(WordJob job)
    {
        if (job.Kind is not (JobKind.ReplaceText or JobKind.ApplyEdits))
        {
            return null;
        }

        var key = job.InputSha256
            ?? throw InvalidPayload("input_snapshot_missing", "An analysis mutation has no immutable input hash.");
        var hash = (uint)StringComparer.Ordinal.GetHashCode(key);
        return mutationLocks[hash % (uint)mutationLocks.Length];
    }

    private static WordMcpException InvalidPayload(string code, string message) => new(
        code,
        "$",
        message,
        "Submit the operation again using opaque IDs from the current caller scope.");

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return leftBytes.Length == rightBytes.Length
                   && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private sealed record JobExecutionResult(
        JobResult Result,
        string? PublishedRunId = null);

    public override void Dispose()
    {
        foreach (var mutationLock in mutationLocks)
        {
            mutationLock.Dispose();
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
