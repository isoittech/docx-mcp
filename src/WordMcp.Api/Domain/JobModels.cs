using System.Text.Json;
using System.Text.Json.Serialization;

namespace WordMcp.Domain;

public enum JobKind
{
    Analyze,
    RenderPreview,
    ReplaceText,
    ApplyEdits,
    PopulateTemplate,
    FinishDocument,
    InsertSections,
    RefineSection,
}

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
    TimedOut,
    RejectedUnsafeDocument,
}

public sealed record JobError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("part_kind")] string? PartKind,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("correction")] string Correction);

public sealed record ArtifactRecord(
    [property: JsonPropertyName("artifact_id")] string ArtifactId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonIgnore] string Path,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("first_downloaded_at")] DateTimeOffset? FirstDownloadedAt = null);

public sealed record JobResult(
    [property: JsonPropertyName("analysis_id")] string? AnalysisId = null,
    [property: JsonPropertyName("output_analysis_id")] string? OutputAnalysisId = null,
    [property: JsonPropertyName("analysis_summary")] AnalysisSummary? AnalysisSummary = null,
    [property: JsonPropertyName("source_sha256")] string? SourceSha256 = null,
    [property: JsonPropertyName("page_count")] int? PageCount = null,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<ArtifactRecord>? Artifacts = null,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string>? Warnings = null,
    [property: JsonPropertyName("section_keys"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? SectionKeys = null);

public sealed record WordJob(
    string Id,
    string UserScope,
    string ConversationScope,
    JobKind Kind,
    JobState State,
    JsonElement Payload,
    string? InputPath,
    string? InputSha256,
    string? RootJobId,
    string? ParentJobId,
    int RevisionRound,
    IReadOnlyList<string> RevisedSections,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    JobResult? Result = null,
    JobError? Error = null,
    string? DraftId = null,
    DocumentDefinition? DocumentDefinition = null,
    long ReservedBytes = 0,
    string? PublishedRunId = null);

public sealed record JobReceipt(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("status")] string Status = "queued",
    [property: JsonPropertyName("recommended_wait_seconds")] int RecommendedWaitSeconds = 45,
    [property: JsonPropertyName("next_tool")] string NextTool = "word_wait_for_job");

public sealed record ArtifactLink(
    [property: JsonPropertyName("artifact_id")] string ArtifactId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record JobView(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("result")] JobResult? Result,
    [property: JsonPropertyName("error")] JobError? Error,
    [property: JsonPropertyName("artifact_links")] IReadOnlyList<ArtifactLink> ArtifactLinks,
    [property: JsonPropertyName("next_tool")] string? NextTool);

public sealed record CancelResult(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("status")] string Status);

public sealed record AnalyzePayload(string SourceReference);

public sealed record RenderPayload(string SourceReference);

public sealed record ReplacePayload(string AnalysisId, IReadOnlyList<TextReplacementRequest> Replacements);

public sealed record ApplyEditsPayload(string AnalysisId, IReadOnlyList<AtomicEditRequest> Edits);

public sealed record PopulateTemplatePayload(IReadOnlyList<TemplateFieldRequest> Fields);

public sealed record FinishDocumentPayload(DocumentDefinition Definition);

public sealed record InsertSectionsPayload(DocumentDefinition Definition);

public sealed record RefineSectionPayload(DocumentDefinition Definition, string SectionKey);
