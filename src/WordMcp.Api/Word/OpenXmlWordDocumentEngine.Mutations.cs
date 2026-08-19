using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Domain;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Word;

public sealed partial class OpenXmlWordDocumentEngine
{
    public WordMutationResult ReplaceText(
        WordMutationRequest request,
        IReadOnlyList<TextReplacementRequest> replacements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        ValidateMutationRequest(request);
        if (replacements.Count is < 1 or > 100)
        {
            throw InvalidInput("replacement_count_out_of_range", "$.replacements", "Provide between 1 and 100 replacements.");
        }

        return ExecuteMutation(
            request,
            (document, context) =>
            {
                var planned = new List<PlannedReplacement>();
                foreach (var replacement in replacements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(replacement.ExpectedText) || replacement.ExpectedMatchCount < 1)
                    {
                        throw InvalidInput(
                            "invalid_replacement_expectation",
                            "$.replacements",
                            "Expected text must be non-empty and expected_match_count must be positive.");
                    }

                    EnsurePlainText(replacement.ExpectedText, "$.replacements[].expected_text");
                    EnsurePlainText(replacement.ReplacementText, "$.replacements[].replacement_text");
                    var target = GetTarget(request.Analysis, replacement.TargetId, "$.replacements[].target_id");
                    if (target.Restricted || target.Kind is not ("paragraph" or "heading"))
                    {
                        throw UnsupportedTarget("text_replacement_target_unsupported", "$.replacements[].target_id");
                    }

                    var resolved = ResolveTarget(document, target);
                    if (resolved.Element is not W.Paragraph paragraph)
                    {
                        throw TargetMismatch();
                    }

                    var map = WordTextMap.Create(paragraph);
                    var matches = map.Find(replacement.ExpectedText);
                    if (matches.Count != replacement.ExpectedMatchCount)
                    {
                        throw new WordMcpException(
                            "expected_match_count_mismatch",
                            "$.replacements[].expected_match_count",
                            $"Expected {replacement.ExpectedMatchCount} match(es), but the target contains {matches.Count}.",
                            "Re-analyze the current artifact and submit the exact expected text and match count.");
                    }

                    if (matches.Any(match => !map.CanReplace(match)))
                    {
                        throw new WordMcpException(
                            "replacement_crosses_protected_boundary",
                            "$.replacements[].expected_text",
                            "The requested text intersects a field, revision, content control, bookmark, hyperlink, tab, or line-break boundary.",
                            "Choose a smaller target range that contains only ordinary text runs.");
                    }

                    planned.AddRange(matches.Select(match => new PlannedReplacement(
                        paragraph,
                        resolved.Part,
                        match,
                        replacement.ReplacementText,
                        replacement.TargetId)));
                }

                RejectOverlappingReplacements(planned);
                foreach (var partGroup in planned.GroupBy(plan => plan.Part, ReferenceComparer<OpenXmlPart>.Instance))
                {
                    context.MarkChanged(partGroup.Key);
                }

                foreach (var paragraphGroup in planned.GroupBy(plan => plan.Paragraph, ReferenceComparer<W.Paragraph>.Instance))
                {
                    foreach (var plan in paragraphGroup.OrderByDescending(plan => plan.Range.Start))
                    {
                        var current = WordTextMap.Create(plan.Paragraph);
                        current.Replace(plan.Range, plan.Replacement);
                    }
                }
            },
            cancellationToken);
    }

    public WordMutationResult ApplyEdits(
        WordMutationRequest request,
        IReadOnlyList<AtomicEditRequest> edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ValidateMutationRequest(request);
        if (edits.Count is < 1 or > 50)
        {
            throw InvalidInput("edit_count_out_of_range", "$.edits", "Provide between 1 and 50 atomic edits.");
        }

        if (edits.Select(edit => edit.TargetId).Distinct(StringComparer.Ordinal).Count() != edits.Count)
        {
            throw InvalidInput("duplicate_atomic_target", "$.edits", "Each target_id may occur only once in an atomic edit batch.");
        }

        return ExecuteMutation(
            request,
            (document, context) =>
            {
                EnsureDocumentIsEditable(document, "$.edits");
                var resolved = edits.Select(edit =>
                {
                    var target = GetTarget(request.Analysis, edit.TargetId, "$.edits[].target_id");
                    if (target.Restricted)
                    {
                        throw UnsupportedTarget("atomic_target_restricted", "$.edits[].target_id");
                    }

                    return new ResolvedEdit(edit, target, ResolveTarget(document, target));
                }).ToArray();

                foreach (var entry in resolved)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyAtomicEdit(document, context, entry);
                }
            },
            cancellationToken);
    }

    public WordMutationResult PopulateTemplate(
        WordTemplatePopulationRequest request,
        IReadOnlyList<TemplateFieldRequest> fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fields);
        ValidateSourceRequest(request.SourcePath, request.ExpectedSourceSha256);
        var actualSha256 = ComputeSha256(request.SourcePath);
        if (!string.Equals(actualSha256, request.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidInput("source_hash_mismatch", "$.source_file_id", "The template no longer matches its immutable snapshot.");
        }

        EnsureDigitalSignatureDoesNotBlockMutation(request.SourcePath, "$.source_file_id");

        if (fields.Count is < 1 or > 100)
        {
            throw InvalidInput("template_field_count_out_of_range", "$.fields", "Provide between 1 and 100 template fields.");
        }

        if (fields.Select(field => field.Tag).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            throw InvalidInput("duplicate_template_field", "$.fields", "Each template tag may occur only once in a populate request.");
        }

        IReadOnlyList<string> changedEntries;
        try
        {
            changedEntries = packageEditor.Edit(
                request.SourcePath,
                request.DestinationPath,
                (document, context) =>
                {
                    EnsurePopulateTemplateIsSupported(document);
                    var mainPart = document.MainDocumentPart ?? throw TargetMismatch();
                    var stories = BuildStoryContexts(mainPart);
                    var controls = stories
                        .SelectMany(story => StoryDescendants(story, IsSdt).Select(control => (story, control)))
                        .ToArray();
                    var tagGroups = controls
                        .Where(entry => !string.IsNullOrWhiteSpace(GetSdtTag(entry.control)))
                        .GroupBy(entry => GetSdtTag(entry.control)!, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

                    foreach (var field in fields)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(field.Tag) || field.Tag.Length > 128 || field.Runs.Count is < 1 or > 200)
                        {
                            throw InvalidInput("invalid_template_field", "$.fields", "Template tags and semantic runs must be bounded and non-empty.");
                        }

                        ValidateSemanticRuns(field.Runs, "$.fields[].runs");
                        if (tagGroups.TryGetValue(field.Tag, out var matches))
                        {
                            if (matches.Length != 1)
                            {
                                throw InvalidInput("duplicate_template_tag", "$.fields[].tag", "The template contains a duplicate content-control tag.");
                            }

                            PopulateControl(matches[0].control, field.Runs);
                            context.MarkChanged(matches[0].story.Part);
                            continue;
                        }

                        if (!field.BookmarkFallback)
                        {
                            throw InvalidInput("template_tag_not_found", "$.fields[].tag", "The requested content-control tag was not found.");
                        }

                        var bookmarkMatches = stories
                            .SelectMany(story => StoryDescendants<W.BookmarkStart>(story).Select(bookmark => (story, bookmark)))
                            .Where(entry => string.Equals(entry.bookmark.Name?.Value, field.Tag, StringComparison.Ordinal))
                            .ToArray();
                        if (bookmarkMatches.Length != 1 || field.Tag.StartsWith('_'))
                        {
                            throw InvalidInput("bookmark_fallback_ambiguous", "$.fields[].tag", "The bookmark fallback is absent, hidden, or not unique.");
                        }

                        PopulateBookmark(bookmarkMatches[0].bookmark, field.Runs);
                        context.MarkChanged(bookmarkMatches[0].story.Part);
                    }

                    if (document.DocumentType == WordprocessingDocumentType.Template)
                    {
                        context.RequestTemplateToDocumentConversion();
                    }
                },
                cancellationToken);

            var validation = validationGate.ValidateExistingEdit(request.SourcePath, request.DestinationPath, cancellationToken);
            var outputSha256 = ComputeSha256(request.DestinationPath);
            var outputAnalysis = Analyze(
                new WordAnalysisRequest(
                    request.DestinationPath,
                    Path.ChangeExtension(request.SourceFileName, ".docx"),
                    outputSha256,
                    request.UserScope,
                    request.ConversationScope),
                cancellationToken);
            return new WordMutationResult(
                outputSha256,
                outputAnalysis,
                validation,
                changedEntries.Select(ToPartUri).ToArray());
        }
        catch
        {
            DeleteFailedOutput(request.DestinationPath);
            throw;
        }
    }

    private WordMutationResult ExecuteMutation(
        WordMutationRequest request,
        Action<WordprocessingDocument, WordPackageMutationContext> mutation,
        CancellationToken cancellationToken)
    {
        EnsureDigitalSignatureDoesNotBlockMutation(request.SourcePath, "$.analysis_id");
        try
        {
            var changedEntries = packageEditor.Edit(
                request.SourcePath,
                request.DestinationPath,
                mutation,
                cancellationToken);
            var validation = validationGate.ValidateExistingEdit(request.SourcePath, request.DestinationPath, cancellationToken);
            var outputSha256 = ComputeSha256(request.DestinationPath);
            var outputAnalysis = Analyze(
                new WordAnalysisRequest(
                    request.DestinationPath,
                    Path.ChangeExtension(request.Analysis.SourceFileName, ".docx"),
                    outputSha256,
                    request.Analysis.UserScope,
                    request.Analysis.ConversationScope),
                cancellationToken);
            return new WordMutationResult(
                outputSha256,
                outputAnalysis,
                validation,
                changedEntries.Select(ToPartUri).ToArray());
        }
        catch
        {
            DeleteFailedOutput(request.DestinationPath);
            throw;
        }
    }

    private static void ApplyAtomicEdit(
        WordprocessingDocument document,
        WordPackageMutationContext context,
        ResolvedEdit entry)
    {
        var edit = entry.Edit;
        var resolved = entry.Resolved;
        switch (edit.Operation)
        {
            case WordEditOperations.ReplaceBlock:
                RequirePayload(edit, runs: true, blocks: false, cells: false);
                if (resolved.Element is not W.Paragraph paragraph || entry.Target.Kind is not ("paragraph" or "heading"))
                {
                    throw UnsupportedTarget("replace_block_target_unsupported", "$.edits[].target_id");
                }

                EnsureSimpleEditableElement(paragraph, allowDrawing: false);
                ReplaceParagraphContent(paragraph, edit.Runs!);
                context.MarkChanged(resolved.Part);
                break;

            case WordEditOperations.InsertBefore:
            case WordEditOperations.InsertAfter:
                RequirePayload(edit, runs: false, blocks: true, cells: false);
                EnsureMainStoryBodyTarget(entry.Target, resolved.Element);
                var blocks = WordOpenXmlFactory.CreateBlocksForExistingDocument(
                    document,
                    edit.Blocks!,
                    context,
                    resolved.Element).ToArray();
                if (edit.Operation == WordEditOperations.InsertBefore)
                {
                    foreach (var block in blocks)
                    {
                        resolved.Element.InsertBeforeSelf(block);
                    }
                }
                else
                {
                    var anchor = resolved.Element;
                    foreach (var block in blocks)
                    {
                        anchor.InsertAfterSelf(block);
                        anchor = block;
                    }
                }

                context.MarkChanged(resolved.Part);
                break;

            case WordEditOperations.DeleteBlock:
                RequirePayload(edit, runs: false, blocks: false, cells: false);
                EnsureMainStoryBodyTarget(entry.Target, resolved.Element);
                EnsureSimpleEditableElement(resolved.Element, allowDrawing: false);
                if (resolved.Element.Descendants<W.SectionProperties>().Any())
                {
                    throw UnsupportedTarget("section_boundary_delete_unsupported", "$.edits[].target_id");
                }

                resolved.Element.Remove();
                context.MarkChanged(resolved.Part);
                break;

            case WordEditOperations.ReplaceCell:
                RequirePayload(edit, runs: true, blocks: false, cells: false);
                if (resolved.Element is not W.TableCell cell || entry.Target.Kind != "cell")
                {
                    throw UnsupportedTarget("replace_cell_target_unsupported", "$.edits[].target_id");
                }

                EnsureSimpleCell(cell);
                ReplaceCellContent(cell, edit.Runs!);
                context.MarkChanged(resolved.Part);
                break;

            case WordEditOperations.AppendTableRow:
                RequirePayload(edit, runs: false, blocks: false, cells: true);
                if (resolved.Element is not W.Table table || entry.Target.Kind != "table")
                {
                    throw UnsupportedTarget("append_row_target_unsupported", "$.edits[].target_id");
                }

                EnsureSimpleTable(table);
                AppendTableRow(table, edit.Cells!);
                context.MarkChanged(resolved.Part);
                break;

            default:
                throw InvalidInput(
                    "unsupported_atomic_operation",
                    "$.edits[].operation",
                    "The requested atomic edit operation is not supported.");
        }
    }

    private static void ReplaceParagraphContent(W.Paragraph paragraph, IReadOnlyList<SemanticRun> runs)
    {
        ValidateSemanticRuns(runs, "$.edits[].runs");
        foreach (var child in paragraph.ChildElements.Where(child => child is not W.ParagraphProperties).ToArray())
        {
            child.Remove();
        }

        paragraph.Append(runs.Select(WordOpenXmlFactory.CreateSemanticRun));
    }

    private static void ReplaceCellContent(W.TableCell cell, IReadOnlyList<SemanticRun> runs)
    {
        ValidateSemanticRuns(runs, "$.edits[].runs");
        foreach (var child in cell.ChildElements.Where(child => child is not W.TableCellProperties).ToArray())
        {
            child.Remove();
        }

        cell.Append(new W.Paragraph(runs.Select(WordOpenXmlFactory.CreateSemanticRun)));
    }

    private static void AppendTableRow(W.Table table, IReadOnlyList<string> values)
    {
        var expectedColumns = table.TableGrid?.Elements<W.GridColumn>().Count()
                              ?? table.Elements<W.TableRow>().FirstOrDefault()?.Elements<W.TableCell>().Count()
                              ?? 0;
        if (expectedColumns == 0 || values.Count != expectedColumns)
        {
            throw InvalidInput("table_row_width_mismatch", "$.edits[].cells", "The appended row must match the table column count.");
        }

        foreach (var value in values)
        {
            EnsurePlainText(value, "$.edits[].cells[]");
        }

        table.Append(new W.TableRow(values.Select(value => WordOpenXmlFactory.CreateTableCell(value, widthTwips: null))));
    }

    private static void PopulateControl(OpenXmlElement control, IReadOnlyList<SemanticRun> runs)
    {
        var properties = control.ChildElements.FirstOrDefault(element => element.LocalName == "sdtPr")
                         ?? throw UnsupportedTarget("content_control_properties_missing", "$.fields[].tag");
        if (properties.Descendants<W.Lock>().Any()
            || properties.Descendants<W.DataBinding>().Any()
            || properties.Descendants().Any(element => element.LocalName is "repeatingSection" or "repeatingSectionItem")
            || control.Descendants().Any(element => IsSdt(element)))
        {
            throw UnsupportedTarget("complex_content_control", "$.fields[].tag");
        }

        EnsureSimpleEditableElement(control, allowDrawing: false, ignoreContentControlRoot: true);
        var content = control.ChildElements.FirstOrDefault(element => element.LocalName.StartsWith("sdtContent", StringComparison.Ordinal))
                      ?? throw UnsupportedTarget("content_control_content_missing", "$.fields[].tag");
        switch (control.GetType().Name)
        {
            case "SdtRun":
                content.RemoveAllChildren();
                content.Append(runs.Select(WordOpenXmlFactory.CreateSemanticRun));
                break;
            case "SdtBlock":
                content.RemoveAllChildren();
                content.Append(new W.Paragraph(runs.Select(WordOpenXmlFactory.CreateSemanticRun)));
                break;
            case "SdtCell":
                var existingCell = content.Elements<W.TableCell>().SingleOrDefault()
                                   ?? throw UnsupportedTarget("content_control_cell_invalid", "$.fields[].tag");
                EnsureSimpleCell(existingCell);
                ReplaceCellContent(existingCell, runs);
                break;
            default:
                throw UnsupportedTarget("content_control_kind_unsupported", "$.fields[].tag");
        }
    }

    private static void PopulateBookmark(W.BookmarkStart start, IReadOnlyList<SemanticRun> runs)
    {
        if (start.Parent is not W.Paragraph paragraph || string.IsNullOrWhiteSpace(start.Id?.Value))
        {
            throw UnsupportedTarget("bookmark_fallback_complex", "$.fields[].tag");
        }

        var siblings = paragraph.ChildElements.ToArray();
        var startIndex = Array.IndexOf(siblings, start);
        var endIndex = Array.FindIndex(
            siblings,
            startIndex + 1,
            element => element is W.BookmarkEnd end && string.Equals(end.Id?.Value, start.Id.Value, StringComparison.Ordinal));
        if (startIndex < 0 || endIndex <= startIndex)
        {
            throw UnsupportedTarget("bookmark_fallback_unbalanced", "$.fields[].tag");
        }

        var between = siblings[(startIndex + 1)..endIndex];
        if (between.Any(element => HasProtectedBoundary(element, allowDrawing: false)))
        {
            throw UnsupportedTarget("bookmark_fallback_complex", "$.fields[].tag");
        }

        foreach (var element in between)
        {
            element.Remove();
        }

        foreach (var run in runs.Select(WordOpenXmlFactory.CreateSemanticRun).Reverse())
        {
            start.InsertAfterSelf(run);
        }
    }

    private static void EnsurePopulateTemplateIsSupported(WordprocessingDocument document)
    {
        EnsureDocumentIsEditable(document, "$.fields");
        var main = document.MainDocumentPart ?? throw TargetMismatch();
        var roots = BuildStoryContexts(main).Select(story => story.Root).Distinct(ReferenceComparer<OpenXmlElement>.Instance).ToArray();
        var parts = DescendantParts(main).ToArray();
        var unsupported = main.WordprocessingCommentsPart is not null
                          || main.FootnotesPart is not null
                          || main.EndnotesPart is not null
                          || parts.Any(part => part is CustomXmlPart
                                               || part.GetType().Name.Contains("Chart", StringComparison.Ordinal)
                                               || part.GetType().Name.Contains("Diagram", StringComparison.Ordinal))
                          || roots.Any(root => root.Descendants().Any(IsUnsupportedPopulateElement))
                          || roots.Any(root => root.Descendants().Where(IsSdt).Any(IsUnsupportedPopulateControl));
        if (unsupported)
        {
            throw UnsupportedTarget("template_contains_unsupported_passive_content", "$.source_file_id");
        }
    }

    private static bool IsUnsupportedPopulateElement(OpenXmlElement element) =>
        IsRevision(element)
        || element.NamespaceUri == "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
        && element.LocalName is "vanish" or "webHidden" or "customXml" or "txbxContent"
            or "commentRangeStart" or "commentRangeEnd" or "commentReference"
        || element.LocalName is "oMath" or "oMathPara";

    private static bool IsUnsupportedPopulateControl(OpenXmlElement control)
    {
        var properties = control.ChildElements.FirstOrDefault(element => element.LocalName == "sdtPr");
        return properties is null
               || properties.Descendants<W.Lock>().Any()
               || properties.Descendants<W.DataBinding>().Any()
               || properties.Descendants().Any(element => element.LocalName is "repeatingSection" or "repeatingSectionItem")
               || control.Descendants().Any(IsSdt);
    }

    private static void EnsureDocumentIsEditable(WordprocessingDocument document, string fieldPath)
    {
        if (document.MainDocumentPart?.DocumentSettingsPart?.Settings?.GetFirstChild<W.DocumentProtection>() is not null)
        {
            throw new WordMcpException(
                "document_protected",
                fieldPath,
                "The document is protected and cannot be changed by this workflow.",
                "Remove protection in the authoring application and upload a new macro-free copy.");
        }
    }

    private static void EnsureMainStoryBodyTarget(TargetRecord target, OpenXmlElement element)
    {
        if (target.Story != "main" || element.Parent is not W.Body || target.Kind is not ("paragraph" or "heading" or "table"))
        {
            throw UnsupportedTarget("main_story_block_required", "$.edits[].target_id");
        }
    }

    private static void EnsureSimpleEditableElement(
        OpenXmlElement element,
        bool allowDrawing,
        bool ignoreContentControlRoot = false)
    {
        if (HasProtectedBoundary(element, allowDrawing, ignoreContentControlRoot))
        {
            throw UnsupportedTarget("target_contains_protected_boundary", "$.edits[].target_id");
        }
    }

    private static bool HasProtectedBoundary(
        OpenXmlElement element,
        bool allowDrawing,
        bool ignoreContentControlRoot = false)
    {
        var protectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "fldChar", "instrText", "fldSimple", "ins", "del", "moveFrom", "moveTo",
            "moveFromRangeStart", "moveFromRangeEnd", "moveToRangeStart", "moveToRangeEnd",
            "bookmarkStart", "bookmarkEnd", "commentRangeStart", "commentRangeEnd", "commentReference",
            "footnoteReference", "endnoteReference", "footnoteRef", "endnoteRef", "permStart", "permEnd",
            "tab", "br", "cr", "hyperlink", "customXml", "smartTag", "altChunk", "object",
            "vanish", "webHidden", "txbxContent", "oMath", "oMathPara",
        };
        if (!allowDrawing)
        {
            protectedNames.Add("drawing");
            protectedNames.Add("pict");
        }

        if (!ignoreContentControlRoot)
        {
            protectedNames.Add("sdt");
        }

        return element.Descendants().Any(descendant => protectedNames.Contains(descendant.LocalName));
    }

    private static void EnsureSimpleCell(W.TableCell cell)
    {
        if ((cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1) > 1
            || cell.TableCellProperties?.VerticalMerge is not null
            || cell.TableCellProperties?.HorizontalMerge is not null)
        {
            throw UnsupportedTarget("merged_cell_unsupported", "$.edits[].target_id");
        }

        EnsureSimpleEditableElement(cell, allowDrawing: false);
    }

    private static void EnsureSimpleTable(W.Table table)
    {
        foreach (var cell in table.Descendants<W.TableCell>())
        {
            EnsureSimpleCell(cell);
        }
    }

    private static void RequirePayload(AtomicEditRequest edit, bool runs, bool blocks, bool cells)
    {
        if ((edit.Runs is not null) != runs || (edit.Blocks is not null) != blocks || (edit.Cells is not null) != cells)
        {
            throw InvalidInput(
                "invalid_atomic_edit_payload",
                "$.edits",
                "The atomic operation received missing or mutually exclusive semantic payloads.");
        }

        if ((runs && edit.Runs!.Count == 0) || (blocks && edit.Blocks!.Count == 0) || (cells && edit.Cells!.Count == 0))
        {
            throw InvalidInput("empty_atomic_edit_payload", "$.edits", "The atomic edit payload must not be empty.");
        }
    }

    private static void ValidateSemanticRuns(IReadOnlyList<SemanticRun> runs, string fieldPath)
    {
        if (runs.Count is < 1 or > 200)
        {
            throw InvalidInput("semantic_run_count_out_of_range", fieldPath, "Semantic runs must contain between 1 and 200 entries.");
        }

        foreach (var run in runs)
        {
            if (run.Text.Length is < 1 or > 5_000)
            {
                throw InvalidInput("semantic_run_text_out_of_range", fieldPath, "Each semantic run must contain 1 to 5000 characters.");
            }

            EnsurePlainText(run.Text, fieldPath);
        }
    }

    private static void EnsurePlainText(string value, string fieldPath)
    {
        if (value.Any(character => char.IsControl(character) && character is not '\u200C' and not '\u200D'))
        {
            throw InvalidInput("plain_text_control_character", fieldPath, "Plain text must not contain control characters.");
        }
    }

    private static void RejectOverlappingReplacements(IReadOnlyList<PlannedReplacement> plans)
    {
        foreach (var group in plans.GroupBy(plan => plan.Paragraph, ReferenceComparer<W.Paragraph>.Instance))
        {
            var ordered = group.OrderBy(plan => plan.Range.Start).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].Range.Start < ordered[index - 1].Range.End)
                {
                    throw InvalidInput(
                        "overlapping_replacements",
                        "$.replacements",
                        "Multiple replacements overlap in the same paragraph target.");
                }
            }
        }
    }

    private static TargetRecord GetTarget(AnalysisSnapshot analysis, string targetId, string fieldPath)
    {
        if (!analysis.Targets.TryGetValue(targetId, out var target))
        {
            throw new WordMcpException(
                "target_not_found",
                fieldPath,
                "The target does not belong to this analysis snapshot.",
                "Use an exact target_id from word_get_analysis_chunk for the current analysis_id.");
        }

        return target;
    }

    private static ResolvedTarget ResolveTarget(WordprocessingDocument document, TargetRecord target)
    {
        var main = document.MainDocumentPart ?? throw TargetMismatch();
        var story = BuildStoryContexts(main).SingleOrDefault(candidate =>
            string.Equals(candidate.Name, target.Story, StringComparison.Ordinal)
            && string.Equals(candidate.PartUri, target.PartUri, StringComparison.Ordinal));
        if (story is null)
        {
            throw TargetMismatch();
        }

        OpenXmlElement? element = target.Kind switch
        {
            "paragraph" or "heading" => StoryDescendants<W.Paragraph>(story).ElementAtOrDefault(target.Ordinal),
            "table" => StoryDescendants<W.Table>(story).ElementAtOrDefault(target.Ordinal),
            "cell" => ResolveCell(story, target),
            "content_control" => StoryDescendants(story, IsSdt).ElementAtOrDefault(target.Ordinal),
            "bookmark" => StoryDescendants<W.BookmarkStart>(story).ElementAtOrDefault(target.Ordinal),
            _ => null,
        };
        if (element is null || !string.Equals(Snippet(WordTextMap.VisibleText(element)), target.Snippet, StringComparison.Ordinal))
        {
            throw TargetMismatch();
        }

        return new ResolvedTarget(story.Part, element);
    }

    private static W.TableCell? ResolveCell(StoryContext story, TargetRecord target)
    {
        if (target.ParentOrdinal is null || target.RowIndex is null || target.ColumnIndex is null)
        {
            return null;
        }

        var table = StoryDescendants<W.Table>(story).ElementAtOrDefault(target.ParentOrdinal.Value);
        var row = table?.Elements<W.TableRow>().ElementAtOrDefault(target.RowIndex.Value);
        return row?.Elements<W.TableCell>().ElementAtOrDefault(target.ColumnIndex.Value);
    }

    private static void ValidateMutationRequest(WordMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Analysis);
        ValidateSourceRequest(request.SourcePath, request.Analysis.SourceSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if (request.Analysis.InvalidatedAt is not null)
        {
            throw InvalidInput("stale_analysis", "$.analysis_id", "The analysis snapshot was invalidated by a previous successful edit.");
        }

        var actualSha256 = ComputeSha256(request.SourcePath);
        if (!string.Equals(actualSha256, request.Analysis.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidInput("stale_source", "$.analysis_id", "The source SHA-256 no longer matches the analysis snapshot.");
        }
    }

    private static string ToPartUri(string entryName) => entryName == "[Content_Types].xml" ? entryName : string.Concat('/', entryName);

    private static void DeleteFailedOutput(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static WordMcpException InvalidInput(string code, string fieldPath, string message) => new(
        code,
        fieldPath,
        message,
        "Re-analyze the current document and submit only the supported bounded semantic operation.");

    private static void EnsureDigitalSignatureDoesNotBlockMutation(string sourcePath, string fieldPath)
    {
        if (!OoxmlDigitalSignaturePolicy.IsPresent(sourcePath))
        {
            return;
        }

        throw new WordMcpException(
            "digital_signature_editing_unsupported",
            fieldPath,
            "Digitally signed Word documents cannot be changed because editing would invalidate the signature.",
            "Create an unsigned copy in Microsoft Word, upload it as a new source, and analyze it again before editing.");
    }

    private static WordMcpException UnsupportedTarget(string code, string fieldPath) => new(
        code,
        fieldPath,
        "The selected target contains or belongs to an unsupported Word structure.",
        "Choose an editable target reported by the current analysis snapshot.");

    private static WordMcpException TargetMismatch() => new(
        "target_locator_mismatch",
        "$.target_id",
        "The opaque target locator no longer matches the immutable source document.",
        "Run word_analyze again and use a target from the new snapshot.");

    private sealed record ResolvedTarget(OpenXmlPart Part, OpenXmlElement Element);

    private sealed record PlannedReplacement(
        W.Paragraph Paragraph,
        OpenXmlPart Part,
        TextRange Range,
        string Replacement,
        string TargetId);

    private sealed record ResolvedEdit(AtomicEditRequest Edit, TargetRecord Target, ResolvedTarget Resolved);
}
