using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Drafts;

public sealed partial class DocumentSpecValidator(IOptions<WordMcpOptions> options)
{
    private readonly WordMcpOptions options = options.Value;

    public void ValidateDefinition(DocumentDefinition definition, bool requireComplete)
    {
        if (definition is null)
        {
            Invalid("definition_required", "$", "A document definition is required.", "Provide the complete document-wide definition.");
        }

        ValidateText(definition.Title, 1, 200, "$.title");
        ValidateText(definition.Purpose, 1, 1_000, "$.purpose");
        ValidateText(definition.Audience, 1, 500, "$.audience");
        ValidateOptionalText(definition.Subject, 500, "$.subject");

        if (!LocalePattern().IsMatch(definition.Locale))
        {
            Invalid("invalid_locale", "$.locale", "Locale must be a BCP 47-style language tag.", "Use a value such as ja-JP or en-US.");
        }

        if (definition.ExpectedSectionCount is < 1 or > 50)
        {
            Invalid("section_count_out_of_range", "$.expected_section_count", "Expected section count must be between 1 and 50.", "Choose the completed logical outline before starting the draft.");
        }

        ValidateTemplateSource(definition.TemplateSource);
        ValidateLayout(definition.Layout);
        ValidateTheme(definition.Theme);
        ValidateDesign(definition.Design);
        ValidateHeaderFooter(definition.HeaderFooter);

        if (definition.Sections is null)
        {
            Invalid("sections_required", "$.sections", "A sections array is required.", "Provide an empty array while staging or the complete section list when finishing.");
        }

        if (definition.Sections.Count > definition.ExpectedSectionCount
            || (requireComplete && definition.Sections.Count != definition.ExpectedSectionCount))
        {
            Invalid(
                "incomplete_or_excess_sections",
                "$.sections",
                "The accepted section count does not match expected_section_count.",
                "Add each remaining section once and finish only when remaining_section_count is zero.");
        }

        ValidateSections(definition.Sections, batch: false);
    }

    public void ValidateSectionBatch(IReadOnlyList<LogicalSectionSpec> sections)
    {
        if (sections is null)
        {
            Invalid("sections_required", "$.sections", "A section batch is required.", "Provide one to three completed logical sections.");
        }

        if (sections.Count is < 1 or > 3)
        {
            Invalid("section_batch_out_of_range", "$.sections", "Each add call must contain 1 to 3 sections.", "Split the completed outline into batches of at most three sections.");
        }

        ValidateSections(sections, batch: true);
    }

    private void ValidateSections(IReadOnlyList<LogicalSectionSpec> sections, bool batch)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var blocks = 0;
        long characters = 0;
        long tableCells = 0;
        var images = 0;
        var explicitPageBreaks = 0;
        foreach (var (section, sectionIndex) in sections.Select((value, index) => (value, index)))
        {
            var sectionPath = $"$.sections[{sectionIndex}]";
            if (section is null)
            {
                Invalid("section_required", sectionPath, "A section entry cannot be null.", "Provide a complete logical section object.");
            }

            if (!SectionKeyPattern().IsMatch(section.SectionKey) || !keys.Add(section.SectionKey))
            {
                Invalid("invalid_or_duplicate_section_key", $"{sectionPath}.section_key", "Section keys must be unique safe identifiers.", "Use a unique ASCII letter/digit/hyphen/underscore key of at most 64 characters.");
            }

            ValidateText(section.Title, 1, 200, $"{sectionPath}.title");
            if (section.Blocks is null || section.Blocks.Count is < 1 or > 60)
            {
                Invalid("block_count_out_of_range", $"{sectionPath}.blocks", "A section must contain 1 to 60 blocks.", "Split large content into more logical sections.");
            }

            ValidateSectionTitleIsNotRepeated(section, sectionPath);

            foreach (var (block, blockIndex) in section.Blocks.Select((value, index) => (value, index)))
            {
                if (block is null)
                {
                    Invalid("block_required", $"{sectionPath}.blocks[{blockIndex}]", "A block entry cannot be null.", "Provide one complete supported block object.");
                }

                ValidateBlock(block, $"{sectionPath}.blocks[{blockIndex}]");
                blocks++;
                characters = checked(characters + CountCharacters(block));
                tableCells = checked(tableCells + CountTableCells(block));
                images += block.Kind == DocumentBlockKind.Image ? 1 : 0;
                explicitPageBreaks += block.Kind == DocumentBlockKind.PageBreak ? 1 : 0;
            }
        }

        var blockLimit = batch ? 60 : options.MaxBlocks;
        var characterLimit = batch ? 30_000 : options.MaxCharacters;
        if (blocks > blockLimit)
        {
            Invalid("block_limit", "$.sections", $"The input contains more than {blockLimit} blocks.", "Send fewer completed sections per call.");
        }

