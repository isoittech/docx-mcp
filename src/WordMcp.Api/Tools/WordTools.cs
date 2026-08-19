using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WordMcp.Analysis;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Drafts;
using WordMcp.Jobs;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Tools;

#pragma warning disable MEAI001 // MCP 2.1 uses AIParameterName to define stable public tool argument names.

[McpServerToolType]
public sealed class WordTools
{
    [McpServerTool(Name = "word_get_capabilities", ReadOnly = true, Idempotent = true),
     Description("Word MCP の対応範囲、hard limit、安全上の拒否範囲、推奨ワークフローを返します。")]
    public static CallToolResult GetCapabilities(
        IOptions<WordMcpOptions> options,
        TemplateRegistry templates)
    {
        var limits = options.Value;
        var capabilities = new
        {
            protocol = new
            {
                transport = "stateless_streamable_http",
                public_json = "snake_case",
                asynchronous_tools = new[]
                {
                    "word_analyze",
                    "word_render_preview",
                    "word_replace_text",
                    "word_apply_edits",
                    "word_populate_template",
                    "word_finish_document",
                    "word_insert_document_sections",
                    "word_refine_document_section",
                },
            },
            workflow = new[]
            {
                "Existing documents: analyze, wait, fetch only needed analysis chunks, then edit returned opaque targets.",
                "New documents: start once, add one to three complete logical sections in order, then finish.",
                "After every successful create or edit job, wait and retrieve every preview page in unique groups of one to four.",
                "Use section-level refine for at most two autonomous visual correction rounds; use insert for additions.",
            },
            limits = new
            {
                request_body_bytes = limits.MaxRequestBodyBytes,
                json_depth = limits.MaxJsonDepth,
                input_file_bytes = limits.MaxFileBytes,
                blocks = limits.MaxBlocks,
                characters = limits.MaxCharacters,
                table_cells = limits.MaxTableCells,
                images = limits.MaxImages,
                explicit_page_breaks = limits.MaxExplicitPageBreaks,
                rendered_pages = limits.MaxRenderedPages,
                concurrent_jobs = limits.MaxConcurrentJobs,
                queue_depth = limits.MaxQueueDepth,
                job_timeout_minutes = limits.JobTimeoutMinutes,
                preview_pages_per_call = 4,
            },
            templates = new
            {
                default_configured = templates.HasDefault,
                readiness = templates.Readiness.State switch
                {
                    TemplateReadinessState.NotConfigured => "not_configured",
                    TemplateReadinessState.Pending => "pending",
                    TemplateReadinessState.Ready => "ready",
                    TemplateReadinessState.Failed => "failed",
                    _ => "failed",
                },
                readiness_failure_code = templates.Readiness.FailureCode,
                accepted_sources = new[] { "none", "default", "latest", "opaque upload file_id" },
            },
            supported_now = new[]
            {
                "Macro-free DOCX/DOTX package preflight and multi-story structural analysis",
                "Run-spanning paragraph text replacement with source hash and expected-count checks",
                "Atomic block insertion/replacement/deletion, simple table cell replacement, and row append",
                "Simple inline, block, and table-cell SDT population with explicit bookmark fallback",
                "Declarative editable DOCX generation with named styles, native numbering, tables, images, TOC, headers, footers, and fields",
                "LibreOffice PDF plus bounded Poppler full-page PNG previews and signed artifact downloads",
            },
            rejected_or_detection_only = new[]
            {
                "Macros, ActiveX, OLE, embedded packages, altChunk, unsafe external relationships, and non-allowlisted fields are rejected before content analysis.",
                "Tracked-change creation/accept/reject, comment editing, footnote/endnote editing, equations, citations, complex or repeating SDT, text boxes, SmartArt, and charts are not edited in this release.",
            },
        };
        return SuccessResult(capabilities);
    }

