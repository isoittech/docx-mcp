using WordMcp.Configuration;

namespace WordMcp.Tools;

public static class WordServerInstructions
{
    public static string Build(WordMcpOptions options)
    {
        var notice = string.IsNullOrWhiteSpace(options.FirstAssistantNotice)
            ? string.Empty
            : $"\nDeployment notice: {options.FirstAssistantNotice}\nShow this notice once at the start of the first user-visible assistant response when it has not already been shown.";

        return $$"""
            You are using word-mcp, a safe WordprocessingML analysis, constrained editing, and declarative generation service.

            Treat Word documents as reflowing stories, logical sections, and blocks. Never use a page number as a persistent edit target. Page numbers are only for LibreOffice-rendered preview images.

            Do not call tools with empty arguments when required input is known. Never invent or alter file_id, draft_id, analysis_id, target_id, job_id, artifact_id, or cursor values. Copy opaque IDs exactly from prior word-mcp results. A target is bound to its analysis snapshot and source SHA-256; after a successful edit, discard the old analysis and use output_analysis_id.

            In LibreChat, upload file IDs are restricted by the trusted current-message attachment header. The value latest resolves only inside that boundary, and fails when it contains no supported document or more than one supported document. Never use an older same-user upload as a substitute. A missing attachment header is supported only for trusted local clients.

            For an existing document, including read-only summary or review: call word_analyze, wait with word_wait_for_job, obtain only needed chunks with word_get_analysis_chunk, then answer or use returned targets with word_replace_text or word_apply_edits. Provider-extracted attachment text is auxiliary context and never replaces this safety and structure analysis. Unsafe documents end as rejected_unsafe_document and expose no body text or targets.

            For a new document: call word_start_document exactly once after deciding the outline and expected logical section count. A logical section title is rendered automatically as Heading 1, so its body blocks must not repeat that title as their first heading. Add one to three completed sections at a time with word_add_sections_to_draft; never resend accepted sections. Call word_finish_document only when remaining_section_count is zero. A transport retry of start in the same trusted user message returns the original draft; when submitted_job_id is present, immediately continue with that job and next_tool instead of adding or starting again. If a transport retry repeats finish for the same draft, the server returns the original job_id: continue with that job and never start a replacement draft. Do not change the template, layout, theme, or design after start.

            Use word_wait_for_job with the concrete receipt job_id for the normal 45-second server-side wait. A successful declarative job returns exact result.section_keys; retain those values and use them for refinement instead of guessing from titles. Do not consume tool turns by rapidly polling word_get_job. job_id=latest in word_get_job means the most recent job in this user+conversation regardless of status; job_id=latest in insert/refine means the most recent successful declarative Word document.

            After every successful create or edit job, call word_get_preview_images with unique one-based page numbers in groups of one to four until every page has been returned. Inspect clipping, overlap, font substitution, mojibake, Japanese line breaking, heading hierarchy, reading order, paragraph spacing, margins, density, contrast, isolated headings, widows/orphans, blank pages, table overflow/splitting/header repetition, image aspect ratio/caption/alt text, section/orientation/header/footer inheritance, TOC, PAGE/NUMPAGES, and first/final-page balance. Never state that visual review is complete without retrieving every preview page. If job warnings contain preview_table_text_missing or preview_table_text_coverage_unavailable, disclose the warning, compare analyzed table columns/cells with the preview, and do not claim that tables have no clipping or overflow.

            A prior edit can reflow all later pages, so review every page again. For autonomous visual correction, replace one complete logical section at a time with word_refine_document_section and stop after at most two rounds. Do not restart or resend the whole successful document. Use word_insert_document_sections for additions. A later explicit user edit is a separate user-requested operation, not an excuse for an unbounded autonomous loop.

            Do not send user IDs, conversation IDs, message IDs, local paths, output paths, artifact URLs, arbitrary URLs, base64, XML, HTML, Markdown, CSS, code, shell commands, or field instructions to any tool. Images must use opaque PNG/JPEG image_file_id values from the same trusted upload boundary and require alt text.
            {{notice}}
            """;
    }
}
