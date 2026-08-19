using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WordMcp.Domain;

public sealed record TargetRecord(
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("story")] string Story,
    [property: JsonPropertyName("part_uri")] string PartUri,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("parent_ordinal")] int? ParentOrdinal,
    [property: JsonPropertyName("row_index")] int? RowIndex,
    [property: JsonPropertyName("column_index")] int? ColumnIndex,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonIgnore] bool Restricted = false);

public sealed record AnalysisItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("target_id")] string? TargetId,
    [property: JsonPropertyName("story")] string Story,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, object?> Data);

public sealed record AnalysisSummary(
    [property: JsonPropertyName("analysis_id")] string AnalysisId,
    [property: JsonPropertyName("source_sha256")] string SourceSha256,
    [property: JsonPropertyName("document_properties")] IReadOnlyDictionary<string, string?> DocumentProperties,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("character_count")] int CharacterCount,
    [property: JsonPropertyName("logical_block_count")] int LogicalBlockCount,
    [property: JsonPropertyName("word_section_count")] int WordSectionCount,
    [property: JsonPropertyName("stories")] IReadOnlyList<string> Stories,
    [property: JsonPropertyName("features")] IReadOnlyList<string> Features,
    [property: JsonPropertyName("unsupported_features")] IReadOnlyList<string> UnsupportedFeatures,
    [property: JsonPropertyName("available_kinds")] IReadOnlyDictionary<string, int> AvailableKinds,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record AnalysisSnapshot(
    string Id,
    string UserScope,
    string ConversationScope,
    string SourceSha256,
    string SourcePath,
    string SourceFileName,
    AnalysisSummary Summary,
    IReadOnlyDictionary<string, IReadOnlyList<AnalysisItem>> Items,
    IReadOnlyDictionary<string, TargetRecord> Targets,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? InvalidatedAt = null,
    DateTimeOffset? LastAccessedAt = null);

public sealed record AnalysisCacheRecord(
    string Id,
    string UserScope,
    string ConversationScope,
    string SourceSha256,
    AnalysisSummary Summary,
    IReadOnlyDictionary<string, IReadOnlyList<AnalysisItem>> Items,
    IReadOnlyDictionary<string, TargetRecord> Targets,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? InvalidatedAt = null,
    DateTimeOffset? LastAccessedAt = null);

public sealed record AnalysisReceipt(
    [property: JsonPropertyName("analysis_id")] string AnalysisId,
    [property: JsonPropertyName("summary")] AnalysisSummary Summary,
    [property: JsonPropertyName("next_tool")] string NextTool = "word_get_analysis_chunk");

public sealed record AnalysisChunk(
    [property: JsonPropertyName("analysis_id")] string AnalysisId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("items")] IReadOnlyList<AnalysisItem> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record TextReplacementRequest(
    [property: Required, JsonPropertyName("target_id")] string TargetId,
    [property: Required, MinLength(1), JsonPropertyName("expected_text")] string ExpectedText,
    [property: JsonPropertyName("replacement_text")] string ReplacementText,
    [property: Range(1, int.MaxValue), JsonPropertyName("expected_match_count")] int ExpectedMatchCount = 1);

public sealed record AtomicEditRequest(
    [property: Required, AllowedValues("replace_block", "insert_before", "insert_after", "delete_block", "replace_cell", "append_table_row"), JsonPropertyName("operation")] string Operation,
    [property: Required, JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("runs")] IReadOnlyList<SemanticRun>? Runs = null,
    [property: JsonPropertyName("blocks")] IReadOnlyList<DocumentBlock>? Blocks = null,
    [property: JsonPropertyName("cells")] IReadOnlyList<string>? Cells = null);

public sealed record TemplateFieldRequest(
    [property: Required, JsonPropertyName("tag")] string Tag,
    [property: Required, MinLength(1), MaxLength(200), JsonPropertyName("runs")] IReadOnlyList<SemanticRun> Runs,
    [property: JsonPropertyName("bookmark_fallback")] bool BookmarkFallback = false);
