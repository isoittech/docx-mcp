using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WordMcp.Domain;

public enum DocumentBlockKind
{
    Heading,
    Paragraph,
    UnorderedList,
    OrderedList,
    Table,
    KeyValue,
    Callout,
    Quote,
    Image,
    Caption,
    PageBreak,
    SectionBreak,
}

public enum PageSizeKind
{
    A4,
    Letter,
}

public enum PageOrientationKind
{
    Portrait,
    Landscape,
}

public enum SectionBreakKind
{
    NextPage,
    Continuous,
    EvenPage,
    OddPage,
}

public sealed record SemanticRun(
    [property: Required, StringLength(5_000, MinimumLength = 1), JsonPropertyName("text"), Description("Plain text only; markup is not interpreted.")]
    string Text,
    [property: JsonPropertyName("bold")] bool Bold = false,
    [property: JsonPropertyName("italic")] bool Italic = false,
    [property: JsonPropertyName("code")] bool Code = false);

public sealed record ListItemSpec(
    [property: Required, MinLength(1), MaxLength(200), JsonPropertyName("runs")] IReadOnlyList<SemanticRun> Runs,
    [property: Range(0, 3), JsonPropertyName("level"), Description("Zero-based list level from 0 to 3.")]
    int Level = 0);

public sealed record TableSpec(
    [property: Required, MinLength(1), MaxLength(12), JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: Required, MaxLength(200), JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<string>> Rows,
    [property: JsonPropertyName("caption")] string? Caption = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("allow_row_split")] bool AllowRowSplit = false);

public sealed record KeyValueSpec(
    [property: Required, StringLength(200, MinimumLength = 1), JsonPropertyName("key")] string Key,
    [property: Required, StringLength(2_000, MinimumLength = 1), JsonPropertyName("value")] string Value);

public sealed record DocumentBlock(
    [property: JsonPropertyName("kind")] DocumentBlockKind Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: MinLength(1), MaxLength(200), JsonPropertyName("runs")] IReadOnlyList<SemanticRun>? Runs = null,
    [property: Range(1, 4), JsonPropertyName("level")] int? Level = null,
    [property: MinLength(1), MaxLength(100), JsonPropertyName("items")] IReadOnlyList<ListItemSpec>? Items = null,
    [property: JsonPropertyName("table")] TableSpec? Table = null,
    [property: MinLength(1), MaxLength(50), JsonPropertyName("key_values")] IReadOnlyList<KeyValueSpec>? KeyValues = null,
    [property: JsonPropertyName("image_file_id")] string? ImageFileId = null,
    [property: JsonPropertyName("alt_text")] string? AltText = null,
    [property: JsonPropertyName("caption")] string? Caption = null,
    [property: JsonPropertyName("section_break_kind")] SectionBreakKind? SectionBreakKind = null);

public sealed record LogicalSectionSpec(
    [property: Required, StringLength(64, MinimumLength = 1), RegularExpression("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$"), JsonPropertyName("section_key"), Description("Stable draft-local section key.")]
    string SectionKey,
    [property: Required, StringLength(200, MinimumLength = 1), JsonPropertyName("title"), Description("Rendered automatically as this logical section's Heading 1; do not repeat it as the first heading block.")]
    string Title,
    [property: Required, MinLength(1), MaxLength(60), JsonPropertyName("blocks"), Description("Section body blocks only; do not repeat title as the first heading block.")]
    IReadOnlyList<DocumentBlock> Blocks);

public sealed record DocumentLayoutSpec(
    [property: JsonPropertyName("page_size")] PageSizeKind PageSize = PageSizeKind.A4,
    [property: JsonPropertyName("orientation")] PageOrientationKind Orientation = PageOrientationKind.Portrait,
    [property: Range(10, 50), JsonPropertyName("margin_top_mm")] decimal MarginTopMm = 20,
    [property: Range(10, 50), JsonPropertyName("margin_right_mm")] decimal MarginRightMm = 20,
    [property: Range(10, 50), JsonPropertyName("margin_bottom_mm")] decimal MarginBottomMm = 20,
    [property: Range(10, 50), JsonPropertyName("margin_left_mm")] decimal MarginLeftMm = 20,
    [property: Range(1, 3), JsonPropertyName("columns")] int Columns = 1);

