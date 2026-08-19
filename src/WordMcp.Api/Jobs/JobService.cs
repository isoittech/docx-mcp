using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WordMcp.Artifacts;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Drafts;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Jobs;

public sealed record PreviewImageData(int PageNumber, string MediaType, byte[] Data);

internal sealed record GenerationImageSnapshot(
    string FileId,
    string RelativePath,
    string Sha256,
    ResolvedInputFormat Format);

internal sealed record GenerationJobPayload(
    DocumentDefinition Definition,
    string? TemplateRelativePath,
    IReadOnlyList<GenerationImageSnapshot> Images);

public sealed class JobService(
    FileJobRepository repository,
    InputFileResolver inputFiles,
    TemplateRegistry templates,
    DraftService drafts,
    AnalysisRepository analyses,
    ScopeIdService scopes,
    DocumentSpecValidator documentValidator,
    StorageQuotaService quota,
    JobChannel queue,
    JobCancellationRegistry cancellationRegistry,
    ArtifactService artifacts,
    IOptions<WordMcpOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonFileStore.Options;
    private readonly SemaphoreSlim submissionGate = new(1, 1);
    private readonly SemaphoreSlim[] lineageLocks = Enumerable.Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly WordMcpOptions settings = options.Value;

    public Task<JobReceipt> SubmitAnalyzeAsync(
        CallerContext caller,
        string sourceFileId,
        CancellationToken cancellationToken) =>
        SubmitDocumentInputAsync(
            caller,
            sourceFileId,
            JobKind.Analyze,
            new AnalyzePayload(sourceFileId),
            cancellationToken);

    public Task<JobReceipt> SubmitRenderPreviewAsync(
        CallerContext caller,
        string sourceFileId,
        CancellationToken cancellationToken) =>
        SubmitDocumentInputAsync(
            caller,
            sourceFileId,
            JobKind.RenderPreview,
            new RenderPayload(sourceFileId),
            cancellationToken);

    public Task<JobReceipt> SubmitReplaceTextAsync(
        CallerContext caller,
        string analysisId,
        IReadOnlyList<TextReplacementRequest> replacements,
        CancellationToken cancellationToken)
    {
        if (replacements is null || replacements.Count == 0)
        {
            throw Invalid(
                "replacement_required",
                "$.replacements",
                "At least one replacement is required.",
                "Provide validated target_id, expected_text, replacement_text, and expected_match_count values.");
        }

        return SubmitAnalysisMutationAsync(
            caller,
            analysisId,
            JobKind.ReplaceText,
            new ReplacePayload(analysisId, replacements),
            cancellationToken);
    }

    public Task<JobReceipt> SubmitApplyEditsAsync(
        CallerContext caller,
        string analysisId,
        IReadOnlyList<AtomicEditRequest> edits,
        CancellationToken cancellationToken)
    {
        if (edits is null || edits.Count == 0)
        {
            throw Invalid(
                "edits_required",
                "$.edits",
                "At least one atomic edit is required.",
                "Provide one or more supported operations using target_id values from the analysis snapshot.");
        }

        return SubmitAnalysisMutationAsync(
            caller,
            analysisId,
            JobKind.ApplyEdits,
            new ApplyEditsPayload(analysisId, edits),
            cancellationToken);
    }

    public Task<JobReceipt> SubmitPopulateTemplateAsync(
        CallerContext caller,
        string sourceFileId,
        IReadOnlyList<TemplateFieldRequest> fields,
        CancellationToken cancellationToken)
    {
        if (fields is null || fields.Count == 0)
        {
            throw Invalid(
                "template_fields_required",
                "$.fields",
                "At least one template field is required.",
                "Provide a non-empty set of exact content-control tags and semantic runs.");
        }

        return SubmitDocumentInputAsync(
            caller,
            sourceFileId,
            JobKind.PopulateTemplate,
            new PopulateTemplatePayload(fields),
            cancellationToken);
    }

    public async Task<JobReceipt> SubmitFinishDocumentAsync(
        CallerContext caller,
        string draftId,
        CancellationToken cancellationToken)
    {
        var draftLock = GetMutationLock(draftId);
        await draftLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var draft = await drafts.AcquireCompletedAsync(caller, draftId, cancellationToken).ConfigureAwait(false);
            if (draft.SubmittedJobId is not null)
            {
                var existing = await GetOwnedAsync(caller, draft.SubmittedJobId, cancellationToken)
                    .ConfigureAwait(false);
                if (existing.Kind != JobKind.FinishDocument
                    || !string.Equals(existing.DraftId, draftId, StringComparison.Ordinal))
                {
                    throw Invalid(
                        "draft_submission_state_invalid",
                        "$.draft_id",
                        "The submitted draft is not bound to its original finish job.",
                        "Do not retry this draft; report the server state error to an administrator.");
                }

                return ExistingFinishReceipt(existing);
            }

            var receipt = await SubmitGenerationAsync(
                caller,
                draft.Definition,
                JobKind.FinishDocument,
                draftId,
                rootJobId: null,
                parentJobId: null,
                revisionRound: 0,
                revisedSections: [],
                copyInputsFrom: null,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await drafts.MarkSubmittedAsync(caller, draftId, receipt.JobId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                await CancelDurableJobAsync(receipt.JobId, "draft_submission_failed", CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            return receipt;
        }
        finally
        {
            draftLock.Release();
        }
    }

    private static JobReceipt ExistingFinishReceipt(WordJob job)
    {
        var terminal = job.State.IsTerminal();
        var nextTool = job.State == JobState.Succeeded
            ? "word_get_preview_images"
            : terminal
                ? "word_get_job"
                : "word_wait_for_job";
        return new JobReceipt(
            job.Id,
            job.State.ToContract(),
            terminal ? 0 : 45,
            nextTool);
    }

    public async Task<JobReceipt> SubmitInsertSectionsAsync(
        CallerContext caller,
        string jobId,
        SectionInsertRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        documentValidator.ValidateSectionBatch(request.Sections);
        var source = await GetDeclarativeSourceAsync(caller, jobId, cancellationToken).ConfigureAwait(false);
        var lineageLock = GetLineageLock(source);
        await lineageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentLineageHeadAsync(source, cancellationToken).ConfigureAwait(false);
            var definition = InsertSections(source.DocumentDefinition!, request);
            documentValidator.ValidateDefinition(definition, requireComplete: true);
            return await SubmitGenerationAsync(
                caller,
                definition,
                JobKind.InsertSections,
                draftId: null,
                GetRootJobId(source),
                source.Id,
                revisionRound: 0,
                revisedSections: [],
                copyInputsFrom: source,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lineageLock.Release();
        }
    }

    public async Task<JobReceipt> SubmitRefineSectionAsync(
        CallerContext caller,
        string jobId,
        LogicalSectionSpec replacement,
        bool userRequestedEdit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        documentValidator.ValidateSectionBatch([replacement]);
        var source = await GetDeclarativeSourceAsync(caller, jobId, cancellationToken).ConfigureAwait(false);
        var lineageLock = GetLineageLock(source);
        await lineageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentLineageHeadAsync(source, cancellationToken).ConfigureAwait(false);
            var definition = ReplaceSection(source.DocumentDefinition!, replacement);
            documentValidator.ValidateDefinition(definition, requireComplete: true);
            var (round, revised) = userRequestedEdit
                ? (0, (IReadOnlyList<string>)Array.Empty<string>())
                : NextRevision(source, replacement.SectionKey);
            return await SubmitGenerationAsync(
                caller,
                definition,
                JobKind.RefineSection,
                draftId: null,
                GetRootJobId(source),
                source.Id,
                round,
                revised,
                copyInputsFrom: source,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lineageLock.Release();
        }
    }

    public async Task<JobView> GetAsync(
        CallerContext caller,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await GetOwnedAsync(caller, jobId, cancellationToken).ConfigureAwait(false);
        return View(job);
    }

    public async Task<JobView> WaitAsync(
        CallerContext caller,
        string jobId,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        if (maximumWait <= TimeSpan.Zero || maximumWait > TimeSpan.FromSeconds(50))
        {
            throw Invalid(
                "wait_seconds_out_of_range",
                "$.wait_seconds",
                "Job waits must be between 1 and 50 seconds.",
                "Use 45 seconds normally and never exceed 50 seconds.");
        }

        // Resolve "latest" exactly once. A newer submission must not retarget an in-flight wait.
        var current = await GetOwnedAsync(caller, jobId, cancellationToken).ConfigureAwait(false);
        var concreteJobId = current.Id;
        if (current.State.IsTerminal())
        {
            return View(current);
        }

        var started = timeProvider.GetTimestamp();
        while (timeProvider.GetElapsedTime(started) < maximumWait)
        {
            var remaining = maximumWait - timeProvider.GetElapsedTime(started);
            await Task.Delay(Min(remaining, TimeSpan.FromMilliseconds(250)), cancellationToken)
                .ConfigureAwait(false);
            current = await GetOwnedAsync(caller, concreteJobId, cancellationToken).ConfigureAwait(false);
            if (current.State.IsTerminal())
            {
                break;
            }
        }

        return View(current);
    }

    public async Task<CancelResult> CancelAsync(
        CallerContext caller,
        string jobId,
        CancellationToken cancellationToken)
    {
        RejectLatest(jobId, "word_cancel_job");
        var owned = await GetOwnedAsync(caller, jobId, cancellationToken).ConfigureAwait(false);
        if (owned.State.IsTerminal())
        {
            return new CancelResult(owned.Id, false, owned.State.ToContract());
        }

        var now = timeProvider.GetUtcNow();
        var updated = await repository.UpdateAsync(
            owned.Id,
            current => current.State.IsTerminal()
                ? current
                : current with
                {
                    State = JobState.Canceled,
                    UpdatedAt = now,
                    Error = new JobError(
                        "canceled_by_user",
                        null,
                        "The job was canceled by its owner.",
                        "Submit a new job only if the operation is still required."),
                },
            cancellationToken).ConfigureAwait(false);
        var accepted = updated.State == JobState.Canceled && owned.State != JobState.Canceled;
        if (accepted)
        {
            cancellationRegistry.Cancel(updated.Id);
        }

        return new CancelResult(updated.Id, accepted, updated.State.ToContract());
    }

    public async Task<IReadOnlyList<PreviewImageData>> GetPreviewImagesAsync(
        CallerContext caller,
        string jobId,
        IReadOnlyList<int> pageNumbers,
        CancellationToken cancellationToken)
    {
        RejectLatest(jobId, "word_get_preview_images");
        if (pageNumbers is null || pageNumbers.Count is < 1 or > 4
            || pageNumbers.Distinct().Count() != pageNumbers.Count
            || pageNumbers.Any(page => page is < 1 or > 50))
        {
            throw Invalid(
                "preview_selection_invalid",
                "$.page_numbers",
                "Select between 1 and 4 distinct one-based page numbers from 1 through 50.",
                "Request all pages in batches of at most four without duplicates.");
        }

        var job = await GetOwnedAsync(caller, jobId, cancellationToken).ConfigureAwait(false);
        if (job.State != JobState.Succeeded || job.Result?.Artifacts is null)
        {
            throw Invalid(
                "job_not_ready",
                "$.job_id",
                "Preview images are available only for a successful job.",
                "Wait for the job to reach succeeded, then request every page in batches of one to four.");
        }

        if (!artifacts.IsRetained(job))
        {
            throw Invalid(
                "job_expired",
                "$.job_id",
                "The job artifacts have reached their retention deadline.",
                "Create a fresh document job; expired previews and downloads cannot be restored.");
        }

        var images = new List<PreviewImageData>(pageNumbers.Count);
        foreach (var pageNumber in pageNumbers)
        {
            var artifact = job.Result.Artifacts.SingleOrDefault(candidate =>
                candidate.Kind == "preview" && ParsePreviewPage(candidate.FileName) == pageNumber);
            if (artifact is null || artifact.Bytes is <= 0 or > 8L * 1024 * 1024 || !File.Exists(artifact.Path))
            {
                throw Invalid(
                    "preview_page_not_found",
                    "$.page_numbers",
                    $"Preview page {pageNumber} is not available for this job.",
                    "Use page numbers between 1 and page_count from this exact successful job.");
            }

            images.Add(new PreviewImageData(
                pageNumber,
                artifact.MediaType,
                await File.ReadAllBytesAsync(artifact.Path, cancellationToken).ConfigureAwait(false)));
        }

        return images;
    }

    internal static int? ParsePreviewPage(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Path.GetExtension(fileName).Equals(".png", StringComparison.OrdinalIgnoreCase)
               && stem.StartsWith("page-", StringComparison.Ordinal)
               && int.TryParse(stem.AsSpan("page-".Length), out var value)
            ? value
            : null;
    }

    internal static DocumentDefinition InsertSections(
        DocumentDefinition definition,
        SectionInsertRequest request)
    {
        if (request.Sections is null || request.Sections.Count == 0)
        {
            throw Invalid(
                "sections_required",
                "$.sections",
                "At least one complete logical section is required.",
                "Provide only the new sections to insert.");
        }

        var existingKeys = definition.Sections.Select(section => section.SectionKey).ToHashSet(StringComparer.Ordinal);
        if (request.Sections.Any(section => !existingKeys.Add(section.SectionKey)))
        {
            throw Invalid(
                "duplicate_section_key",
                "$.sections",
                "Inserted section keys must be unique across the document.",
                "Choose new stable section_key values that are not already present.");
        }

        var index = request.Position switch
        {
            "start" when request.AfterSectionKey is null => 0,
            "end" when request.AfterSectionKey is null => definition.Sections.Count,
            "after" when !string.IsNullOrWhiteSpace(request.AfterSectionKey) =>
                FindSectionIndex(definition, request.AfterSectionKey) + 1,
            _ => throw Invalid(
                "insert_position_invalid",
                "$.position",
                "position and after_section_key do not form a supported insertion point.",
                "Use position=start or end without after_section_key, or position=after with an existing section key."),
        };
        var combined = definition.Sections.Take(index)
            .Concat(request.Sections)
            .Concat(definition.Sections.Skip(index))
            .ToArray();
        return definition with
        {
            ExpectedSectionCount = combined.Length,
            Sections = combined,
        };
    }

    internal static DocumentDefinition ReplaceSection(
        DocumentDefinition definition,
        LogicalSectionSpec replacement)
    {
        var index = FindSectionIndex(definition, replacement.SectionKey);
        var sections = definition.Sections.ToArray();
        sections[index] = replacement;
        return definition with { Sections = sections };
    }

    private async Task<JobReceipt> SubmitDocumentInputAsync<TPayload>(
        CallerContext caller,
        string sourceFileId,
        JobKind kind,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceFileId))
        {
            throw Invalid(
                "source_file_required",
                "$.source_file_id",
                "A source file reference is required.",
                "Use latest, default where supported, an opaque upload file_id, or an owned document artifact_id.");
        }

        var scope = scopes.Create(caller);
        return await SubmitAsync(
            scope,
            kind,
            payload,
            inputPathFactory: async (jobId, jobDirectory, token) =>
            {
                var snapshotDirectory = Path.Combine(jobDirectory, "input");
                return string.Equals(sourceFileId, "default", StringComparison.OrdinalIgnoreCase)
                    ? await templates.SnapshotDefaultAsync(snapshotDirectory, token).ConfigureAwait(false)
                    : await inputFiles.ResolveDocumentAsync(
                        caller,
                        scope,
                        sourceFileId,
                        snapshotDirectory,
                        token).ConfigureAwait(false);
            },
            documentDefinition: null,
            draftId: null,
            rootJobIdFactory: null,
            parentJobId: null,
            revisionRound: 0,
            revisedSections: [],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JobReceipt> SubmitAnalysisMutationAsync<TPayload>(
        CallerContext caller,
        string analysisId,
        JobKind kind,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var scope = scopes.Create(caller);
        var analysis = await analyses.GetOwnedAsync(scope, analysisId, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        return await SubmitAsync(
            scope,
            kind,
            payload,
            async (jobId, jobDirectory, token) => await SnapshotAnalysisAsync(
                analysis,
                Path.Combine(jobDirectory, "input"),
                token).ConfigureAwait(false),
            documentDefinition: null,
            draftId: null,
            rootJobIdFactory: null,
            parentJobId: null,
            revisionRound: 0,
            revisedSections: [],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JobReceipt> SubmitGenerationAsync(
        CallerContext caller,
        DocumentDefinition definition,
        JobKind kind,
        string? draftId,
        string? rootJobId,
        string? parentJobId,
        int revisionRound,
        IReadOnlyList<string> revisedSections,
        WordJob? copyInputsFrom,
        CancellationToken cancellationToken)
    {
        var scope = scopes.Create(caller);
        return await SubmitAsync(
            scope,
            kind,
            payload: definition,
            inputPathFactory: async (jobId, jobDirectory, token) =>
            {
                GenerationJobPayload generation;
                if (copyInputsFrom is not null)
                {
                    generation = await CopyGenerationInputsAsync(copyInputsFrom, jobDirectory, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    generation = await SnapshotGenerationInputsAsync(caller, scope, definition, jobDirectory, token)
                        .ConfigureAwait(false);
                }

                await JsonFileStore.WriteAtomicAsync(
                    Path.Combine(jobDirectory, "generation-inputs.json"),
                    generation with { Definition = definition },
                    token).ConfigureAwait(false);
                return generation.TemplateRelativePath is null
                    ? null
                    : await DescribeExistingSnapshotAsync(
                        "template",
                        Path.Combine(jobDirectory, generation.TemplateRelativePath),
                        token).ConfigureAwait(false);
            },
            definition,
            draftId,
            rootJobIdFactory: rootJobId is null ? static id => id : _ => rootJobId,
            parentJobId,
            revisionRound,
            revisedSections,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JobReceipt> SubmitAsync<TPayload>(
        CallerScope scope,
        JobKind kind,
        TPayload payload,
        Func<string, string, CancellationToken, Task<ResolvedInputSnapshot?>> inputPathFactory,
        DocumentDefinition? documentDefinition,
        string? draftId,
        Func<string, string?>? rootJobIdFactory,
        string? parentJobId,
        int revisionRound,
        IReadOnlyList<string> revisedSections,
        CancellationToken cancellationToken)
    {
        await submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? jobId = null;
        try
        {
            await quota.EnsureCanCreateAsync(scope, kind, cancellationToken).ConfigureAwait(false);
            var jobs = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
            var queuedCount = jobs.Count(job => job.State == JobState.Queued);
            if (queuedCount >= settings.MaxQueueDepth)
            {
                throw Invalid(
                    "queue_full",
                    "$",
                    "The persistent Word job queue is full.",
                    "Wait for an existing job to finish before retrying the submission.");
            }

            jobId = Identifier.New("job_");
            var directory = repository.CreateJobDirectory(jobId);
            var input = await inputPathFactory(jobId, directory, cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var reservedBytes = quota.GetJobReservationBytes(kind);
            var job = new WordJob(
                jobId,
                scope.UserScope,
                scope.ConversationScope,
                kind,
                JobState.Queued,
                JsonSerializer.SerializeToElement(payload, SerializerOptions),
                input?.Path,
                input?.Sha256,
                rootJobIdFactory?.Invoke(jobId),
                parentJobId,
                revisionRound,
                revisedSections,
                now,
                now,
                now.AddDays(settings.RetentionDays),
                DraftId: draftId,
                DocumentDefinition: documentDefinition,
                ReservedBytes: reservedBytes);
            await quota.StoreJobAsync(
                job,
                token => repository.CreateAsync(job, token),
                cancellationToken).ConfigureAwait(false);
            if (!queue.TryEnqueue(jobId))
            {
                throw Invalid(
                    "queue_full",
                    "$",
                    "The persistent Word job queue is full.",
                    "Wait for an existing job to finish before retrying the submission.");
            }

            jobId = null;
            return new JobReceipt(job.Id);
        }
        catch
        {
            if (jobId is not null)
            {
                TryDeleteUnpublishedJob(jobId);
            }

            throw;
        }
        finally
        {
            submissionGate.Release();
        }
    }

    private async Task<GenerationJobPayload> SnapshotGenerationInputsAsync(
        CallerContext caller,
        CallerScope scope,
        DocumentDefinition definition,
        string jobDirectory,
        CancellationToken cancellationToken)
    {
        string? templateRelativePath = null;
        if (!string.Equals(definition.TemplateSource, "none", StringComparison.OrdinalIgnoreCase))
        {
            var templateDirectory = Path.Combine(jobDirectory, "template");
            var template = string.Equals(definition.TemplateSource, "default", StringComparison.OrdinalIgnoreCase)
                ? await templates.SnapshotDefaultAsync(templateDirectory, cancellationToken).ConfigureAwait(false)
                : await inputFiles.ResolveDocumentAsync(
                    caller,
                    scope,
                    definition.TemplateSource,
                    templateDirectory,
                    cancellationToken).ConfigureAwait(false);
            templateRelativePath = ToJobRelativePath(jobDirectory, template.Path);
        }

        var imageIds = definition.Sections
            .SelectMany(section => section.Blocks)
            .Where(block => block.Kind == DocumentBlockKind.Image)
            .Select(block => block.ImageFileId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var images = new List<GenerationImageSnapshot>(imageIds.Length);
        for (var index = 0; index < imageIds.Length; index++)
        {
            var image = await inputFiles.ResolveImageAsync(
                caller,
                imageIds[index],
                Path.Combine(jobDirectory, "images", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                cancellationToken).ConfigureAwait(false);
            images.Add(new GenerationImageSnapshot(
                imageIds[index],
                ToJobRelativePath(jobDirectory, image.Path),
                image.Sha256,
                image.Format));
        }

        return new GenerationJobPayload(definition, templateRelativePath, images);
    }

    private async Task<GenerationJobPayload> CopyGenerationInputsAsync(
        WordJob source,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var sourcePayloadPath = Path.Combine(repository.GetJobDirectory(source.Id), "generation-inputs.json");
        var sourcePayload = await JsonFileStore.ReadAsync<GenerationJobPayload>(sourcePayloadPath, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Invalid(
                "generation_inputs_expired",
                "$.job_id",
                "The source document inputs are no longer available.",
                "Use the latest successful declarative document before its retention period expires.");
        var copiedImages = new List<GenerationImageSnapshot>(sourcePayload.Images.Count);
        string? copiedTemplate = null;
        if (sourcePayload.TemplateRelativePath is not null)
        {
            var sourceTemplate = ResolveJobRelativePath(repository.GetJobDirectory(source.Id), sourcePayload.TemplateRelativePath);
            var destination = Path.Combine(targetDirectory, "template", Path.GetFileName(sourceTemplate));
            await CopyTrustedFileAsync(sourceTemplate, destination, settings.MaxFileBytes, null, cancellationToken)
                .ConfigureAwait(false);
            copiedTemplate = ToJobRelativePath(targetDirectory, destination);
        }

        for (var index = 0; index < sourcePayload.Images.Count; index++)
        {
            var image = sourcePayload.Images[index];
            var sourcePath = ResolveJobRelativePath(repository.GetJobDirectory(source.Id), image.RelativePath);
            var destination = Path.Combine(
                targetDirectory,
                "images",
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFileName(sourcePath));
            var copied = await CopyTrustedFileAsync(
                sourcePath,
                destination,
                settings.MaxImageBytes,
                image.Sha256,
                cancellationToken).ConfigureAwait(false);
            copiedImages.Add(image with
            {
                RelativePath = ToJobRelativePath(targetDirectory, destination),
                Sha256 = copied.Sha256,
            });
        }

        return new GenerationJobPayload(sourcePayload.Definition, copiedTemplate, copiedImages);
    }

    private async Task<ResolvedInputSnapshot> SnapshotAnalysisAsync(
        AnalysisSnapshot analysis,
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        var extension = NormalizeDocumentExtension(Path.GetExtension(analysis.SourceFileName));
        var destination = Path.Combine(snapshotDirectory, $"source{extension}");
        var copy = await CopyTrustedFileAsync(
            analysis.SourcePath,
            destination,
            settings.MaxFileBytes,
            analysis.SourceSha256,
            cancellationToken).ConfigureAwait(false);
        return new ResolvedInputSnapshot(
            analysis.Id,
            destination,
            copy.Sha256,
            copy.Bytes,
            ParseDocumentFormat(extension));
    }

    private async Task<ResolvedInputSnapshot> DescribeExistingSnapshotAsync(
        string sourceId,
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > settings.MaxFileBytes)
        {
            throw Invalid(
                "input_snapshot_missing",
                "$",
                "A required immutable job input is missing.",
                "Resubmit the job while the source document is still available.");
        }

        var sha = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        var extension = NormalizeDocumentExtension(info.Extension);
        return new ResolvedInputSnapshot(sourceId, path, sha, info.Length, ParseDocumentFormat(extension));
    }

    private async Task<WordJob> GetDeclarativeSourceAsync(
        CallerContext caller,
        string jobId,
        CancellationToken cancellationToken)
    {
        var scope = scopes.Create(caller);
        var job = string.Equals(jobId, "latest", StringComparison.OrdinalIgnoreCase)
            ? await repository.LatestAsync(scope, successfulDeclarativeOnly: true, cancellationToken)
                .ConfigureAwait(false)
            : await repository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.UserScope != scope.UserScope || job.ConversationScope != scope.ConversationScope)
        {
            throw JobNotFound();
        }

        if (job.State != JobState.Succeeded || job.DocumentDefinition is null
            || job.Kind is not (JobKind.FinishDocument or JobKind.InsertSections or JobKind.RefineSection))
        {
            throw Invalid(
                "document_job_not_mutable",
                "$.job_id",
                "Only a successful declarative document job can be inserted into or refined.",
                "Use latest or an exact successful finish/insert/refine job_id from this conversation.");
        }

        return job;
    }

    private async Task<WordJob> GetOwnedAsync(
        CallerContext caller,
        string jobId,
        CancellationToken cancellationToken)
    {
        var scope = scopes.Create(caller);
        var job = string.Equals(jobId, "latest", StringComparison.OrdinalIgnoreCase)
            ? await repository.LatestAsync(scope, successfulDeclarativeOnly: false, cancellationToken)
                .ConfigureAwait(false)
            : await repository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.UserScope != scope.UserScope || job.ConversationScope != scope.ConversationScope)
        {
            throw JobNotFound();
        }

        return job;
    }

    private async Task EnsureCurrentLineageHeadAsync(WordJob source, CancellationToken cancellationToken)
    {
        var root = GetRootJobId(source);
        var jobs = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
        var lineage = jobs.Where(job =>
                job.UserScope == source.UserScope
                && job.ConversationScope == source.ConversationScope
                && job.DocumentDefinition is not null
                && GetRootJobId(job) == root)
            .ToArray();
        if (lineage.Any(job => job.Id != source.Id && job.State is JobState.Queued or JobState.Running))
        {
            throw Invalid(
                "document_operation_in_progress",
                "$.job_id",
                "Another operation in this document lineage is queued or running.",
                "Wait for that job and continue from its successful result.");
        }

        var latest = lineage.Where(job => job.State == JobState.Succeeded)
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is not null && latest.Id != source.Id)
        {
            throw Invalid(
                "document_job_superseded",
                "$.job_id",
                "The selected document is not the latest successful version in its lineage.",
                "Use job_id=latest so all prior insertions and refinements are preserved.");
        }
    }

    private static (int Round, IReadOnlyList<string> Revised) NextRevision(WordJob source, string sectionKey)
    {
        var revised = source.RevisedSections ?? [];
        var nextRound = source.RevisionRound == 0
            ? 1
            : revised.Contains(sectionKey, StringComparer.Ordinal)
                ? source.RevisionRound + 1
                : source.RevisionRound;
        if (nextRound > 2)
        {
            throw Invalid(
                "section_refinement_limit_reached",
                "$.section.section_key",
                "This section has already been refined in both permitted automatic visual-review rounds.",
                "Stop the autonomous refinement loop and return the latest successful document.");
        }

        var nextRevised = nextRound == source.RevisionRound
            ? revised.Append(sectionKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            : [sectionKey];
        return (nextRound, nextRevised);
    }

    private JobView View(WordJob job) => new(
        job.Id,
        job.Kind.ToContract(),
        job.State.ToContract(),
        job.CreatedAt,
        job.UpdatedAt,
        ResultWithSectionKeys(job),
        job.Error,
        artifacts.CreateLinks(job),
        NextTool(job));

    private static JobResult? ResultWithSectionKeys(WordJob job)
    {
        if (job.Result is null || job.DocumentDefinition is null
            || job.Kind is not (JobKind.FinishDocument or JobKind.InsertSections or JobKind.RefineSection))
        {
            return job.Result;
        }

        return job.Result with
        {
            SectionKeys = job.DocumentDefinition.Sections
                .Select(static section => section.SectionKey)
                .ToArray(),
        };
    }

    private static string? NextTool(WordJob job) => job.State switch
    {
        JobState.Queued or JobState.Running => "word_wait_for_job",
        JobState.Succeeded when job.Kind == JobKind.Analyze => "word_get_analysis_chunk",
        JobState.Succeeded when job.Result?.PageCount > 0 => "word_get_preview_images",
        _ => null,
    };

    private async Task CancelDurableJobAsync(string jobId, string code, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await repository.UpdateAsync(
            jobId,
            current => current.State.IsTerminal()
                ? current
                : current with
                {
                    State = JobState.Canceled,
                    UpdatedAt = now,
                    Error = new JobError(
                        code,
                        null,
                        "The durable submission could not be completed.",
                        "Retry the operation after checking the referenced draft."),
                },
            cancellationToken).ConfigureAwait(false);
        cancellationRegistry.Cancel(jobId);
    }

    private static int FindSectionIndex(DocumentDefinition definition, string? sectionKey)
    {
        var index = string.IsNullOrWhiteSpace(sectionKey)
            ? -1
            : definition.Sections.ToList().FindIndex(section => section.SectionKey == sectionKey);
        if (index < 0)
        {
            throw Invalid(
                "section_not_found",
                "$.section_key",
                "The requested logical section does not exist in the source document definition.",
                "Use an exact section_key from the latest successful declarative job.");
        }

        return index;
    }

    private static string GetRootJobId(WordJob job) =>
        string.IsNullOrWhiteSpace(job.RootJobId) ? job.Id : job.RootJobId;

    private SemaphoreSlim GetLineageLock(WordJob job) => GetMutationLock(GetRootJobId(job));

    private SemaphoreSlim GetMutationLock(string identifier)
    {
        var hash = (uint)StringComparer.Ordinal.GetHashCode(identifier);
        return lineageLocks[hash % (uint)lineageLocks.Length];
    }

    private void TryDeleteUnpublishedJob(string jobId)
    {
        try
        {
            var directory = repository.GetJobDirectory(jobId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ToJobRelativePath(string jobDirectory, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(jobDirectory), Path.GetFullPath(path));
        if (Path.IsPathFullyQualified(relative) || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A resolved snapshot escaped its job directory.");
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ResolveJobRelativePath(string jobDirectory, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException("A persisted job snapshot path must be relative.");
        }

        var root = Path.GetFullPath(jobDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(jobDirectory, relativePath));
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A persisted job snapshot path escaped its job directory.");
        }

        return path;
    }

    private static string NormalizeDocumentExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" => ".docx",
        ".dotx" => ".dotx",
        _ => ".docx",
    };

    private static ResolvedInputFormat ParseDocumentFormat(string extension) =>
        extension.Equals(".dotx", StringComparison.OrdinalIgnoreCase)
            ? ResolvedInputFormat.Dotx
            : ResolvedInputFormat.Docx;

    private static async Task<(string Sha256, long Bytes)> CopyTrustedFileAsync(
        string sourcePath,
        string destinationPath,
        long maximumBytes,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists || source.Length <= 0 || source.Length > maximumBytes)
        {
            throw Invalid(
                "source_expired",
                "$",
                "A referenced source snapshot is missing or outside the supported size limit.",
                "Resubmit using a currently available opaque source identifier.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using (var input = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var hash = await HashFileAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        if (expectedSha256 is not null && !FixedTimeEqualsHex(expectedSha256, hash))
        {
            File.Delete(destinationPath);
            throw Invalid(
                "source_snapshot_changed",
                "$",
                "A persisted source no longer matches its immutable snapshot hash.",
                "Run analysis again or continue from a current successful document job.");
        }

        return (hash, new FileInfo(destinationPath).Length);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

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

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static void RejectLatest(string jobId, string toolName)
    {
        if (string.Equals(jobId, "latest", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                "concrete_job_id_required",
                "$.job_id",
                $"{toolName} requires a concrete job identifier.",
                "Use the exact opaque job_id returned by submission, word_get_job, or word_wait_for_job.");
        }
    }

    private static WordMcpException JobNotFound() => Invalid(
        "job_not_found",
        "$.job_id",
        "The job was not found in this caller scope.",
        "Use a job_id returned in this conversation.");

    private static WordMcpException Invalid(string code, string field, string message, string correction) =>
        new(code, field, message, correction);

    public void Dispose()
    {
        submissionGate.Dispose();
        foreach (var lineageLock in lineageLocks)
        {
            lineageLock.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
