using WordMcp.Domain;

namespace WordMcp.Word;

public sealed record WordAnalysisRequest(
    string SourcePath,
    string SourceFileName,
    string ExpectedSourceSha256,
    string UserScope,
    string ConversationScope);

public sealed record WordMutationRequest(
    string SourcePath,
    string DestinationPath,
    AnalysisSnapshot Analysis);

public sealed record WordTemplatePopulationRequest(
    string SourcePath,
    string DestinationPath,
    string SourceFileName,
    string ExpectedSourceSha256,
    string UserScope,
    string ConversationScope);

public sealed record WordImageAsset(
    string FileId,
    byte[] Bytes,
    string MediaType,
    string Sha256);

public sealed record WordGenerationRequest(
    string DestinationPath,
    DocumentDefinition Definition,
    string UserScope,
    string ConversationScope,
    string? TemplatePath = null,
    IReadOnlyDictionary<string, WordImageAsset>? Images = null);

public sealed record WordMutationResult(
    string OutputSha256,
    AnalysisSnapshot OutputAnalysis,
    OpenXmlValidationReport Validation,
    IReadOnlyList<string> ChangedPartUris);

public sealed record WordGenerationResult(
    string OutputSha256,
    AnalysisSnapshot OutputAnalysis,
    OpenXmlValidationReport Validation);

public sealed record OpenXmlValidationFingerprint(
    string ErrorType,
    string Id,
    string PartUri,
    string Path,
    string Description);

public sealed record OpenXmlValidationReport(
    IReadOnlyList<OpenXmlValidationFingerprint> BaselineErrors,
    IReadOnlyList<OpenXmlValidationFingerprint> CandidateErrors,
    IReadOnlyList<OpenXmlValidationFingerprint> NewErrors)
{
    public bool IsAccepted => NewErrors.Count == 0;
}

public static class WordEditOperations
{
    public const string ReplaceBlock = "replace_block";
    public const string InsertBefore = "insert_before";
    public const string InsertAfter = "insert_after";
    public const string DeleteBlock = "delete_block";
    public const string ReplaceCell = "replace_cell";
    public const string AppendTableRow = "append_table_row";
}
