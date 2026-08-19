using System.Text.Json.Serialization;

namespace WordMcp.Domain;

public sealed class WordMcpException : Exception
{
    public WordMcpException(
        string code,
        string fieldPath,
        string message,
        string correction,
        bool unsafeDocument = false)
        : base(message)
    {
        Code = code;
        FieldPath = fieldPath;
        Correction = correction;
        UnsafeDocument = unsafeDocument;
    }

    public string Code { get; }

    public string FieldPath { get; }

    public string Correction { get; }

    public bool UnsafeDocument { get; }
}

public sealed record ToolError(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("field_path")] string FieldPath,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("correction")] string Correction);

public static class ToolErrors
{
    public static ToolError From(WordMcpException exception) => new(
        Status(exception.Code),
        exception.Code,
        exception.FieldPath,
        exception.Message,
        exception.Correction);

    private static string Status(string code) => code switch
    {
        "storage_quota_exceeded" or "queue_full" => "resource_exhausted",
        "job_not_found" or "analysis_not_found" or "draft_not_found" or "input_file_not_found"
            or "template_not_found" => "not_found",
        _ => "invalid_input",
    };
}
