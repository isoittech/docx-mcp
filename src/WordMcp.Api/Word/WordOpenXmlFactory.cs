using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Domain;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Word;

internal static class WordOpenXmlFactory
{
    public static W.Run CreateSemanticRun(SemanticRun semanticRun)
    {
        ArgumentNullException.ThrowIfNull(semanticRun);
        var properties = new W.RunProperties();
        if (semanticRun.Bold)
        {
            properties.Append(new W.Bold());
        }

        if (semanticRun.Italic)
        {
            properties.Append(new W.Italic());
        }

        if (semanticRun.Code)
        {
            properties.Append(new W.RunStyle { Val = "CodeChar" });
        }

        var text = new W.Text(semanticRun.Text);
        WordTextMap.UpdateSpace(text);
        var run = new W.Run();
        if (properties.HasChildren)
        {
            run.Append(properties);
        }

        run.Append(text);
        return run;
    }

    public static W.TableCell CreateTableCell(string value, int? widthTwips)
    {
        var properties = new W.TableCellProperties();
        if (widthTwips is not null)
        {
            properties.Append(new W.TableCellWidth
            {
                Width = widthTwips.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Type = W.TableWidthUnitValues.Dxa,
            });
        }

        return new W.TableCell(
            properties,
            new W.Paragraph(new W.Run(CreateText(value))));
    }