        if (characters > characterLimit)
        {
            Invalid("character_limit", "$.sections", $"The input contains more than {characterLimit} characters.", "Shorten the content or split it across calls.");
        }

        if (tableCells > options.MaxTableCells)
        {
            Invalid(
                "table_cell_limit",
                "$.sections",
                $"The document contains more than {options.MaxTableCells} generated table cells.",
                "Reduce or split table and key-value blocks before submitting the document.");
        }

        if (images > options.MaxImages)
        {
            Invalid(
                "image_limit",
                "$.sections",
                $"The document contains more than {options.MaxImages} image blocks.",
                "Remove image blocks until the complete document is within the image limit.");
        }

        if (explicitPageBreaks > options.MaxExplicitPageBreaks)
        {
            Invalid(
                "explicit_page_break_limit",
                "$.sections",
                $"The document contains more than {options.MaxExplicitPageBreaks} explicit page breaks.",
                "Remove manual page-break blocks and rely on semantic section flow where possible.");
        }
    }

    private static void ValidateSectionTitleIsNotRepeated(LogicalSectionSpec section, string sectionPath)
    {
        var firstContent = section.Blocks
            .Select((block, index) => (Block: block, Index: index))
            .FirstOrDefault(item => item.Block is not null
                && item.Block.Kind is not (DocumentBlockKind.PageBreak or DocumentBlockKind.SectionBreak));
        if (firstContent.Block?.Kind != DocumentBlockKind.Heading)
        {
            return;
        }

        var headingText = firstContent.Block.Text;
        if (headingText is null && firstContent.Block.Runs is not null
            && firstContent.Block.Runs.All(static run => run is not null))
        {
            headingText = string.Concat(firstContent.Block.Runs.Select(static run => run.Text));
        }

        if (headingText is not null
            && string.Equals(
                NormalizeHeadingText(section.Title),
                NormalizeHeadingText(headingText),
                StringComparison.Ordinal))
        {
            Invalid(
                "section_title_repeated",
                $"{sectionPath}.blocks[{firstContent.Index}]",
                "A logical section title is already rendered automatically and cannot be repeated as its first heading block.",
                "Remove the duplicate heading block and keep only the section title plus body blocks.");
        }
    }

    private static string NormalizeHeadingText(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormKC).Where(static character => !char.IsWhiteSpace(character)));

    private static void ValidateBlock(DocumentBlock block, string path)
    {
        var suppliedPayloads = new[]
        {
            block.Text is not null,
            block.Runs is not null,
            block.Items is not null,
            block.Table is not null,
            block.KeyValues is not null,
            block.ImageFileId is not null,
        }.Count(value => value);

        switch (block.Kind)
        {
            case DocumentBlockKind.Heading:
                RequireExactlyOneTextPayload(block, path);
                if (block.Level is < 1 or > 4)
                {
                    Invalid("heading_level_out_of_range", $"{path}.level", "Heading level must be between 1 and 4.", "Use one of the supported named heading levels.");
                }

                break;
            case DocumentBlockKind.Paragraph:
            case DocumentBlockKind.Callout:
            case DocumentBlockKind.Quote:
            case DocumentBlockKind.Caption:
                RequireExactlyOneTextPayload(block, path);
                break;
            case DocumentBlockKind.UnorderedList:
            case DocumentBlockKind.OrderedList:
                if (suppliedPayloads != 1 || block.Items is null || block.Items.Count is < 1 or > 100)
                {
                    Invalid("invalid_list", path, "A list requires only an items array with 1 to 100 entries.", "Provide semantic list items without manual bullet or number glyphs.");
                }

                foreach (var (item, index) in block.Items.Select((value, index) => (value, index)))
                {
                    if (item is null)
                    {
                        Invalid("list_item_required", $"{path}.items[{index}]", "A list item cannot be null.", "Provide semantic runs for every list item.");
                    }

                    if (item.Level is < 0 or > 3)
                    {
                        Invalid("list_level_out_of_range", $"{path}.items[{index}].level", "List level must be between 0 and 3.", "Use no more than four list levels.");
                    }

                    ValidateRuns(item.Runs, $"{path}.items[{index}].runs");
                }

                break;
            case DocumentBlockKind.Table:
                if (suppliedPayloads != 1 || block.Table is null)
                {
                    Invalid("invalid_table", path, "A table block requires only the table payload.", "Provide columns and rows in the constrained table model.");
                }

                ValidateTable(block.Table, $"{path}.table");
                break;
            case DocumentBlockKind.KeyValue:
                if (suppliedPayloads != 1 || block.KeyValues is null || block.KeyValues.Count is < 1 or > 50)
                {
                    Invalid("invalid_key_value", path, "A key-value block requires only 1 to 50 key_values.", "Provide each key and value as plain text.");
                }

                foreach (var (pair, index) in block.KeyValues.Select((value, index) => (value, index)))
                {
                    if (pair is null)
                    {
                        Invalid("key_value_required", $"{path}.key_values[{index}]", "A key-value entry cannot be null.", "Provide both a key and value for every entry.");
                    }

                    ValidateText(pair.Key, 1, 200, $"{path}.key_values[{index}].key");
                    ValidateText(pair.Value, 1, 2_000, $"{path}.key_values[{index}].value");
                }

                break;
            case DocumentBlockKind.Image:
                if (suppliedPayloads != 1 || string.IsNullOrWhiteSpace(block.ImageFileId)
                    || block.ImageFileId.Length > 128 || string.IsNullOrWhiteSpace(block.AltText))
                {
                    Invalid("invalid_image_block", path, "An image requires one opaque image_file_id and non-empty alt_text.", "Use a PNG/JPEG upload ID from the same user boundary; do not send paths, URLs, or base64.");
                }

                ValidateText(block.AltText, 1, 500, $"{path}.alt_text");
                ValidateOptionalText(block.Caption, 500, $"{path}.caption");
                break;
            case DocumentBlockKind.PageBreak:
                if (suppliedPayloads != 0)
                {
                    Invalid("unexpected_block_payload", path, "A page break has no content payload.", "Send only kind=page_break.");
                }

                break;
            case DocumentBlockKind.SectionBreak:
                if (suppliedPayloads != 0 || block.SectionBreakKind is null)
                {
                    Invalid("invalid_section_break", path, "A section break requires only section_break_kind.", "Choose next_page, continuous, even_page, or odd_page.");
                }

                break;
            default:
                Invalid("unsupported_block_kind", $"{path}.kind", "The block kind is unsupported.", "Use one of the block kinds in word_get_capabilities.");
                break;
        }
    }

    private static void RequireExactlyOneTextPayload(DocumentBlock block, string path)
    {
        if ((block.Text is null) == (block.Runs is null)
            || block.Items is not null || block.Table is not null || block.KeyValues is not null || block.ImageFileId is not null)
        {
            Invalid("invalid_text_block", path, "A text block requires exactly one of text or runs.", "Provide plain text or constrained semantic runs, but not both.");
        }

        if (block.Text is not null)
        {
            ValidateText(block.Text, 1, 10_000, $"{path}.text");
        }
        else
        {
            ValidateRuns(block.Runs!, $"{path}.runs");
        }
    }

    private static void ValidateRuns(IReadOnlyList<SemanticRun>? runs, string path)
    {
        if (runs is null || runs.Count is < 1 or > 200)
        {
            Invalid("run_count_out_of_range", path, "Semantic runs must contain 1 to 200 entries.", "Merge adjacent runs with the same formatting.");
        }

        for (var index = 0; index < runs.Count; index++)
        {
            if (runs[index] is null)
            {
                Invalid("semantic_run_required", $"{path}[{index}]", "A semantic run cannot be null.", "Provide constrained plain text for every run.");
            }

            ValidateText(runs[index].Text, 1, 5_000, $"{path}[{index}].text");
        }
    }

    private static void ValidateTable(TableSpec? table, string path)
    {
        if (table is null || table.Columns is null || table.Rows is null
            || table.Columns.Count is < 1 or > 12 || table.Rows.Count > 200)
        {
            Invalid("table_dimensions_out_of_range", path, "Tables support 1 to 12 columns and at most 200 data rows.", "Split a wide or long table into smaller logical tables.");
        }

        for (var column = 0; column < table.Columns.Count; column++)
        {
            ValidateText(table.Columns[column], 1, 500, $"{path}.columns[{column}]");
        }

        for (var row = 0; row < table.Rows.Count; row++)
        {
            if (table.Rows[row] is null || table.Rows[row].Count != table.Columns.Count)
            {
                Invalid("table_row_width_mismatch", $"{path}.rows[{row}]", "Every row must have exactly one cell per column.", "Add or remove cells so the row width matches columns.");
            }

            for (var column = 0; column < table.Rows[row].Count; column++)
            {
                ValidateText(table.Rows[row][column], 0, 2_000, $"{path}.rows[{row}][{column}]");
            }
        }

        ValidateOptionalText(table.Caption, 500, $"{path}.caption");
        ValidateOptionalText(table.Description, 1_000, $"{path}.description");
    }

    private static long CountCharacters(DocumentBlock block) =>
        (block.Text?.Length ?? 0)
        + (block.Runs?.Sum(run => run.Text.Length) ?? 0)
        + (block.Items?.Sum(item => item.Runs.Sum(run => run.Text.Length)) ?? 0)
        + (block.Table?.Columns.Sum(value => value.Length) ?? 0)
        + (block.Table?.Rows.Sum(row => row.Sum(value => value.Length)) ?? 0)
        + (block.KeyValues?.Sum(pair => pair.Key.Length + pair.Value.Length) ?? 0)
        + (block.AltText?.Length ?? 0)
        + (block.Caption?.Length ?? 0);

    private static long CountTableCells(DocumentBlock block) => block.Kind switch
    {
        DocumentBlockKind.Table => checked(
            (long)block.Table!.Columns.Count * (block.Table.Rows.Count + 1L)),
        DocumentBlockKind.KeyValue => checked(2L * (block.KeyValues!.Count + 1L)),
        _ => 0,
    };

    private static void ValidateLayout(DocumentLayoutSpec? layout)
    {
        if (layout is null)
        {
            Invalid("layout_required", "$.layout", "A layout object is required.", "Provide a supported page size, margins, orientation, and column count.");
        }

        if (layout.Columns is < 1 or > 3)
        {
            Invalid("column_count_out_of_range", "$.layout.columns", "Column count must be between 1 and 3.", "Use a supported Word column count.");
        }

        foreach (var (value, name) in new[]
                 {
                     (layout.MarginTopMm, "margin_top_mm"),
                     (layout.MarginRightMm, "margin_right_mm"),
                     (layout.MarginBottomMm, "margin_bottom_mm"),
                     (layout.MarginLeftMm, "margin_left_mm"),
                 })
        {
            if (value is < 10 or > 50)
            {
                Invalid("margin_out_of_range", $"$.layout.{name}", "Margins must be between 10 and 50 mm.", "Choose a printable margin within the supported range.");
            }
        }
    }

    private static void ValidateTheme(DocumentThemeSpec? theme)
    {
        if (theme is null)
        {
            Invalid("theme_required", "$.theme", "A theme object is required.", "Provide one supported theme preset and its bounded font settings.");
        }

        if (theme.Preset is not ("professional" or "minimal" or "report" or "academic"))
        {
            Invalid("invalid_theme_preset", "$.theme.preset", "The theme preset is unsupported.", "Use professional, minimal, report, or academic.");
        }

        if (!HexColorPattern().IsMatch(theme.Accent))
        {
            Invalid("invalid_accent_color", "$.theme.accent", "Accent must be a six-digit RGB value without '#'.", "Use a value such as 1F4E79.");
        }

        ValidateText(theme.HeadingFont, 1, 100, "$.theme.heading_font");
        ValidateText(theme.BodyFont, 1, 100, "$.theme.body_font");
        ValidateText(theme.CodeFont, 1, 100, "$.theme.code_font");
    }

    private static void ValidateDesign(DocumentDesignSpec? design)
    {
        if (design is null)
        {
            Invalid("design_required", "$.design", "A design object is required.", "Provide a supported document design policy.");
        }

        if (design.Density is not ("airy" or "balanced" or "detailed"))
        {
            Invalid("invalid_density", "$.design.density", "Density is unsupported.", "Use airy, balanced, or detailed.");
        }
    }

    private static void ValidateHeaderFooter(HeaderFooterPolicy? policy)
    {
        if (policy is null)
        {
            Invalid("header_footer_required", "$.header_footer", "A header/footer policy is required.", "Provide the desired header, footer, and page-number settings.");
        }

        ValidateOptionalText(policy.HeaderText, 500, "$.header_footer.header_text");
        ValidateOptionalText(policy.FooterText, 500, "$.header_footer.footer_text");
    }

    private static void ValidateTemplateSource(string? source)
    {
        if (source is "default" or "none" or "latest")
        {
            return;
        }

        if (source is null || !OpaqueIdPattern().IsMatch(source))
        {
            Invalid("invalid_template_source", "$.template_source", "Template source must be default, none, latest, or an opaque file ID.", "Do not send a file name, path, URL, or base64 content.");
        }
    }

    private static void ValidateText(string? value, int minimum, int maximum, string path)
    {
        if (value is null || value.Length < minimum || value.Length > maximum || value.Any(character => character == '\0'))
        {
            Invalid("text_length_out_of_range", path, $"Text length must be between {minimum} and {maximum} characters.", "Shorten the plain text and remove NUL characters.");
        }
    }

    private static void ValidateOptionalText(string? value, int maximum, string path)
    {
        if (value is not null)
        {
            ValidateText(value, 0, maximum, path);
        }
    }

    [DoesNotReturn]
    private static void Invalid(string code, string path, string message, string correction) =>
        throw new WordMcpException(code, path, message, correction);

    [GeneratedRegex("\\A[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalePattern();

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SectionKeyPattern();

    [GeneratedRegex("\\A[A-Fa-f0-9]{6}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorPattern();

    [GeneratedRegex("\\A[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueIdPattern();
}