    [McpServerTool(Name = "word_analyze", ReadOnly = true, Idempotent = true),
     Description("LibreChat upload または同じ会話の document artifact を安全な immutable snapshot に固定し、非同期解析します。source_file_id は不透明 ID、同じ利用者の最新 DOCX/DOTX を選ぶ latest、または省略です。unsafe 文書は rejected_unsafe_document で本文や target を返しません。受領後は word_wait_for_job を使います。")]
    public static Task<CallToolResult> AnalyzeAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        CancellationToken cancellationToken,
        [AIParameterName("source_file_id"), Description("Opaque upload file_id, same-conversation document artifact_id, or latest. Omit when LibreChat did not expose a file ID.")]
        string sourceFileId = "latest") =>
        InvokeAsync(
            () => jobs.SubmitAnalyzeAsync(callerContext.GetRequired(), sourceFileId, cancellationToken));

    [McpServerTool(Name = "word_get_analysis_chunk", ReadOnly = true, Idempotent = true),
     Description("解析済み snapshot から outline、blocks、tables、cells、controls 等を最大50件ずつ取得します。analysis_id と cursor は返却値をそのまま使い、target_id を推測しません。")]
    public static Task<CallToolResult> GetAnalysisChunkAsync(
        CallerContextAccessor callerContext,
        AnalysisQueryService analyses,
        [Required, AIParameterName("analysis_id"), Description("word-mcp が返した opaque analysis_id。")]
        string analysisId,
        [Required, AIParameterName("kind"), Description("analysis summary の available_kinds に含まれる正確な kind。")]
        string kind,
        CancellationToken cancellationToken,
        [AIParameterName("cursor"), Description("直前の chunk が返した opaque next_cursor。最初は省略します。")]
        string? cursor = null,
        [Range(1, 50), AIParameterName("limit"), Description("1〜50件。")]
        int limit = 50) =>
        InvokeAsync(
            () => analyses.GetChunkAsync(
                callerContext.GetRequired(),
                analysisId,
                kind,
                cursor,
                limit,
                cancellationToken));

    [McpServerTool(Name = "word_render_preview", ReadOnly = true, Idempotent = true),
     Description("安全検査済み DOCX/DOTX を非同期で LibreOffice PDF と全ページ PNG に変換します。source_file_id は upload、同じ会話の document artifact、latest のいずれかです。受領後は word_wait_for_job、その後 word_get_preview_images で全ページを確認します。")]
    public static Task<CallToolResult> RenderPreviewAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        CancellationToken cancellationToken,
        [AIParameterName("source_file_id"), Description("Opaque upload file_id, same-conversation document artifact_id, or latest.")]
        string sourceFileId = "latest") =>
        InvokeAsync(
            () => jobs.SubmitRenderPreviewAsync(callerContext.GetRequired(), sourceFileId, cancellationToken));

    [McpServerTool(Name = "word_replace_text", Destructive = true),
     Description("analysis snapshot の段落 target で run 跨ぎ文字列を置換します。source SHA、expected_text、expected_match_count が一致しない場合は変更せず失敗します。成功後は旧 analysis/target を捨て、job result の output_analysis_id を使います。")]
    public static Task<CallToolResult> ReplaceTextAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        [Required, AIParameterName("analysis_id"), Description("word_analyze または直前の編集が返した opaque analysis_id。")]
        string analysisId,
        [Required, MinLength(1), MaxLength(100), AIParameterName("replacements"), Description("1〜100件。各 target_id、expected_text、replacement_text、expected_match_count を指定します。")]
        IReadOnlyList<TextReplacementRequest> replacements,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => jobs.SubmitReplaceTextAsync(
                callerContext.GetRequired(),
                analysisId,
                replacements,
                cancellationToken));

    [McpServerTool(Name = "word_apply_edits", Destructive = true),
     Description("解析済み target に限定した 1〜50 件の atomic 編集を全成功または全失敗で適用します。任意 XML や index path は受けません。成功後は output_analysis_id を使い、全ページを再確認します。")]
    public static Task<CallToolResult> ApplyEditsAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        [Required, AIParameterName("analysis_id"), Description("word_analyze または直前の編集が返した opaque analysis_id。")]
        string analysisId,
        [Required, MinLength(1), MaxLength(50), AIParameterName("edits"), Description("1〜50件の constrained edit。operation は replace_block、insert_before、insert_after、delete_block、replace_cell、append_table_row。")]
        IReadOnlyList<AtomicEditRequest> edits,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => jobs.SubmitApplyEditsAsync(
                callerContext.GetRequired(),
                analysisId,
                edits,
                cancellationToken));

    [McpServerTool(Name = "word_populate_template", Destructive = true),
     Description("macro-free DOCX/DOTX の単純 SDT tag を 1〜100 件流し込みます。明示した項目だけ bookmark fallback を許可し、重複・locked・data-bound・nested・repeating control は拒否します。成功後は全ページを確認します。")]
    public static Task<CallToolResult> PopulateTemplateAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        [Required, MinLength(1), MaxLength(100), AIParameterName("fields"), Description("1〜100件の tag と semantic runs。")]
        IReadOnlyList<TemplateFieldRequest> fields,
        CancellationToken cancellationToken,
        [AIParameterName("source_file_id"), Description("Opaque upload file_id、latest、または default。省略時は default。")]
        string sourceFileId = "default") =>
        InvokeAsync(
            () => jobs.SubmitPopulateTemplateAsync(
                callerContext.GetRequired(),
                sourceFileId,
                fields,
                cancellationToken));

    [McpServerTool(Name = "word_start_document", Destructive = true),
     Description("新規 Word 文書の metadata、期待論理 section 数、template、layout、theme、design、header/footer を固定し、1時間有効の draft を同期作成します。sections は空にし、成功後は word_add_sections_to_draft を使います。同じ利用者メッセージの通信再送は既存draftへ収束し、提出済みなら submitted_job_id と next_tool を返します。")]
    public static Task<CallToolResult> StartDocumentAsync(
        CallerContextAccessor callerContext,
        DraftService drafts,
        [Required, AIParameterName("definition"), Description("DocumentDefinition。sections は空、expected_section_count は完成時の論理 section 数です。")]
        DocumentDefinition definition,
        CancellationToken cancellationToken,
        [AIParameterName("user_requested_new_workflow"), Description("この会話で既に成功文書があり、ユーザーが明示的に別文書を依頼した場合だけ true。")]
        bool userRequestedNewWorkflow = false) =>
        InvokeAsync(
            () => drafts.StartAsync(
                callerContext.GetRequired(),
                definition,
                userRequestedNewWorkflow,
                cancellationToken));

    [McpServerTool(Name = "word_add_sections_to_draft", Destructive = true),
     Description("draft へ完成済み論理 section を順番どおり1〜3件追加します。受理済み section は再送しません。1回60 block・30,000文字以内です。remaining_section_count が0なら finish します。")]
    public static Task<CallToolResult> AddSectionsToDraftAsync(
        CallerContextAccessor callerContext,
        DraftService drafts,
        [Required, AIParameterName("draft_id"), Description("word_start_document が返した opaque draft_id。")]
        string draftId,
        [Required, MinLength(1), MaxLength(3), AIParameterName("sections"), Description("新しい完成済み logical section を1〜3件。")]
        IReadOnlyList<LogicalSectionSpec> sections,
        CancellationToken cancellationToken,
        [Range(0, int.MaxValue), AIParameterName("start_section_index"), Description("省略推奨。指定時は response の next_section_index と一致させます。")]
        int? startSectionIndex = null) =>
        InvokeAsync(
            () => drafts.AddSectionsAsync(
                callerContext.GetRequired(),
                draftId,
                startSectionIndex,
                sections,
                cancellationToken));

    [McpServerTool(Name = "word_finish_document", Destructive = true),
     Description("remaining_section_count=0 の draft だけを immutable 仕様として取得し、DOCX/PDF/全ページ PNG を非同期生成します。同じ draft の通信再送は新規 job を作らず既存 job_id を返します。start で固定した全体設定は変更できません。受領後は返された job_id で wait と全ページ preview を行います。")]
    public static Task<CallToolResult> FinishDocumentAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        [Required, AIParameterName("draft_id"), Description("完成済み draft の opaque draft_id。")]
        string draftId,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => jobs.SubmitFinishDocumentAsync(callerContext.GetRequired(), draftId, cancellationToken));

    [McpServerTool(Name = "word_insert_document_sections", Destructive = true),
     Description("成功済み宣言型 Word 文書へ追加 section だけを挿入します。job_id=latest は同じ利用者・会話の最新成功済み宣言型文書です。既存 section を再送せず、成功後は全ページを再確認します。")]
    public static Task<CallToolResult> InsertDocumentSectionsAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        [Required, AIParameterName("request"), Description("1〜3件の sections、position=start|end|after、after の場合だけ after_section_key。")]
        SectionInsertRequest request,
        CancellationToken cancellationToken,
        [AIParameterName("job_id"), Description("成功済み宣言型文書の opaque job_id、または latest。")]
        string jobId = "latest") =>
        InvokeAsync(
            () => jobs.SubmitInsertSectionsAsync(
                callerContext.GetRequired(),
                jobId,
                request,
                cancellationToken));

    [McpServerTool(Name = "word_refine_document_section", Destructive = true),
     Description("成功済み宣言型文書の論理 section 1件を完全仕様で差し替えます。自律視覚修正は1 sectionずつ最大2巡で、変更後は再フローする全ページを確認します。user_requested_edit は後ターンでユーザーが明示した編集だけ true にします。")]
    public static Task<CallToolResult> RefineDocumentSectionAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        [Required, AIParameterName("section"), Description("置換後の完全な logical section 1件。section_key は既存値と一致させます。")]
        LogicalSectionSpec section,
        CancellationToken cancellationToken,
        [AIParameterName("job_id"), Description("成功済み宣言型文書の opaque job_id、または latest。")]
        string jobId = "latest",
        [AIParameterName("user_requested_edit"), Description("後の user turn でユーザーが明示した編集要求の場合だけ true。自律視覚修正では false。")]
        bool userRequestedEdit = false) =>
        InvokeAsync(
            () => jobs.SubmitRefineSectionAsync(
                callerContext.GetRequired(),
                jobId,
                section,
                userRequestedEdit,
                cancellationToken));

    [McpServerTool(Name = "word_get_job", ReadOnly = true, Idempotent = true),
     Description("ジョブ状態を待たず1回取得します。latest は同じ利用者・会話の状態を問わない直近 job です。通常の待機は短間隔 polling ではなく word_wait_for_job を使います。")]
    public static Task<CallToolResult> GetJobAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        CancellationToken cancellationToken,
        [AIParameterName("job_id"), Description("word-mcp が返した opaque job_id、または latest。")]
        string jobId = "latest") =>
        InvokeAsync(() => jobs.GetAsync(callerContext.GetRequired(), jobId, cancellationToken));

    [McpServerTool(Name = "word_wait_for_job", ReadOnly = true, Idempotent = true),
     Description("ジョブをサーバー内で通常45秒、最大50秒待ちます。最初に latest を具体 job へ固定します。terminal でなければ同じ job_id でもう一度待てます。宣言型文書の成功結果は正確な result.section_keys を返します。これを保持してから word_get_preview_images で全ページを確認します。")]
    public static Task<CallToolResult> WaitForJobAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        CancellationToken cancellationToken,
        [AIParameterName("job_id"), Description("word-mcp が返した opaque job_id、または latest。")]
        string jobId = "latest",
        [Range(1, 50), AIParameterName("wait_seconds"), Description("サーバー内待機秒。1〜50、既定45。client timeout より短くします。")]
        int waitSeconds = 45)
    {
        if (waitSeconds is < 1 or > 50)
        {
            return Task.FromResult(ErrorResult(new ToolError(
                "invalid_input",
                "wait_seconds_out_of_range",
                "$.wait_seconds",
                "wait_seconds must be between 1 and 50.",
                "Use a whole number from 1 through 50.")));
        }

        return InvokeAsync(
            () => jobs.WaitAsync(
                callerContext.GetRequired(),
                jobId,
                TimeSpan.FromSeconds(waitSeconds),
                cancellationToken));
    }

    [McpServerTool(Name = "word_get_preview_images", ReadOnly = true, Idempotent = true),
     Description("成功済み job の全ページ PNG をモデル自身の視覚確認用 MCP image block として返します。1始まり・重複なしの page_numbers を1〜4件指定し、全ページを漏れなく取得します。このツールを呼ばず確認済みと述べてはいけません。編集後は再フローした全ページを再確認します。")]
    public static async Task<CallToolResult> GetPreviewImagesAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        [Required, AIParameterName("job_id"), Description("成功済み job の opaque job_id。latest は不可。")]
        string jobId,
        [Required, MinLength(1), MaxLength(4), AIParameterName("page_numbers"), Description("今回確認する重複のない1始まりページ番号を1〜4件。")]
        IReadOnlyList<int> pageNumbers,
        CancellationToken cancellationToken)
    {
        try
        {
            var images = await jobs.GetPreviewImagesAsync(
                callerContext.GetRequired(),
                jobId,
                pageNumbers,
                cancellationToken).ConfigureAwait(false);
            var content = new List<ContentBlock>(images.Count * 2 + 1);
            foreach (var preview in images)
            {
                content.Add(new TextContentBlock
                {
                    Text = $"Page {preview.PageNumber} visual review image:",
                });
                content.Add(ImageContentBlock.FromBytes(preview.Data, preview.MediaType));
            }

            content.Add(new TextContentBlock
            {
                Text = "Inspect every returned page for clipping, overlap, font substitution, mojibake, Japanese line breaking, hierarchy, reading order, spacing, margins, contrast, isolated headings, widows/orphans, blank pages, table overflow, repeated table headers, image aspect ratio/caption/alt text, section and header/footer inheritance, TOC and PAGE/NUMPAGES fields, and first/final-page balance. Retrieve every page before claiming visual review. After any section edit, review every page again.",
            });
            return new CallToolResult
            {
                Content = content,
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    job_id = jobId,
                    reviewed_page_numbers = images.Select(image => image.PageNumber).ToArray(),
                }, ResultSerializerOptions),
            };
        }
        catch (WordMcpException exception)
        {
            return ErrorResult(exception);
        }
    }

    [McpServerTool(Name = "word_cancel_job", Destructive = true),
     Description("同じ利用者・会話の queued/running Word job をキャンセルします。受理結果は同期で返り、その後 word_get_job で終端状態を確認できます。")]
    public static Task<CallToolResult> CancelJobAsync(
        CallerContextAccessor callerContext,
        JobService jobs,
        [Required, AIParameterName("job_id"), Description("word-mcp が返した opaque job_id。")]
        string jobId,
        CancellationToken cancellationToken) =>
        InvokeAsync(() => jobs.CancelAsync(callerContext.GetRequired(), jobId, cancellationToken));

    private static readonly JsonSerializerOptions ResultSerializerOptions = CreateResultSerializerOptions();

    private static async Task<CallToolResult> InvokeAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            return result is null
                ? SuccessResult(new { status = "ok" })
                : SuccessResult(result);
        }
        catch (WordMcpException exception)
        {
            return ErrorResult(exception);
        }
    }

    private static CallToolResult SuccessResult<T>(T result)
    {
        var structuredContent = JsonSerializer.SerializeToElement(result, ResultSerializerOptions);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = structuredContent.GetRawText(),
                },
            ],
            StructuredContent = structuredContent,
        };
    }

    private static CallToolResult ErrorResult(WordMcpException exception) =>
        ErrorResult(ToolErrors.From(exception));

    private static CallToolResult ErrorResult(ToolError error)
    {
        var structuredContent = JsonSerializer.SerializeToElement(error, ResultSerializerOptions);
        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = structuredContent.GetRawText(),
                },
            ],
            StructuredContent = structuredContent,
        };
    }

    private static JsonSerializerOptions CreateResultSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            MaxDepth = 32,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

#pragma warning restore MEAI001
