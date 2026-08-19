using WordMcp.Domain;

namespace WordMcp.Jobs;

internal static class JobStateExtensions
{
    public static bool IsTerminal(this JobState state) => state is
        JobState.Succeeded or
        JobState.Failed or
        JobState.Canceled or
        JobState.TimedOut or
        JobState.RejectedUnsafeDocument;

    public static string ToContract(this JobState state) => state switch
    {
        JobState.Queued => "queued",
        JobState.Running => "running",
        JobState.Succeeded => "succeeded",
        JobState.Failed => "failed",
        JobState.Canceled => "canceled",
        JobState.TimedOut => "timed_out",
        JobState.RejectedUnsafeDocument => "rejected_unsafe_document",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string ToContract(this JobKind kind) => kind switch
    {
        JobKind.Analyze => "analyze",
        JobKind.RenderPreview => "render_preview",
        JobKind.ReplaceText => "replace_text",
        JobKind.ApplyEdits => "apply_edits",
        JobKind.PopulateTemplate => "populate_template",
        JobKind.FinishDocument => "finish_document",
        JobKind.InsertSections => "insert_document_sections",
        JobKind.RefineSection => "refine_document_section",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