    public static IEnumerable<OpenXmlElement> CreateBlocksForExistingDocument(
        WordprocessingDocument document,
        IReadOnlyList<DocumentBlock> blocks,
        WordPackageMutationContext context,
        OpenXmlElement anchor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(anchor);
        if (blocks.Count is < 1 or > 60)
        {
            throw new WordMcpException(
                "block_count_out_of_range",
                "$.edits[].blocks",
                "Inserted block batches must contain between 1 and 60 blocks.",
                "Split the edit into bounded logical batches.");
        }

        NumberingIds? numbering = null;
        var availableWidthTwips = AvailableWidthTwips(document, anchor);
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case DocumentBlockKind.Heading:
                    yield return CreateParagraph(
                        Runs(block),
                        ExistingStyle(document, $"Heading{block.Level ?? 1}"),
                        keepNext: true);
                    break;
                case DocumentBlockKind.Paragraph:
                    yield return CreateParagraph(Runs(block), ExistingStyle(document, "Normal"), keepLines: true);
                    break;
                case DocumentBlockKind.Callout:
                    yield return CreateParagraph(Runs(block), ExistingStyle(document, "Callout", "Quote", "Normal"), keepLines: true);
                    break;
                case DocumentBlockKind.Quote:
                    yield return CreateParagraph(Runs(block), ExistingStyle(document, "Quote", "Normal"), keepLines: true);
                    break;
                case DocumentBlockKind.Caption:
                    yield return CreateParagraph(Runs(block), ExistingStyle(document, "Caption", "Normal"), keepLines: true);
                    break;
                case DocumentBlockKind.UnorderedList:
                case DocumentBlockKind.OrderedList:
                    numbering ??= EnsureNumbering(document, context);
                    var numberId = block.Kind == DocumentBlockKind.OrderedList
                        ? numbering.OrderedNumberId
                        : numbering.UnorderedNumberId;
                    foreach (var item in block.Items ?? throw InvalidBlock(block.Kind))
                    {
                        yield return CreateListParagraph(item.Runs, item.Level, numberId, ExistingStyle(document, "Normal"));
                    }

                    break;
                case DocumentBlockKind.Table:
                    yield return CreateTable(block.Table ?? throw InvalidBlock(block.Kind), availableWidthTwips);
                    break;
                case DocumentBlockKind.KeyValue:
                    yield return CreateKeyValueTable(block.KeyValues ?? throw InvalidBlock(block.Kind), availableWidthTwips);
                    break;
                case DocumentBlockKind.PageBreak:
                    yield return new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page }));
                    break;
                case DocumentBlockKind.SectionBreak:
                    yield return CreateSectionBreak(block.SectionBreakKind ?? throw InvalidBlock(block.Kind));
                    break;
                case DocumentBlockKind.Image:
                    throw new WordMcpException(
                        "atomic_image_requires_snapshot",
                        "$.edits[].blocks",
                        "Atomic image insertion requires a pre-resolved opaque image snapshot, which this operation does not accept.",
                        "Create or refine the declarative document with an image_file_id resolved by the worker.");
                default:
                    throw InvalidBlock(block.Kind);
            }
        }
    }

    public static W.Paragraph CreateParagraph(
        IReadOnlyList<SemanticRun> runs,
        string? styleId,
        bool keepNext = false,
        bool keepLines = false)
    {
        var properties = new W.ParagraphProperties();
        if (!string.IsNullOrWhiteSpace(styleId))
        {
            properties.Append(new W.ParagraphStyleId { Val = styleId });
        }

        if (keepNext)
        {
            properties.Append(new W.KeepNext());
        }

        if (keepLines)
        {
            properties.Append(new W.KeepLines());
        }

        properties.Append(new W.WidowControl());
        var paragraph = new W.Paragraph(properties);
        paragraph.Append(runs.Select(CreateSemanticRun));
        return paragraph;
    }

    public static W.Table CreateTable(TableSpec table, int availableWidthTwips = 9_000)
    {
        if (table.Columns.Count is < 1 or > 12 || table.Rows.Count > 200
            || table.Rows.Any(row => row.Count != table.Columns.Count))
        {
            throw new WordMcpException(
                "table_dimensions_out_of_range",
                "$.blocks[].table",
                "The table dimensions are outside the supported rectangular range.",
                "Use 1 to 12 columns, at most 200 rows, and the same number of cells in every row.");
        }

        if (availableWidthTwips < 720)
        {
            throw new WordMcpException(
                "page_content_width_out_of_range",
                "$.layout",
                "The current page, margins, and columns leave too little usable width for a native Word table.",
                "Reduce the margins or column count before inserting the table.");
        }

        var baseColumnWidth = availableWidthTwips / table.Columns.Count;
        var remainder = availableWidthTwips % table.Columns.Count;
        var columnWidths = Enumerable.Range(0, table.Columns.Count)
            .Select(index => baseColumnWidth + (index < remainder ? 1 : 0))
            .ToArray();
        var properties = new W.TableProperties(
            new W.TableWidth { Width = availableWidthTwips.ToString(System.Globalization.CultureInfo.InvariantCulture), Type = W.TableWidthUnitValues.Dxa },
            new W.TableBorders(
                Border<W.TopBorder>(),
                Border<W.LeftBorder>(),
                Border<W.BottomBorder>(),
                Border<W.RightBorder>(),
                Border<W.InsideHorizontalBorder>(),
                Border<W.InsideVerticalBorder>()),
            new W.TableLayout { Type = W.TableLayoutValues.Fixed });
        if (!string.IsNullOrWhiteSpace(table.Caption))
        {
            properties.Append(new W.TableCaption { Val = table.Caption });
        }

        if (!string.IsNullOrWhiteSpace(table.Description))
        {
            properties.Append(new W.TableDescription { Val = table.Description });
        }

        var result = new W.Table(properties, new W.TableGrid(columnWidths.Select(width => new W.GridColumn
        {
            Width = width.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })));
        var headerProperties = new W.TableRowProperties(new W.TableHeader());
        if (!table.AllowRowSplit)
        {
            headerProperties.Append(new W.CantSplit());
        }

        var header = new W.TableRow(headerProperties);
        header.Append(table.Columns.Select((value, index) => CreateTableCell(value, columnWidths[index])));
        result.Append(header);
        foreach (var row in table.Rows)
        {
            var rowProperties = new W.TableRowProperties();
            if (!table.AllowRowSplit)
            {
                rowProperties.Append(new W.CantSplit());
            }

            var tableRow = new W.TableRow(rowProperties);
            tableRow.Append(row.Select((value, index) => CreateTableCell(value, columnWidths[index])));
            result.Append(tableRow);
        }

        return result;
    }

    public static W.Table CreateKeyValueTable(IReadOnlyList<KeyValueSpec> pairs, int availableWidthTwips = 9_000)
    {
        if (pairs.Count is < 1 or > 50)
        {
            throw InvalidBlock(DocumentBlockKind.KeyValue);
        }

        var table = new TableSpec(
            ["項目", "内容"],
            pairs.Select(pair => (IReadOnlyList<string>)[pair.Key, pair.Value]).ToArray(),
            AllowRowSplit: false);
        return CreateTable(table, availableWidthTwips);
    }

    private static int AvailableWidthTwips(WordprocessingDocument document, OpenXmlElement anchor)
    {
        var body = document.MainDocumentPart?.Document?.Body
                   ?? throw new InvalidOperationException("Missing main document body.");
        var anchorBlock = anchor.Ancestors<OpenXmlElement>().FirstOrDefault(element => element.Parent == body)
                          ?? (anchor.Parent == body ? anchor : null)
                          ?? throw new WordMcpException(
                              "main_story_block_required",
                              "$.edits[].target_id",
                              "A page-aware insertion requires a main-story block target.",
                              "Select a paragraph, heading, or table target in the main document body.");
        var children = body.ChildElements.ToArray();
        var anchorIndex = Array.IndexOf(children, anchorBlock);
        var section = children
            .Skip(Math.Max(0, anchorIndex))
            .OfType<W.Paragraph>()
            .Select(paragraph => paragraph.ParagraphProperties?.GetFirstChild<W.SectionProperties>())
            .FirstOrDefault(properties => properties is not null)
            ?? body.Elements<W.SectionProperties>().LastOrDefault();
        return AvailableWidthTwips(section);
    }

    internal static int AvailableWidthTwips(W.SectionProperties? section)
    {
        const int defaultPageWidth = 12_240;
        const int defaultMargin = 1_440;
        const int defaultColumnSpace = 720;
        var pageWidth = checked((int)(section?.GetFirstChild<W.PageSize>()?.Width?.Value ?? defaultPageWidth));
        var margins = section?.GetFirstChild<W.PageMargin>();
        var left = checked((int)(margins?.Left?.Value ?? defaultMargin));
        var right = checked((int)(margins?.Right?.Value ?? defaultMargin));
        var bodyWidth = pageWidth - left - right;
        var columns = section?.GetFirstChild<W.Columns>();
        var columnCount = Math.Max(1, (int)(columns?.ColumnCount?.Value ?? 1));
        var columnSpace = int.TryParse(
            columns?.Space?.Value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedColumnSpace)
            ? parsedColumnSpace
            : defaultColumnSpace;
        return (bodyWidth - ((columnCount - 1) * columnSpace)) / columnCount;
    }

    private static W.Paragraph CreateListParagraph(
        IReadOnlyList<SemanticRun> runs,
        int level,
        int numberId,
        string? styleId)
    {
        if (level is < 0 or > 3)
        {
            throw new WordMcpException(
                "list_level_out_of_range",
                "$.blocks[].items[].level",
                "List levels must be between 0 and 3.",
                "Use at most four native numbering levels.");
        }

        var paragraph = CreateParagraph(runs, styleId, keepLines: true);
        paragraph.ParagraphProperties!.Append(new W.NumberingProperties(
            new W.NumberingLevelReference { Val = level },
            new W.NumberingId { Val = numberId }));
        return paragraph;
    }

    private static W.Paragraph CreateSectionBreak(SectionBreakKind kind)
    {
        var sectionType = kind switch
        {
            SectionBreakKind.Continuous => W.SectionMarkValues.Continuous,
            SectionBreakKind.EvenPage => W.SectionMarkValues.EvenPage,
            SectionBreakKind.OddPage => W.SectionMarkValues.OddPage,
            _ => W.SectionMarkValues.NextPage,
        };
        return new W.Paragraph(
            new W.ParagraphProperties(new W.SectionProperties(new W.SectionType { Val = sectionType })));
    }

    private static NumberingIds EnsureNumbering(WordprocessingDocument document, WordPackageMutationContext context)
    {
        var main = document.MainDocumentPart ?? throw new InvalidOperationException("Missing main document part.");
        var numberingPart = main.NumberingDefinitionsPart;
        if (numberingPart is null)
        {
            numberingPart = main.AddNewPart<NumberingDefinitionsPart>();
            numberingPart.Numbering = new W.Numbering();
            context.MarkChangedEntry(RelationshipEntry(main.Uri));
        }

        numberingPart.Numbering ??= new W.Numbering();
        var maxAbstractId = numberingPart.Numbering.Elements<W.AbstractNum>()
            .Select(value => value.AbstractNumberId?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var maxNumberId = numberingPart.Numbering.Elements<W.NumberingInstance>()
            .Select(value => value.NumberID?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var unorderedAbstractId = maxAbstractId + 1;
        var orderedAbstractId = unorderedAbstractId + 1;
        var unorderedNumberId = maxNumberId + 1;
        var orderedNumberId = unorderedNumberId + 1;
        numberingPart.Numbering.Append(
            CreateAbstractNumbering(unorderedAbstractId, ordered: false),
            CreateAbstractNumbering(orderedAbstractId, ordered: true),
            new W.NumberingInstance(new W.AbstractNumId { Val = unorderedAbstractId }) { NumberID = unorderedNumberId },
            new W.NumberingInstance(new W.AbstractNumId { Val = orderedAbstractId }) { NumberID = orderedNumberId });
        context.MarkChanged(numberingPart);
        return new NumberingIds(unorderedNumberId, orderedNumberId);
    }

    internal static W.AbstractNum CreateAbstractNumbering(int id, bool ordered)
    {
        var abstractNumber = new W.AbstractNum { AbstractNumberId = id };
        abstractNumber.Append(new W.MultiLevelType { Val = W.MultiLevelValues.Multilevel });
        for (var level = 0; level < 4; level++)
        {
            var levelElement = new W.Level { LevelIndex = level };
            levelElement.Append(
                new W.StartNumberingValue { Val = 1 },
                new W.NumberingFormat { Val = ordered ? W.NumberFormatValues.Decimal : W.NumberFormatValues.Bullet },
                new W.LevelText { Val = ordered ? $"%{level + 1}." : level % 2 == 0 ? "●" : "○" },
                new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                new W.PreviousParagraphProperties(
                    new W.Indentation
                    {
                        Left = ((level + 1) * 720).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Hanging = "360",
                    }));
            abstractNumber.Append(levelElement);
        }

        return abstractNumber;
    }

    private static string? ExistingStyle(WordprocessingDocument document, params string[] candidates)
    {
        var styleIds = document.MainDocumentPart?.StyleDefinitionsPart?.Styles?.Elements<W.Style>()
            .Select(style => style.StyleId?.Value)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        return candidates.FirstOrDefault(styleIds.Contains);
    }

    private static IReadOnlyList<SemanticRun> Runs(DocumentBlock block)
    {
        if (block.Runs is { Count: > 0 })
        {
            return block.Runs;
        }

        if (block.Text is not null)
        {
            return [new SemanticRun(block.Text)];
        }

        throw InvalidBlock(block.Kind);
    }

    private static T Border<T>()
        where T : W.BorderType, new() => new()
        {
            Val = W.BorderValues.Single,
            Size = 4,
            Color = "B7C9D6",
        };

    private static W.Text CreateText(string value)
    {
        var text = new W.Text(value);
        WordTextMap.UpdateSpace(text);
        return text;
    }

    private static string RelationshipEntry(Uri partUri)
    {
        var normalized = WordPackageEditor.NormalizeEntryName(partUri.ToString());
        var directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
        var fileName = Path.GetFileName(normalized);
        return string.IsNullOrEmpty(directory)
            ? $"_rels/{fileName}.rels"
            : $"{directory}/_rels/{fileName}.rels";
    }

    private static WordMcpException InvalidBlock(DocumentBlockKind kind) => new(
        "invalid_or_unsupported_block",
        "$.blocks",
        $"The {kind} block is missing its required constrained semantic payload or is unsupported here.",
        "Use a supported block with exactly one matching semantic payload.");

    private sealed record NumberingIds(int UnorderedNumberId, int OrderedNumberId);
}
