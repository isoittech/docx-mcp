using System.Text.Json;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Drafts;

public sealed class DraftService(
    DraftRepository repository,
    FileJobRepository jobs,
    ScopeIdService scopes,
    DocumentSpecValidator validator,
    StorageQuotaService quota,
    IOptions<WordMcpOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan lifetime = TimeSpan.FromMinutes(options.Value.DraftLifetimeMinutes);

    public async Task<DraftView> StartAsync(
        CallerContext caller,
        DocumentDefinition definition,
        bool userRequestedNewWorkflow,
        CancellationToken cancellationToken)
    {
        validator.ValidateDefinition(definition, requireComplete: false);
        if (definition.Sections.Count != 0)
        {
            throw new WordMcpException(
                "sections_not_allowed_at_start",
                "$.sections",
                "word_start_document fixes document-wide settings but does not accept sections.",
                "Start with an empty section list, then call word_add_sections_to_draft.");
        }

        var scope = scopes.Create(caller);
        var messageScope = scopes.CreateMessageScope(caller);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            var existingDrafts = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
            var replay = existingDrafts
                .Where(draft => draft.UserScope == scope.UserScope
                                && draft.ConversationScope == scope.ConversationScope
                                && draft.ExpiresAt > now)
                .Where(draft => messageScope is not null
                    ? string.Equals(draft.OriginMessageScope, messageScope, StringComparison.Ordinal)
                    : draft.OriginMessageScope is null && DefinitionsMatch(draft.Definition, definition))
                .OrderByDescending(draft => draft.CreatedAt)
                .ThenByDescending(draft => draft.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (replay is not null)
            {
                var current = await repository.GetOwnedAsync(scope, replay.Id, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return View(current);
            }

            var latestSuccess = await jobs.LatestAsync(scope, successfulDeclarativeOnly: true, cancellationToken).ConfigureAwait(false);
            if (latestSuccess is not null && !userRequestedNewWorkflow)
            {
                throw new WordMcpException(
                    "successful_workflow_exists",
                    "$.user_requested_new_workflow",
                    "This conversation already has a successful declarative Word document.",
                    "Use insert/refine for that document, or set user_requested_new_workflow only after an explicit user request for a separate document.");
            }

            var priorJobs = await jobs.ListAsync(cancellationToken).ConfigureAwait(false);
            var failedInitialAttempts = priorJobs.Count(job =>
                job.UserScope == scope.UserScope
                && job.ConversationScope == scope.ConversationScope
                && job.Kind == JobKind.FinishDocument
                && job.ParentJobId is null
                && job.State is JobState.Failed or JobState.Canceled or JobState.TimedOut or JobState.RejectedUnsafeDocument);
            if (failedInitialAttempts >= 2 && !userRequestedNewWorkflow)
            {
                throw new WordMcpException(
                    "initial_retry_limit_reached",
                    "$.user_requested_new_workflow",
                    "The initial document generation and its single full retry have both ended without success.",
                    "Stop the autonomous retry loop; start again only after an explicit user request for a separate workflow.");
            }

            var draft = new DraftRecord(
                Identifier.New("draft_"),
                scope.UserScope,
                scope.ConversationScope,
                definition,
                now,
                now.Add(lifetime),
                LastAccessedAt: now,
                OriginMessageScope: messageScope);
            await quota.StoreDraftAsync(
                draft,
                token => repository.SaveAsync(draft, token),
                cancellationToken).ConfigureAwait(false);
            return View(draft);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DraftView> AddSectionsAsync(
        CallerContext caller,
        string draftId,
        int? startSectionIndex,
        IReadOnlyList<LogicalSectionSpec> sections,
        CancellationToken cancellationToken)
    {
        validator.ValidateSectionBatch(sections);
        var scope = scopes.Create(caller);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var draft = await repository.GetOwnedAsync(scope, draftId, timeProvider, cancellationToken).ConfigureAwait(false);
            if (draft.SubmittedJobId is not null)
            {
                throw new WordMcpException(
                    "draft_already_submitted",
                    "$.draft_id",
                    "The draft has already been submitted.",
                    "Use the returned job_id and do not add sections to this draft.");
            }

            var expectedIndex = draft.Definition.Sections.Count + 1;
            if (startSectionIndex.HasValue && startSectionIndex.Value != expectedIndex)
            {
                throw new WordMcpException(
                    "section_order_mismatch",
                    "$.start_section_index",
                    "The section batch does not start at the next expected index.",
                    $"Use start_section_index={expectedIndex} or omit it.");
            }

            if (draft.Definition.Sections.Count + sections.Count > draft.Definition.ExpectedSectionCount)
            {
                throw new WordMcpException(
                    "too_many_sections",
                    "$.sections",
                    "The batch would exceed expected_section_count.",
                    "Send only the remaining completed logical sections.");
            }

            var existingKeys = draft.Definition.Sections.Select(section => section.SectionKey).ToHashSet(StringComparer.Ordinal);
            if (sections.Any(section => !existingKeys.Add(section.SectionKey)))
            {
                throw new WordMcpException(
                    "duplicate_section_key",
                    "$.sections",
                    "A section_key has already been accepted.",
                    "Do not resend accepted sections; send only the next new sections.");
            }

            var updatedDefinition = draft.Definition with
            {
                Sections = draft.Definition.Sections.Concat(sections).ToArray(),
            };
            validator.ValidateDefinition(updatedDefinition, requireComplete: false);
            var updated = draft with
            {
                Definition = updatedDefinition,
                LastAccessedAt = timeProvider.GetUtcNow(),
            };
            await quota.StoreDraftAsync(
                updated,
                token => repository.SaveAsync(updated, token),
                cancellationToken).ConfigureAwait(false);
            return View(updated);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DraftRecord> AcquireCompletedAsync(
        CallerContext caller,
        string draftId,
        CancellationToken cancellationToken)
    {
        var scope = scopes.Create(caller);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var draft = await repository.GetOwnedAsync(scope, draftId, timeProvider, cancellationToken).ConfigureAwait(false);
            validator.ValidateDefinition(draft.Definition, requireComplete: true);
            return draft;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkSubmittedAsync(
        CallerContext caller,
        string draftId,
        string jobId,
        CancellationToken cancellationToken)
    {
        var scope = scopes.Create(caller);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var draft = await repository.GetOwnedAsync(scope, draftId, timeProvider, cancellationToken).ConfigureAwait(false);
            if (draft.SubmittedJobId is not null && draft.SubmittedJobId != jobId)
            {
                throw new InvalidOperationException("The draft was concurrently submitted to another job.");
            }

            var submitted = draft with
            {
                SubmittedJobId = jobId,
                LastAccessedAt = timeProvider.GetUtcNow(),
            };
            await quota.StoreDraftAsync(
                submitted,
                token => repository.SaveAsync(submitted, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool DefinitionsMatch(DocumentDefinition left, DocumentDefinition right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left, JsonFileStore.Options),
            JsonSerializer.SerializeToElement(right, JsonFileStore.Options));

    private static DraftView View(DraftRecord draft) => new(
        draft.Id,
        draft.Definition.Sections.Count + 1,
        draft.Definition.ExpectedSectionCount - draft.Definition.Sections.Count,
        draft.ExpiresAt,
        draft.SubmittedJobId,
        draft.SubmittedJobId is null ? null : "word_wait_for_job");

    public void Dispose() => gate.Dispose();
}