public sealed record DocumentThemeSpec(
    [property: AllowedValues("professional", "minimal", "report", "academic"), JsonPropertyName("preset")] string Preset = "professional",
    [property: RegularExpression("^[A-Fa-f0-9]{6}$"), JsonPropertyName("accent")] string Accent = "1F4E79",
    [property: StringLength(100, MinimumLength = 1), JsonPropertyName("heading_font")] string HeadingFont = "Noto Sans CJK JP",
    [property: StringLength(100, MinimumLength = 1), JsonPropertyName("body_font")] string BodyFont = "Noto Serif CJK JP",
    [property: StringLength(100, MinimumLength = 1), JsonPropertyName("code_font")] string CodeFont = "Noto Sans Mono CJK JP");

public sealed record DocumentDesignSpec(
    [property: AllowedValues("airy", "balanced", "detailed"), JsonPropertyName("density")] string Density = "balanced",
    [property: JsonPropertyName("cover")] bool Cover = true,
    [property: JsonPropertyName("table_of_contents")] bool TableOfContents = true);

public sealed record HeaderFooterPolicy(
    [property: JsonPropertyName("header_text")] string? HeaderText = null,
    [property: JsonPropertyName("footer_text")] string? FooterText = null,
    [property: JsonPropertyName("page_numbers")] bool PageNumbers = true,
    [property: JsonPropertyName("different_first_page")] bool DifferentFirstPage = true,
    [property: JsonPropertyName("different_even_odd")] bool DifferentEvenOdd = false);

public sealed record DocumentDefinition(
    [property: Required, StringLength(200, MinimumLength = 1), JsonPropertyName("title")] string Title,
    [property: Required, StringLength(1_000, MinimumLength = 1), JsonPropertyName("purpose")] string Purpose,
    [property: Required, StringLength(500, MinimumLength = 1), JsonPropertyName("audience")] string Audience,
    [property: StringLength(500), JsonPropertyName("subject")] string? Subject,
    [property: Required, RegularExpression("^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$"), JsonPropertyName("locale")] string Locale,
    [property: Range(1, 50), JsonPropertyName("expected_section_count")] int ExpectedSectionCount,
    [property: JsonPropertyName("template_source")] string TemplateSource,
    [property: JsonPropertyName("layout")] DocumentLayoutSpec Layout,
    [property: JsonPropertyName("theme")] DocumentThemeSpec Theme,
    [property: JsonPropertyName("design")] DocumentDesignSpec Design,
    [property: JsonPropertyName("header_footer")] HeaderFooterPolicy HeaderFooter,
    [property: Required, MaxLength(50), JsonPropertyName("sections")] IReadOnlyList<LogicalSectionSpec> Sections);

public sealed record DraftView(
    [property: JsonPropertyName("draft_id")] string DraftId,
    [property: JsonPropertyName("next_section_index")] int NextSectionIndex,
    [property: JsonPropertyName("remaining_section_count")] int RemainingSectionCount,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("submitted_job_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubmittedJobId = null,
    [property: JsonPropertyName("next_tool"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextTool = null);

public sealed record DraftRecord(
    string Id,
    string UserScope,
    string ConversationScope,
    DocumentDefinition Definition,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? SubmittedJobId = null,
    DateTimeOffset? LastAccessedAt = null,
    string? OriginMessageScope = null);

public sealed record SectionInsertRequest(
    [property: Required, MinLength(1), MaxLength(3), JsonPropertyName("sections")] IReadOnlyList<LogicalSectionSpec> Sections,
    [property: AllowedValues("start", "end", "after"), JsonPropertyName("position")] string Position = "end",
    [property: JsonPropertyName("after_section_key")] string? AfterSectionKey = null);
