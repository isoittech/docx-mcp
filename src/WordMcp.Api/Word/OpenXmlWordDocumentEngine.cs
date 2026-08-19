using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Configuration;
using WordMcp.Domain;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Word;

public sealed partial class OpenXmlWordDocumentEngine : IWordDocumentEngine
{
    private const int SnippetLength = 160;
    private const int AnalysisExcerptLength = 2_000;
    private readonly WordMcpOptions options;
    private readonly TimeProvider timeProvider;
    private readonly WordPackageEditor packageEditor;
    private readonly OpenXmlValidationGate validationGate;

    public OpenXmlWordDocumentEngine(IOptions<WordMcpOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), TimeProvider.System)
    {
    }

    public OpenXmlWordDocumentEngine(WordMcpOptions options, TimeProvider? timeProvider = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        packageEditor = new WordPackageEditor();
        validationGate = new OpenXmlValidationGate();
    }

    public AnalysisSnapshot Analyze(WordAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSourceRequest(request.SourcePath, request.ExpectedSourceSha256);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceSha256 = ComputeSha256(request.SourcePath);
        if (!string.Equals(sourceSha256, request.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new WordMcpException(
                "source_hash_mismatch",
                "$.source_file_id",
                "The immutable Word source no longer matches the expected SHA-256.",
                "Resolve and copy the source again, then create a fresh analysis snapshot.");
        }

        try
        {
            var hasDigitalSignature = OoxmlDigitalSignaturePolicy.IsPresent(request.SourcePath);
            using var document = WordprocessingDocument.Open(request.SourcePath, false);
            var mainPart = document.MainDocumentPart ?? throw InvalidDocument("missing_main_document", "The package has no main Word document part.");
            var mainDocument = mainPart.Document ?? throw InvalidDocument("missing_main_document_root", "The main Word document part has no document root.");
            var stories = BuildStoryContexts(mainPart);
            var items = CreateItemBuckets();
            var targets = new Dictionary<string, TargetRecord>(StringComparer.Ordinal);
            var features = DetectFeatures(document, stories, hasDigitalSignature);
            var unsupportedFeatures = UnsupportedFeatures(features);
            var globalTags = stories
                .SelectMany(story => story.Root.Descendants().Where(IsSdt))
                .Select(GetSdtTag)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .GroupBy(tag => tag!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var characterCount = 0;
            var logicalBlockCount = 0;
            foreach (var story in stories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AnalyzeStory(story, globalTags, items, targets, ref characterCount, ref logicalBlockCount);
            }

            AnalyzeSections(mainPart, items);
            AnalyzeStyles(mainPart, items);
            AnalyzeNumbering(mainPart, items);
            AnalyzeImages(mainPart, items);

            if (characterCount > options.MaxCharacters || logicalBlockCount > options.MaxBlocks)
            {
                throw new WordMcpException(
                    "semantic_limit_exceeded",
                    "$.source_file_id",
                    "The document exceeds the configured semantic analysis limit.",
                    "Split the document into smaller macro-free DOCX files before analysis.",
                    unsafeDocument: true);
            }

            var now = timeProvider.GetUtcNow();
            var expiresAt = now.AddMinutes(options.AnalysisLifetimeMinutes);
            var analysisId = NewOpaqueId("ana_");
            var readOnlyItems = items.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<AnalysisItem>)new ReadOnlyCollection<AnalysisItem>(pair.Value),
                StringComparer.Ordinal);
            var availableKinds = readOnlyItems
                .Where(pair => pair.Value.Count > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
            var storyNames = stories.Select(story => story.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var summary = new AnalysisSummary(
                analysisId,
                sourceSha256,
                DocumentProperties(document),
                DetectLocale(mainPart),
                characterCount,
                logicalBlockCount,
                Math.Max(1, mainDocument.Body?.Descendants<W.SectionProperties>().Count() ?? 0),
                storyNames,
                features.Order(StringComparer.Ordinal).ToArray(),
                unsupportedFeatures,
                availableKinds,
                expiresAt);

            return new AnalysisSnapshot(
                analysisId,
                request.UserScope,
                request.ConversationScope,
                sourceSha256,
                Path.GetFullPath(request.SourcePath),
                request.SourceFileName,
                summary,
                readOnlyItems,
                new ReadOnlyDictionary<string, TargetRecord>(targets),
                now,
                expiresAt);
        }
        catch (WordMcpException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or OpenXmlPackageException
                                          or InvalidDataException
                                          or System.Xml.XmlException)
        {
            throw InvalidDocument("invalid_openxml_package", "The input could not be opened as a WordprocessingML package.");
        }
    }

    public OpenXmlValidationReport ValidateExistingEdit(
        string sourcePath,
        string candidatePath,
        CancellationToken cancellationToken = default) =>
        validationGate.ValidateExistingEdit(sourcePath, candidatePath, cancellationToken);

    public OpenXmlValidationReport ValidateNewDocument(
        string candidatePath,
        CancellationToken cancellationToken = default) =>
        validationGate.ValidateNewDocument(candidatePath, cancellationToken);

    private static Dictionary<string, List<AnalysisItem>> CreateItemBuckets() => new(StringComparer.Ordinal)
    {
        ["outline"] = [],
        ["blocks"] = [],
        ["tables"] = [],
        ["cells"] = [],
        ["controls"] = [],
        ["bookmarks"] = [],
        ["fields"] = [],
        ["sections"] = [],
        ["styles"] = [],
        ["numbering"] = [],
        ["images"] = [],
        ["headers_footers"] = [],
    };

    private static List<StoryContext> BuildStoryContexts(MainDocumentPart mainPart)
    {
        var mainDocument = mainPart.Document ?? throw InvalidDocument(
            "missing_main_document_root",
            "The main Word document part has no document root.");
        var result = new List<StoryContext>
        {
            new("main", mainPart.Uri.ToString(), mainPart, mainDocument, Restricted: false),
        };

        foreach (var part in mainPart.HeaderParts.OrderBy(part => part.Uri.ToString(), StringComparer.Ordinal))
        {
            if (part.Header is not null)
            {
                result.Add(new StoryContext("header", part.Uri.ToString(), part, part.Header, Restricted: false));
            }
        }

        foreach (var part in mainPart.FooterParts.OrderBy(part => part.Uri.ToString(), StringComparer.Ordinal))
        {
            if (part.Footer is not null)
            {
                result.Add(new StoryContext("footer", part.Uri.ToString(), part, part.Footer, Restricted: false));
            }
        }

        if (mainPart.FootnotesPart?.Footnotes is { } footnotes)
        {
            result.Add(new StoryContext("footnote", mainPart.FootnotesPart.Uri.ToString(), mainPart.FootnotesPart, footnotes, Restricted: true));
        }

        if (mainPart.EndnotesPart?.Endnotes is { } endnotes)
        {
            result.Add(new StoryContext("endnote", mainPart.EndnotesPart.Uri.ToString(), mainPart.EndnotesPart, endnotes, Restricted: true));
        }

        if (mainPart.WordprocessingCommentsPart?.Comments is { } comments)
        {
            result.Add(new StoryContext("comment", mainPart.WordprocessingCommentsPart.Uri.ToString(), mainPart.WordprocessingCommentsPart, comments, Restricted: true));
        }

        var parentStories = result.ToArray();
        foreach (var parent in parentStories)
        {
            var index = 0;
            foreach (var textBox in parent.Root.Descendants().Where(element => element.LocalName == "txbxContent"))
            {
                result.Add(new StoryContext($"textbox:{index++}", parent.PartUri, parent.Part, textBox, Restricted: true));
            }
        }

        return result;
    }

    private static void AnalyzeStory(
        StoryContext story,
        Dictionary<string, int> globalTags,
        Dictionary<string, List<AnalysisItem>> items,
        Dictionary<string, TargetRecord> targets,
        ref int characterCount,
        ref int logicalBlockCount)
    {
        var paragraphs = StoryDescendants<W.Paragraph>(story).ToArray();
        var tables = StoryDescendants<W.Table>(story).ToArray();
        var tableOrdinals = new Dictionary<OpenXmlElement, int>(ReferenceComparer<OpenXmlElement>.Instance);
        for (var index = 0; index < tables.Length; index++)
        {
            tableOrdinals.Add(tables[index], index);
        }

        for (var ordinal = 0; ordinal < paragraphs.Length; ordinal++)
        {
            var paragraph = paragraphs[ordinal];
            var text = WordTextMap.Create(paragraph).Text;
            characterCount += text.Length;
            logicalBlockCount++;
            var kind = IsHeading(paragraph) ? "heading" : "paragraph";
            var restricted = story.Restricted || HasProtectedBoundary(paragraph, allowDrawing: false);
            var target = NewTarget(kind, story, ordinal, paragraph, restricted, tableOrdinals);
            targets.Add(target.TargetId, target);
            var data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = AnalysisExcerpt(text),
                ["snippet_truncated"] = text.Length > AnalysisExcerptLength,
                ["style_id"] = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value,
                ["numbering_id"] = paragraph.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value,
                ["numbering_level"] = paragraph.ParagraphProperties?.NumberingProperties?.NumberingLevelReference?.Val?.Value,
                ["editable"] = !restricted,
            };
            items["blocks"].Add(new AnalysisItem(kind, target.TargetId, story.Name, data));
            if (kind == "heading")
            {
                data["level"] = HeadingLevel(paragraph);
                items["outline"].Add(new AnalysisItem(kind, target.TargetId, story.Name, data));
            }
        }

        for (var tableOrdinal = 0; tableOrdinal < tables.Length; tableOrdinal++)
        {
            var table = tables[tableOrdinal];
            logicalBlockCount++;
            var rows = table.Elements<W.TableRow>().ToArray();
            var columnCount = rows.Select(row => row.Elements<W.TableCell>().Count()).DefaultIfEmpty(0).Max();
            var merged = table.Descendants<W.GridSpan>().Any(span => (span.Val?.Value ?? 1) > 1)
                         || table.Descendants<W.VerticalMerge>().Any();
            var restricted = story.Restricted || merged || HasProtectedBoundary(table, allowDrawing: false);
            var tableTarget = NewTarget("table", story, tableOrdinal, table, restricted, tableOrdinals);
            targets.Add(tableTarget.TargetId, tableTarget);
            items["tables"].Add(new AnalysisItem(
                "table",
                tableTarget.TargetId,
                story.Name,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["rows"] = rows.Length,
                    ["columns"] = columnCount,
                    ["merged"] = merged,
                    ["header_row"] = rows.FirstOrDefault()?.TableRowProperties?.GetFirstChild<W.TableHeader>() is not null,
                    ["caption"] = table.TableProperties?.GetFirstChild<W.TableCaption>()?.Val?.Value,
                    ["description"] = table.TableProperties?.GetFirstChild<W.TableDescription>()?.Val?.Value,
                    ["editable"] = !restricted,
                }));

            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                var cells = rows[rowIndex].Elements<W.TableCell>().ToArray();
                for (var columnIndex = 0; columnIndex < cells.Length; columnIndex++)
                {
                    var cell = cells[columnIndex];
                    var cellText = string.Join("\n", cell.Descendants<W.Paragraph>().Select(paragraph => WordTextMap.Create(paragraph).Text));
                    var cellTarget = new TargetRecord(
                        NewOpaqueId("tgt_"),
                        "cell",
                        story.Name,
                        story.PartUri,
                        (rowIndex * Math.Max(1, columnCount)) + columnIndex,
                        tableOrdinal,
                        rowIndex,
                        columnIndex,
                        Snippet(cellText),
                        restricted);
                    targets.Add(cellTarget.TargetId, cellTarget);
                    items["cells"].Add(new AnalysisItem(
                        "cell",
                        cellTarget.TargetId,
                        story.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["table_ordinal"] = tableOrdinal,
                            ["row_index"] = rowIndex,
                            ["column_index"] = columnIndex,
                            ["text"] = AnalysisExcerpt(cellText),
                            ["snippet_truncated"] = cellText.Length > AnalysisExcerptLength,
                            ["editable"] = !restricted,
                        }));
                }
            }
        }

        var controls = StoryDescendants(story, IsSdt).ToArray();
        for (var ordinal = 0; ordinal < controls.Length; ordinal++)
        {
            var control = controls[ordinal];
            var tag = GetSdtTag(control);
            var alias = control.Descendants<W.SdtAlias>().FirstOrDefault()?.Val?.Value;
            var nested = control.Descendants().Any(IsSdt);
            var locked = control.Descendants<W.Lock>().Any();
            var dataBound = control.Descendants<W.DataBinding>().Any();
            var repeating = control.Descendants().Any(element => element.LocalName is "repeatingSection" or "repeatingSectionItem");
            var simpleKind = control.LocalName switch
            {
                "sdt" when control.GetType().Name == "SdtRun" => "inline",
                "sdt" when control.GetType().Name == "SdtCell" => "cell",
                _ => "block",
            };
            var duplicate = tag is not null && globalTags.TryGetValue(tag, out var count) && count != 1;
            var restricted = story.Restricted || string.IsNullOrWhiteSpace(tag) || nested || locked || dataBound || repeating || duplicate;
            var target = new TargetRecord(
                NewOpaqueId("tgt_"),
                "content_control",
                story.Name,
                story.PartUri,
                ordinal,
                null,
                null,
                null,
                Snippet(WordTextMap.VisibleText(control)),
                restricted);
            targets.Add(target.TargetId, target);
            items["controls"].Add(new AnalysisItem(
                "content_control",
                target.TargetId,
                story.Name,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tag"] = tag,
                    ["alias"] = alias,
                    ["control_kind"] = simpleKind,
                    ["locked"] = locked,
                    ["data_bound"] = dataBound,
                    ["nested"] = nested,
                    ["repeating"] = repeating,
                    ["duplicate_tag"] = duplicate,
                    ["editable"] = !restricted,
                }));
        }

        var bookmarks = StoryDescendants<W.BookmarkStart>(story).ToArray();
        for (var ordinal = 0; ordinal < bookmarks.Length; ordinal++)
        {
            var bookmark = bookmarks[ordinal];
            var target = new TargetRecord(
                NewOpaqueId("tgt_"),
                "bookmark",
                story.Name,
                story.PartUri,
                ordinal,
                null,
                null,
                null,
                Snippet(bookmark.Name?.Value ?? string.Empty),
                Restricted: true);
            targets.Add(target.TargetId, target);
            items["bookmarks"].Add(new AnalysisItem(
                "bookmark",
                target.TargetId,
                story.Name,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = bookmark.Name?.Value,
                    ["id"] = bookmark.Id?.Value,
                    ["hidden"] = bookmark.Name?.Value?.StartsWith('_') == true,
                }));
        }

        foreach (var instruction in FieldInstructions(story.Root))
        {
            items["fields"].Add(new AnalysisItem(
                "field",
                null,
                story.Name,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field_type"] = instruction,
                    ["part_uri"] = story.PartUri,
                }));
        }

        if (story.Name is "header" or "footer")
        {
            items["headers_footers"].Add(new AnalysisItem(
                story.Name,
                null,
                story.Name,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["part_uri"] = story.PartUri,
                    ["text"] = Snippet(string.Join("\n", paragraphs.Select(paragraph => WordTextMap.Create(paragraph).Text))),
                    ["has_page"] = FieldInstructions(story.Root).Contains("PAGE", StringComparer.Ordinal),
                    ["has_numpages"] = FieldInstructions(story.Root).Contains("NUMPAGES", StringComparer.Ordinal),
                }));
        }
    }

    private static IEnumerable<T> StoryDescendants<T>(StoryContext story)
        where T : OpenXmlElement => StoryDescendants(story, element => element is T).Cast<T>();

    private static IEnumerable<OpenXmlElement> StoryDescendants(StoryContext story, Func<OpenXmlElement, bool> predicate)
    {
        var isTextBoxStory = story.Name.StartsWith("textbox:", StringComparison.Ordinal);
        return story.Root.Descendants()
            .Where(predicate)
            .Where(element => isTextBoxStory || !element.Ancestors().Any(ancestor => ancestor.LocalName == "txbxContent"));
    }

    private static TargetRecord NewTarget(
        string kind,
        StoryContext story,
        int ordinal,
        OpenXmlElement element,
        bool restricted,
        Dictionary<OpenXmlElement, int> tableOrdinals)
    {
        var parentTable = element.Ancestors<W.Table>().FirstOrDefault();
        var parentOrdinal = parentTable is not null && tableOrdinals.TryGetValue(parentTable, out var tableOrdinal)
            ? (int?)tableOrdinal
            : null;
        return new TargetRecord(
            NewOpaqueId("tgt_"),
            kind,
            story.Name,
            story.PartUri,
            ordinal,
            parentOrdinal,
            null,
            null,
            Snippet(WordTextMap.VisibleText(element)),
            restricted);
    }

    private static void AnalyzeSections(MainDocumentPart mainPart, Dictionary<string, List<AnalysisItem>> items)
    {
        var body = mainPart.Document?.Body;
        var sections = body?.Descendants<W.SectionProperties>().ToArray() ?? [];
        if (sections.Length == 0 && body is not null)
        {
            sections = [new W.SectionProperties()];
        }

        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            var pageSize = section.GetFirstChild<W.PageSize>();
            var margins = section.GetFirstChild<W.PageMargin>();
            var headerReferences = section.Elements<W.HeaderReference>().ToArray();
            var footerReferences = section.Elements<W.FooterReference>().ToArray();
            items["sections"].Add(new AnalysisItem(
                "section",
                null,
                "main",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ordinal"] = index,
                    ["page_width_twips"] = pageSize?.Width?.Value,
                    ["page_height_twips"] = pageSize?.Height?.Value,
                    ["orientation"] = pageSize?.Orient?.Value.ToString(),
                    ["margin_top_twips"] = margins?.Top?.Value,
                    ["margin_right_twips"] = margins?.Right?.Value,
                    ["margin_bottom_twips"] = margins?.Bottom?.Value,
                    ["margin_left_twips"] = margins?.Left?.Value,
                    ["columns"] = section.GetFirstChild<W.Columns>()?.ColumnCount?.Value ?? 1,
                    ["break_type"] = section.GetFirstChild<W.SectionType>()?.Val?.Value.ToString(),
                    ["default_header_part_uri"] = ReferencedPartUri(mainPart, headerReferences, W.HeaderFooterValues.Default),
                    ["first_header_part_uri"] = ReferencedPartUri(mainPart, headerReferences, W.HeaderFooterValues.First),
                    ["even_header_part_uri"] = ReferencedPartUri(mainPart, headerReferences, W.HeaderFooterValues.Even),
                    ["default_footer_part_uri"] = ReferencedPartUri(mainPart, footerReferences, W.HeaderFooterValues.Default),
                    ["first_footer_part_uri"] = ReferencedPartUri(mainPart, footerReferences, W.HeaderFooterValues.First),
                    ["even_footer_part_uri"] = ReferencedPartUri(mainPart, footerReferences, W.HeaderFooterValues.Even),
                    ["default_header_link_to_previous"] = index > 0 && !HasReference(headerReferences, W.HeaderFooterValues.Default),
                    ["first_header_link_to_previous"] = index > 0 && !HasReference(headerReferences, W.HeaderFooterValues.First),
                    ["even_header_link_to_previous"] = index > 0 && !HasReference(headerReferences, W.HeaderFooterValues.Even),
                    ["default_footer_link_to_previous"] = index > 0 && !HasReference(footerReferences, W.HeaderFooterValues.Default),
                    ["first_footer_link_to_previous"] = index > 0 && !HasReference(footerReferences, W.HeaderFooterValues.First),
                    ["even_footer_link_to_previous"] = index > 0 && !HasReference(footerReferences, W.HeaderFooterValues.Even),
                }));
        }
    }

    private static bool HasReference<T>(IEnumerable<T> references, W.HeaderFooterValues type)
        where T : OpenXmlLeafElement => references.Any(reference => reference switch
        {
            W.HeaderReference header => header.Type?.Value == type,
            W.FooterReference footer => footer.Type?.Value == type,
            _ => false,
        });

    private static string? ReferencedPartUri<T>(
        MainDocumentPart mainPart,
        IEnumerable<T> references,
        W.HeaderFooterValues type)
        where T : OpenXmlLeafElement
    {
        var relationshipId = references.Select(reference => reference switch
            {
                W.HeaderReference header when header.Type?.Value == type => header.Id?.Value,
                W.FooterReference footer when footer.Type?.Value == type => footer.Id?.Value,
                _ => null,
            })
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        return relationshipId is null ? null : mainPart.GetPartById(relationshipId).Uri.ToString();
    }

    private static void AnalyzeStyles(MainDocumentPart mainPart, Dictionary<string, List<AnalysisItem>> items)
    {
        var styles = mainPart.StyleDefinitionsPart?.Styles?.Elements<W.Style>() ?? [];
        foreach (var style in styles)
        {
            var runProperties = style.StyleRunProperties;
            items["styles"].Add(new AnalysisItem(
                "style",
                null,
                "styles",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["style_id"] = style.StyleId?.Value,
                    ["name"] = style.StyleName?.Val?.Value,
                    ["type"] = style.Type?.Value.ToString(),
                    ["based_on"] = style.BasedOn?.Val?.Value,
                    ["next"] = style.NextParagraphStyle?.Val?.Value,
                    ["latin_font"] = runProperties?.RunFonts?.Ascii?.Value,
                    ["east_asia_font"] = runProperties?.RunFonts?.EastAsia?.Value,
                    ["theme_color"] = runProperties?.Color?.ThemeColor?.Value.ToString(),
                }));
        }
    }

    private static void AnalyzeNumbering(MainDocumentPart mainPart, Dictionary<string, List<AnalysisItem>> items)
    {
        var numbering = mainPart.NumberingDefinitionsPart?.Numbering;
        if (numbering is null)
        {
            return;
        }

        foreach (var instance in numbering.Elements<W.NumberingInstance>())
        {
            items["numbering"].Add(new AnalysisItem(
                "numbering_instance",
                null,
                "numbering",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["num_id"] = instance.NumberID?.Value,
                    ["abstract_num_id"] = instance.AbstractNumId?.Val?.Value,
                }));
        }

        foreach (var definition in numbering.Elements<W.AbstractNum>())
        {
            foreach (var level in definition.Elements<W.Level>())
            {
                items["numbering"].Add(new AnalysisItem(
                    "numbering_level",
                    null,
                    "numbering",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["abstract_num_id"] = definition.AbstractNumberId?.Value,
                        ["level"] = level.LevelIndex?.Value,
                        ["format"] = level.NumberingFormat?.Val?.Value.ToString(),
                        ["text"] = level.LevelText?.Val?.Value,
                    }));
            }
        }
    }

    private static void AnalyzeImages(MainDocumentPart mainPart, Dictionary<string, List<AnalysisItem>> items)
    {
        foreach (var story in BuildStoryContexts(mainPart))
        {
            foreach (var drawing in story.Root.Descendants<W.Drawing>())
            {
                var blip = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Blip>()
                    .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Embed?.Value));
                if (blip?.Embed?.Value is not { } relationshipId
                    || story.Part.GetPartById(relationshipId) is not ImagePart part)
                {
                    continue;
                }

                var properties = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>()
                    .FirstOrDefault();
                var extent = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>()
                    .FirstOrDefault();
                var altText = properties?.Description?.Value;
                items["images"].Add(new AnalysisItem(
                    "image",
                    null,
                    story.Name,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["part_uri"] = part.Uri.ToString(),
                        ["content_type"] = part.ContentType,
                        ["embedded"] = true,
                        ["width_emu"] = extent?.Cx?.Value,
                        ["height_emu"] = extent?.Cy?.Value,
                        ["alt_text_present"] = !string.IsNullOrWhiteSpace(altText),
                        ["alt_text"] = altText is null ? null : AnalysisExcerpt(altText),
                    }));
            }
        }
    }

    private static IEnumerable<OpenXmlPart> DescendantParts(OpenXmlPartContainer container)
    {
        var visited = new HashSet<OpenXmlPart>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<OpenXmlPart>(container.Parts.Select(pair => pair.OpenXmlPart));
        while (pending.TryPop(out var part))
        {
            if (!visited.Add(part))
            {
                continue;
            }

            yield return part;
            foreach (var child in part.Parts)
            {
                pending.Push(child.OpenXmlPart);
            }
        }
    }

    private static HashSet<string> DetectFeatures(
        WordprocessingDocument document,
        IReadOnlyList<StoryContext> stories,
        bool hasDigitalSignature)
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        var roots = stories.Select(story => story.Root).Distinct(ReferenceComparer<OpenXmlElement>.Instance).ToArray();
        AddIf(features, "headers", stories.Any(story => story.Name == "header"));
        AddIf(features, "footers", stories.Any(story => story.Name == "footer"));
        AddIf(features, "footnotes", stories.Any(story => story.Name == "footnote"));
        AddIf(features, "endnotes", stories.Any(story => story.Name == "endnote"));
        AddIf(features, "comments", stories.Any(story => story.Name == "comment"));
        AddIf(features, "text_boxes", stories.Any(story => story.Name.StartsWith("textbox:", StringComparison.Ordinal)));
        AddIf(features, "tracked_revisions", roots.Any(root => root.Descendants().Any(IsRevision)));
        AddIf(features, "hidden_text", roots.Any(root => root.Descendants<W.Vanish>().Any()));
        AddIf(features, "content_controls", roots.Any(root => root.Descendants().Any(IsSdt)));
        AddIf(features, "bookmarks", roots.Any(root => root.Descendants<W.BookmarkStart>().Any()));
        AddIf(features, "fields", roots.Any(root => FieldInstructions(root).Length > 0));
        AddIf(features, "document_protection", document.MainDocumentPart?.DocumentSettingsPart?.Settings?.GetFirstChild<W.DocumentProtection>() is not null);
        AddIf(features, "digital_signature", hasDigitalSignature);
        AddIf(features, "custom_xml", document.MainDocumentPart?.CustomXmlParts.Any() == true);
        var parts = document.MainDocumentPart is null ? [] : DescendantParts(document.MainDocumentPart).ToArray();
        AddIf(features, "embedded_images", parts.OfType<ImagePart>().Any());
        AddIf(features, "charts", parts.Any(part => part.GetType().Name.Contains("Chart", StringComparison.Ordinal)));
        AddIf(features, "smartart", parts.Any(part => part.GetType().Name.Contains("Diagram", StringComparison.Ordinal)));
        AddIf(features, "equations", roots.Any(root => root.Descendants().Any(element => element.LocalName is "oMath" or "oMathPara")));
        AddIf(features, "numbering", document.MainDocumentPart?.NumberingDefinitionsPart is not null);

        return features;
    }

    private static string[] UnsupportedFeatures(HashSet<string> features)
    {
        var result = new List<string>();
        foreach (var feature in new[]
                 {
                     "footnotes", "endnotes", "comments", "text_boxes", "tracked_revisions", "hidden_text",
                     "custom_xml", "charts", "smartart", "equations", "document_protection", "digital_signature",
                 })
        {
            if (features.Contains(feature))
            {
                result.Add($"{feature}_editing");
            }
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, string?> DocumentProperties(WordprocessingDocument document) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["title"] = document.PackageProperties.Title,
            ["subject"] = document.PackageProperties.Subject,
            ["category"] = document.PackageProperties.Category,
            ["description"] = document.PackageProperties.Description,
            ["created"] = document.PackageProperties.Created?.ToString("O"),
            ["modified"] = document.PackageProperties.Modified?.ToString("O"),
        };

    private static string? DetectLocale(MainDocumentPart mainPart) =>
        mainPart.StyleDefinitionsPart?.Styles?.DocDefaults?
            .RunPropertiesDefault?.RunPropertiesBaseStyle?.Languages?.EastAsia?.Value
        ?? mainPart.Document?.Descendants<W.Languages>().Select(language => language.EastAsia?.Value ?? language.Val?.Value).FirstOrDefault(value => value is not null);

    private static string[] FieldInstructions(OpenXmlElement root)
    {
        var instructions = root.Descendants<W.FieldCode>()
            .Select(code => FieldName(code.Text))
            .Concat(root.Descendants<W.SimpleField>().Select(field => FieldName(field.Instruction?.Value)))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return instructions;
    }

    private static string FieldName(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return string.Empty;
        }

        return instruction.TrimStart().Split([' ', '\t', '\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
    }

    private static bool IsSdt(OpenXmlElement element) =>
        element.LocalName == "sdt"
        && element.NamespaceUri == "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string? GetSdtTag(OpenXmlElement element) => element.Descendants<W.Tag>().FirstOrDefault()?.Val?.Value;

    private static bool IsRevision(OpenXmlElement element) =>
        element.NamespaceUri == "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
        && element.LocalName is "ins" or "del" or "moveFrom" or "moveTo";

    private static bool IsHeading(W.Paragraph paragraph) => HeadingLevel(paragraph) is >= 1 and <= 9;

    private static int? HeadingLevel(W.Paragraph paragraph)
    {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style is not null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style["Heading".Length..], out var level))
        {
            return level;
        }

        var outline = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        return outline is null ? null : outline.Value + 1;
    }

    private static string Snippet(string value) => value.Length <= SnippetLength ? value : string.Concat(value.AsSpan(0, SnippetLength - 1), "…");

    private static string AnalysisExcerpt(string value) => value.Length <= AnalysisExcerptLength
        ? value
        : string.Concat(value.AsSpan(0, AnalysisExcerptLength - 1), "…");

    private static string NewOpaqueId(string prefix)
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return string.Concat(prefix, Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(bytes));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void ValidateSourceRequest(string path, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new WordMcpException(
                "source_not_found",
                "$.source_file_id",
                "The job-owned Word source snapshot was not found.",
                "Resolve the opaque source again without sending a local path.");
        }

        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("ExpectedSourceSha256 must be a 64-character hexadecimal SHA-256.", nameof(expectedSha256));
        }
    }

    private static void AddIf(HashSet<string> set, string value, bool condition)
    {
        if (condition)
        {
            set.Add(value);
        }
    }

    private static WordMcpException InvalidDocument(string code, string message) => new(
        code,
        "$.source_file_id",
        message,
        "Provide an unencrypted macro-free DOCX or DOTX that opens without repair warnings.",
        unsafeDocument: true);

    private sealed record StoryContext(
        string Name,
        string PartUri,
        OpenXmlPart Part,
        OpenXmlElement Root,
        bool Restricted);

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
