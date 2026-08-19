using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Word;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Tests;

public sealed class OpenXmlWordDocumentEngineTests
{
    [Fact]
    public void AnalyzeEnumeratesWordStoriesAndBindsOpaqueTargetsToSourceHash()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var sha256 = TestDocumentFactory.Sha256(source);
        var engine = CreateEngine();

        var analysis = engine.Analyze(AnalysisRequest(source, sha256), TestContext.Current.CancellationToken);

        Assert.Equal(sha256, analysis.SourceSha256);
        Assert.Equal(sha256, analysis.Summary.SourceSha256);
        Assert.Equal(["comment", "endnote", "footer", "footnote", "header", "main"], analysis.Summary.Stories);
        Assert.Contains("comments", analysis.Summary.Features);
        Assert.Contains("footnotes", analysis.Summary.Features);
        Assert.Contains("endnotes", analysis.Summary.Features);
        Assert.Contains("fields", analysis.Summary.Features);
        Assert.Contains("comments_editing", analysis.Summary.UnsupportedFeatures);
        Assert.All(analysis.Targets, pair => Assert.StartsWith("tgt_", pair.Key, StringComparison.Ordinal));
        Assert.Contains(analysis.Targets.Values, target => target.Story == "header" && !target.Restricted);
        Assert.Contains(analysis.Targets.Values, target => target.Story == "footer" && target.Restricted);
        Assert.Contains(analysis.Targets.Values, target => target.Story == "footnote" && target.Restricted);
        Assert.Contains(analysis.Targets.Values, target => target.Story == "endnote" && target.Restricted);
        Assert.Contains(analysis.Targets.Values, target => target.Story == "comment" && target.Restricted);
        Assert.Contains(analysis.Targets.Values, target => target.Snippet.Contains("Commented", StringComparison.Ordinal) && target.Restricted);
        Assert.Contains(analysis.Items["tables"], item => item.Story == "main");
        Assert.Contains(analysis.Items["cells"], item => item.Story == "main");
        Assert.Contains(analysis.Items["blocks"], item =>
            item.Data.TryGetValue("text", out var text)
            && string.Equals(text as string, "BeforeAfter", StringComparison.Ordinal)
            && item.Data.TryGetValue("editable", out var editable)
            && editable is false);
    }

    [Fact]
    public void AnalyzeBoundsTextExcerptsAndReportsTruncation()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var longText = new string('長', 2_500);
        using (var document = WordprocessingDocument.Open(source, true))
        {
            var body = document.MainDocumentPart!.Document!.Body!;
            body.InsertBefore(
                new W.Paragraph(new W.Run(new W.Text(longText))),
                body.Elements<W.SectionProperties>().Last());
            document.MainDocumentPart.Document.Save();
        }

        var analysis = CreateEngine().Analyze(
            AnalysisRequest(source, TestDocumentFactory.Sha256(source)),
            TestContext.Current.CancellationToken);
        var item = analysis.Items["blocks"].Single(candidate =>
            candidate.Data.TryGetValue("snippet_truncated", out var value) && value is true);

        Assert.Equal(2_000, Assert.IsType<string>(item.Data["text"]).Length);
        Assert.EndsWith("…", Assert.IsType<string>(item.Data["text"]), StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeRejectsAChangedImmutableSource()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var expected = new string('0', 64);

        var exception = Assert.Throws<WordMcpException>(() => CreateEngine().Analyze(
            AnalysisRequest(source, expected),
            TestContext.Current.CancellationToken));

        Assert.Equal("source_hash_mismatch", exception.Code);
    }

    [Fact]
    public void ReplaceTextSpansRunsPreservesFirstRunFormattingAndUntouchedPartPayloads()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var destination = files.OutputPath("replaced.docx");
        var headerBefore = TestDocumentFactory.ReadEntryPayload(source, "word/header1.xml");
        var engine = CreateEngine();
        var analysis = engine.Analyze(
            AnalysisRequest(source, TestDocumentFactory.Sha256(source)),
            TestContext.Current.CancellationToken);
        var target = Target(analysis, "heading", "Alpha Beta");

        var result = engine.ReplaceText(
            new WordMutationRequest(source, destination, analysis),
            [new TextReplacementRequest(target.TargetId, "Alpha Beta", "Gamma Value")],
            TestContext.Current.CancellationToken);

        Assert.True(result.Validation.IsAccepted);
        Assert.Equal(["/word/document.xml"], result.ChangedPartUris);
        Assert.Equal(headerBefore, TestDocumentFactory.ReadEntryPayload(destination, "word/header1.xml"));
        using var document = WordprocessingDocument.Open(destination, false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing output main part.");
        var body = main.Document?.Body ?? throw new InvalidDataException("Missing output body.");
        var paragraph = body.Elements<W.Paragraph>().First();
        var runs = paragraph.Elements<W.Run>().ToArray();
        Assert.Equal("Gamma Value", runs[0].InnerText);
        Assert.Null(runs[0].RunProperties?.Bold);
        Assert.Equal(string.Empty, runs[1].InnerText);
        Assert.NotNull(runs[1].RunProperties?.Bold);
        Assert.Equal("Gamma Value", result.OutputAnalysis.Targets.Values.Single(value => value.Kind == "heading").Snippet);
    }

    [Fact]
    public void ReplaceTextRejectsRestrictedBookmarkTargetAndLeavesNoOutput()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var destination = files.OutputPath("boundary-rejected.docx");
        var engine = CreateEngine();
        var analysis = engine.Analyze(
            AnalysisRequest(source, TestDocumentFactory.Sha256(source)),
            TestContext.Current.CancellationToken);
        var target = Target(analysis, "paragraph", "BeforeAfter");

        var exception = Assert.Throws<WordMcpException>(() => engine.ReplaceText(
            new WordMutationRequest(source, destination, analysis),
            [new TextReplacementRequest(target.TargetId, "BeforeAfter", "unsafe")],
            TestContext.Current.CancellationToken));

        Assert.Equal("text_replacement_target_unsupported", exception.Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ApplyEditsCommitsTheWholeSupportedBatchAtomically()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var destination = files.OutputPath("atomic.docx");
        var engine = CreateEngine();
        var analysis = engine.Analyze(
            AnalysisRequest(source, TestDocumentFactory.Sha256(source)),
            TestContext.Current.CancellationToken);
        var paragraph = Target(analysis, "paragraph", "Second paragraph");
        var insertAnchor = Target(analysis, "heading", "Alpha Beta");
        var cell = Target(analysis, "cell", "One");
        var table = analysis.Targets.Values.Single(value => value.Kind == "table" && value.Story == "main");

        var result = engine.ApplyEdits(
            new WordMutationRequest(source, destination, analysis),
            [
                new AtomicEditRequest(
                    WordEditOperations.ReplaceBlock,
                    paragraph.TargetId,
                    Runs: [new SemanticRun("Atomic replacement", Bold: true)]),
                new AtomicEditRequest(
                    WordEditOperations.InsertBefore,
                    insertAnchor.TargetId,
                    Blocks: [new DocumentBlock(DocumentBlockKind.Paragraph, Text: "Inserted block")]),
                new AtomicEditRequest(
                    WordEditOperations.ReplaceCell,
                    cell.TargetId,
                    Runs: [new SemanticRun("Updated cell")]),
                new AtomicEditRequest(
                    WordEditOperations.AppendTableRow,
                    table.TargetId,
                    Cells: ["Three", "Four"]),
            ],
            TestContext.Current.CancellationToken);

        Assert.True(result.Validation.IsAccepted);
        using var document = WordprocessingDocument.Open(destination, false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing output main part.");
        var body = main.Document?.Body ?? throw new InvalidDataException("Missing output body.");
        Assert.Contains(body.Elements<W.Paragraph>(), value => value.InnerText == "Atomic replacement");
        Assert.Contains(body.Elements<W.Paragraph>(), value => value.InnerText == "Inserted block");
        var outputTable = body.Elements<W.Table>().Single();
        Assert.Equal(3, outputTable.Elements<W.TableRow>().Count());
        Assert.Equal("Updated cell", outputTable.Descendants<W.TableCell>().ElementAt(2).InnerText);
        Assert.Equal(["Three", "Four"], outputTable.Elements<W.TableRow>().Last().Elements<W.TableCell>().Select(value => value.InnerText));
    }

    [Fact]
    public void ApplyEditsRemovesCandidateWhenAnyEditFails()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var destination = files.OutputPath("atomic-failed.docx");
        var engine = CreateEngine();
        var analysis = engine.Analyze(
            AnalysisRequest(source, TestDocumentFactory.Sha256(source)),
            TestContext.Current.CancellationToken);
        var paragraph = Target(analysis, "paragraph", "Second paragraph");
        var table = analysis.Targets.Values.Single(value => value.Kind == "table" && value.Story == "main");

        var exception = Assert.Throws<WordMcpException>(() => engine.ApplyEdits(
            new WordMutationRequest(source, destination, analysis),
            [
                new AtomicEditRequest(
                    WordEditOperations.ReplaceBlock,
                    paragraph.TargetId,
                    Runs: [new SemanticRun("Must not be committed")]),
                new AtomicEditRequest(WordEditOperations.AppendTableRow, table.TargetId, Cells: ["wrong width"]),
            ],
            TestContext.Current.CancellationToken));

        Assert.Equal("table_row_width_mismatch", exception.Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void PopulateTemplateUsesSimpleSdtTagsBookmarkFallbackAndConvertsDotx()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreatePopulateTemplate();
        var destination = files.OutputPath("populated.docx");
        var engine = CreateEngine();

        var result = engine.PopulateTemplate(
            new WordTemplatePopulationRequest(
                source,
                destination,
                "template.dotx",
                TestDocumentFactory.Sha256(source),
                "usr_test",
                "cnv_test"),
            [
                new TemplateFieldRequest("CustomerName", [new SemanticRun("株式会社サンプル", Bold: true)]),
                new TemplateFieldRequest("Summary", [new SemanticRun("合成サマリー")]),
                new TemplateFieldRequest("Status", [new SemanticRun("完了")]),
                new TemplateFieldRequest("LegacyField", [new SemanticRun("互換入力")], BookmarkFallback: true),
            ],
            TestContext.Current.CancellationToken);

        Assert.True(result.Validation.IsAccepted);
        Assert.Contains("[Content_Types].xml", result.ChangedPartUris);
        using var document = WordprocessingDocument.Open(destination, false);
        Assert.Equal(WordprocessingDocumentType.Document, document.DocumentType);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing output main part.");
        var root = main.Document ?? throw new InvalidDataException("Missing output document root.");
        Assert.Equal("株式会社サンプル", root.Descendants<W.SdtRun>().Single().InnerText);
        Assert.Equal("合成サマリー", root.Descendants<W.SdtBlock>().Single().InnerText);
        Assert.Equal("完了", root.Descendants<W.SdtCell>().Single().InnerText);
        var bookmark = root.Descendants<W.BookmarkStart>().Single(value => value.Name?.Value == "LegacyField");
        Assert.Equal("互換入力", bookmark.NextSibling<W.Run>()?.InnerText);
    }

    [Theory]
    [InlineData("footnote")]
    [InlineData("data_bound_control")]
    public void PopulateTemplateRejectsUnsupportedPassiveContentAnywhereInTemplate(string feature)
    {
        using var files = new TestDocumentFactory();
        var source = files.CreatePopulateTemplate();
        using (var document = WordprocessingDocument.Open(source, true))
        {
            var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing template main part.");
            if (feature == "footnote")
            {
                var footnotes = main.AddNewPart<FootnotesPart>();
                footnotes.Footnotes = new W.Footnotes(
                    new W.Footnote(new W.Paragraph(new W.Run(new W.Text("Synthetic footnote"))))
                    {
                        Id = 1,
                    });
                footnotes.Footnotes.Save();
            }
            else
            {
                var control = main.Document?.Descendants<W.SdtRun>().Single()
                              ?? throw new InvalidDataException("Missing template content control.");
                control.SdtProperties?.Append(new W.DataBinding
                {
                    XPath = "/root/value",
                    StoreItemId = "{00000000-0000-0000-0000-000000000001}",
                });
                main.Document?.Save();
            }
        }

        var destination = files.OutputPath("unsupported-populate.docx");
        var error = Assert.Throws<WordMcpException>(() => CreateEngine().PopulateTemplate(
            new WordTemplatePopulationRequest(
                source,
                destination,
                "template.dotx",
                TestDocumentFactory.Sha256(source),
                "usr_test",
                "cnv_test"),
            [new TemplateFieldRequest("Summary", [new SemanticRun("must not be written")])],
            TestContext.Current.CancellationToken));

        Assert.Equal("template_contains_unsupported_passive_content", error.Code);
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("duplicate", "duplicate_template_tag")]
    [InlineData("locked", "template_contains_unsupported_passive_content")]
    [InlineData("nested", "template_contains_unsupported_passive_content")]
    [InlineData("repeating", "template_contains_unsupported_passive_content")]
    public void PopulateTemplateRejectsAmbiguousOrComplexContentControls(
        string feature,
        string expectedCode)
    {
        using var files = new TestDocumentFactory();
        var source = files.CreatePopulateTemplate();
        using (var document = WordprocessingDocument.Open(source, true))
        {
            var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing template main part.");
            var control = main.Document?.Descendants<W.SdtRun>().First()
                          ?? throw new InvalidDataException("Missing template content control.");
            switch (feature)
            {
                case "duplicate":
                    control.InsertAfterSelf(control.CloneNode(deep: true));
                    break;
                case "locked":
                    control.SdtProperties?.Append(new W.Lock());
                    break;
                case "nested":
                    control.SdtContentRun?.Append(new W.SdtRun(
                        new W.SdtProperties(new W.Tag { Val = "Nested" }),
                        new W.SdtContentRun(new W.Run(new W.Text("nested placeholder")))));
                    break;
                case "repeating":
                    var properties = control.SdtProperties
                                     ?? throw new InvalidDataException("Missing content-control properties.");
                    properties.InnerXml +=
                        "<w15:repeatingSection xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\"/>";
                    break;
                default:
                    throw new InvalidOperationException("Unknown synthetic content-control feature.");
            }

            main.Document?.Save();
        }

        var destination = files.OutputPath($"unsupported-{feature}.docx");
        var error = Assert.Throws<WordMcpException>(() => CreateEngine().PopulateTemplate(
            new WordTemplatePopulationRequest(
                source,
                destination,
                "template.dotx",
                TestDocumentFactory.Sha256(source),
                "usr_test",
                "cnv_test"),
            [new TemplateFieldRequest("CustomerName", [new SemanticRun("must not be written")])],
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, error.Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void GenerateCreatesEditableJapaneseStructuresAndEmbeddedOpaqueImage()
    {
        using var files = new TestDocumentFactory();
        var destination = files.OutputPath("generated.docx");
        var imageBytes = DocxTestPackage.Png(320, 200);
        var image = new WordImageAsset(
            "img_fixture",
            imageBytes,
            "image/png",
            Convert.ToHexStringLower(SHA256.HashData(imageBytes)));

        var result = CreateEngine().Generate(new WordGenerationRequest(
            destination,
            TestDocumentFactory.JapaneseReportDefinition(includeImage: true),
            "usr_test",
            "cnv_test",
            Images: new Dictionary<string, WordImageAsset>(StringComparer.Ordinal)
            {
                [image.FileId] = image,
            }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Validation.IsAccepted);
        Assert.Empty(result.Validation.CandidateErrors);
        using var document = WordprocessingDocument.Open(destination, false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing generated main part.");
        var root = main.Document ?? throw new InvalidDataException("Missing generated document root.");
        Assert.Contains(root.Descendants<W.ParagraphStyleId>(), value => value.Val?.Value == "Heading1");
        Assert.Contains(root.Descendants<W.NumberingProperties>(), value => value.NumberingId?.Val?.Value > 0);
        Assert.Contains(root.Descendants<W.Table>(), _ => true);
        Assert.Contains(root.Descendants<W.FieldCode>(), value => value.Text.Contains("TOC", StringComparison.Ordinal));
        Assert.Empty(main.DocumentSettingsPart?.Settings?.Elements<W.UpdateFieldsOnOpen>() ?? []);
        Assert.Contains(main.FooterParts.SelectMany(part => part.Footer!.Descendants<W.FieldCode>()), value => value.Text.Contains("PAGE", StringComparison.Ordinal));
        Assert.Contains(main.FooterParts.SelectMany(part => part.Footer!.Descendants<W.FieldCode>()), value => value.Text.Contains("NUMPAGES", StringComparison.Ordinal));
        Assert.Single(main.ImageParts);
        Assert.Contains(root.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>(), value => value.Description?.Value == "合成検証画像");
        Assert.All(root.Descendants<W.RunFonts>(), fonts => Assert.False(string.IsNullOrWhiteSpace(fonts.EastAsia?.Value)));
        Assert.Empty(main.ExternalRelationships);
        var analyzedImage = Assert.Single(result.OutputAnalysis.Items["images"]);
        Assert.Equal(true, analyzedImage.Data["alt_text_present"]);
        Assert.Equal("合成検証画像", analyzedImage.Data["alt_text"]);
        Assert.True(Convert.ToInt64(analyzedImage.Data["width_emu"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        var analyzedSection = Assert.Single(result.OutputAnalysis.Items["sections"]);
        Assert.NotNull(analyzedSection.Data["default_header_part_uri"]);
        Assert.NotNull(analyzedSection.Data["default_footer_part_uri"]);
    }

    [Fact]
    public void GenerateConstrainsTablesAndImagesToTheUsableColumnWidth()
    {
        using var files = new TestDocumentFactory();
        var destination = files.OutputPath("narrow-layout.docx");
        var imageBytes = DocxTestPackage.Png(1_200, 800);
        var image = new WordImageAsset(
            "img_fixture",
            imageBytes,
            "image/png",
            Convert.ToHexStringLower(SHA256.HashData(imageBytes)));
        var source = TestDocumentFactory.JapaneseReportDefinition(includeImage: true);
        var definition = source with
        {
            Layout = source.Layout with
            {
                MarginLeftMm = 50,
                MarginRightMm = 50,
                Columns = 3,
            },
        };

        var result = CreateEngine().Generate(new WordGenerationRequest(
            destination,
            definition,
            "usr_test",
            "cnv_test",
            Images: new Dictionary<string, WordImageAsset>(StringComparer.Ordinal)
            {
                [image.FileId] = image,
            }), TestContext.Current.CancellationToken);

        Assert.True(result.Validation.IsAccepted);
        using var document = WordprocessingDocument.Open(destination, false);
        var body = document.MainDocumentPart?.Document?.Body
                   ?? throw new InvalidDataException("Missing generated document body.");
        var section = body.Elements<W.SectionProperties>().Last();
        var usableWidth = WordOpenXmlFactory.AvailableWidthTwips(section);
        var table = body.Descendants<W.Table>().Single();
        Assert.Equal(usableWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TableWidth>()?.Width?.Value);
        Assert.Equal(usableWidth, table.GetFirstChild<W.TableGrid>()!.Elements<W.GridColumn>()
            .Sum(column => int.Parse(column.Width!.Value!, System.Globalization.CultureInfo.InvariantCulture)));
        var extent = body.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().Single();
        Assert.True(extent.Cx?.Value <= usableWidth * 635L);
    }

    [Fact]
    public void GenerateAppliesHeaderFooterReferencesToEverySection()
    {
        using var files = new TestDocumentFactory();
        var destination = files.OutputPath("multi-section.docx");
        var source = TestDocumentFactory.JapaneseReportDefinition();
        var section = source.Sections.Single();
        var definition = source with
        {
            Sections =
            [
                section with
                {
                    Blocks =
                    [
                        .. section.Blocks,
                        new DocumentBlock(
                            DocumentBlockKind.SectionBreak,
                            SectionBreakKind: SectionBreakKind.NextPage),
                        new DocumentBlock(DocumentBlockKind.Paragraph, Text: "第2セクション"),
                    ],
                },
            ],
        };

        var result = CreateEngine().Generate(new WordGenerationRequest(
            destination,
            definition,
            "usr_test",
            "cnv_test"), TestContext.Current.CancellationToken);

        Assert.True(result.Validation.IsAccepted);
        using var document = WordprocessingDocument.Open(destination, false);
        var sections = document.MainDocumentPart?.Document?.Body?.Descendants<W.SectionProperties>().ToArray()
                       ?? throw new InvalidDataException("Missing generated sections.");
        Assert.Equal(2, sections.Length);
        Assert.All(sections, current =>
        {
            Assert.Contains(current.Elements<W.HeaderReference>(),
                reference => reference.Type?.Value == W.HeaderFooterValues.Default);
            Assert.Contains(current.Elements<W.HeaderReference>(),
                reference => reference.Type?.Value == W.HeaderFooterValues.First);
            Assert.Contains(current.Elements<W.FooterReference>(),
                reference => reference.Type?.Value == W.HeaderFooterValues.Default);
            Assert.Contains(current.Elements<W.FooterReference>(),
                reference => reference.Type?.Value == W.HeaderFooterValues.First);
            Assert.NotNull(current.GetFirstChild<W.TitlePage>());
        });
    }

    [Fact]
    public void GenerateInheritsAllowlistedTemplateHeadersFootersAndMediaWithoutLeakyContent()
    {
        using var files = new TestDocumentFactory();
        var template = files.CreateLeakyGenerationTemplate();
        var destination = files.OutputPath("sanitized.docx");
        var source = TestDocumentFactory.JapaneseReportDefinition(includeImage: true);
        var generatedImageBytes = DocxTestPackage.Png(320, 200);
        var generatedImage = new WordImageAsset(
            "img_fixture",
            generatedImageBytes,
            "image/png",
            Convert.ToHexStringLower(SHA256.HashData(generatedImageBytes)));
        var logicalSection = source.Sections.Single();
        var definition = source with
        {
            Sections =
            [
                logicalSection with
                {
                    Blocks =
                    [
                        .. logicalSection.Blocks,
                        new DocumentBlock(
                            DocumentBlockKind.SectionBreak,
                            SectionBreakKind: SectionBreakKind.NextPage),
                        new DocumentBlock(DocumentBlockKind.Paragraph, Text: "第2セクション"),
                    ],
                },
            ],
        };
        Assert.True(OoxmlDigitalSignaturePolicy.IsPresent(template));

        var result = CreateEngine().Generate(new WordGenerationRequest(
            destination,
            definition,
            "usr_test",
            "cnv_test",
            TemplatePath: template,
            Images: new Dictionary<string, WordImageAsset>(StringComparer.Ordinal)
            {
                [generatedImage.FileId] = generatedImage,
            }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Validation.IsAccepted);
        using var document = WordprocessingDocument.Open(destination, false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing generated main part.");
        var root = main.Document ?? throw new InvalidDataException("Missing generated document root.");
        Assert.DoesNotContain("SECRET", root.InnerText, StringComparison.Ordinal);
        Assert.Equal(3, main.HeaderParts.Count());
        Assert.Equal(3, main.FooterParts.Count());
        Assert.DoesNotContain(
            main.HeaderParts.SelectMany(part => part.Header!.Descendants()),
            element => element.LocalName is "oMath" or "oMathPara");
        var headerText = string.Concat(main.HeaderParts.Select(part => part.Header?.InnerText));
        var footerText = string.Concat(main.FooterParts.Select(part => part.Footer?.InnerText));
        Assert.Contains("Template default header", headerText, StringComparison.Ordinal);
        Assert.Contains("Template first header", headerText, StringComparison.Ordinal);
        Assert.Contains("Template even header", headerText, StringComparison.Ordinal);
        Assert.Contains("Template relationship label", headerText, StringComparison.Ordinal);
        Assert.DoesNotContain("合成ヘッダー", headerText, StringComparison.Ordinal);
        Assert.Contains("Template default footer", footerText, StringComparison.Ordinal);
        Assert.Contains("Template first footer", footerText, StringComparison.Ordinal);
        Assert.Contains("Template even footer", footerText, StringComparison.Ordinal);
        Assert.DoesNotContain("合成フッター", footerText, StringComparison.Ordinal);
        var sections = root.Body!.Descendants<W.SectionProperties>().ToArray();
        Assert.Equal(2, sections.Length);
        Assert.All(sections, section =>
        {
            Assert.Equal(3, section.Elements<W.HeaderReference>().Count());
            Assert.Equal(3, section.Elements<W.FooterReference>().Count());
            Assert.NotNull(section.GetFirstChild<W.TitlePage>());
        });

        Assert.NotNull(main.DocumentSettingsPart?.Settings?.GetFirstChild<W.EvenAndOddHeaders>());
        Assert.Null(main.DocumentSettingsPart?.Settings?.GetFirstChild<W.DocumentProtection>());
        var inheritedImage = Assert.Single(main.HeaderParts.SelectMany(part => part.ImageParts).Distinct());
        using (var image = inheritedImage.GetStream(FileMode.Open, FileAccess.Read))
        using (var buffer = new MemoryStream())
        {
            image.CopyTo(buffer);
            Assert.Equal(DocxTestPackage.Png(96, 48), buffer.ToArray());
        }

        Assert.Contains(
            main.HeaderParts.SelectMany(part => part.Header!.Descendants<DW.DocProperties>()),
            properties => properties.Description?.Value == "Synthetic template mark");
        var footerFields = main.FooterParts
            .SelectMany(part => part.Footer!.Descendants<W.FieldCode>())
            .Select(field => field.Text.Trim())
            .ToArray();
        Assert.Contains("PAGE", footerFields);
        Assert.Contains("NUMPAGES", footerFields);
        Assert.All(main.HeaderParts, part => Assert.Empty(part.ExternalRelationships));
        Assert.All(main.FooterParts, part => Assert.Empty(part.ExternalRelationships));
        Assert.Null(main.WordprocessingCommentsPart);
        Assert.Empty(main.CustomXmlParts);
        Assert.Null(document.CustomFilePropertiesPart);
        var inheritedStories = main.HeaderParts.Select(part => (OpenXmlElement)part.Header!)
            .Concat(main.FooterParts.Select(part => (OpenXmlElement)part.Footer!))
            .Append(root)
            .ToArray();
        Assert.All(inheritedStories, story =>
        {
            Assert.DoesNotContain("SECRET", story.InnerText, StringComparison.Ordinal);
            Assert.Empty(story.Descendants<W.Vanish>());
            Assert.DoesNotContain(story.Descendants(), element =>
                element.NamespaceUri == "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                && element.LocalName is "ins" or "del" or "moveFrom" or "moveTo");
        });
        Assert.Contains("Template accepted revision", headerText, StringComparison.Ordinal);
        var drawingIds = inheritedStories
            .SelectMany(story => story.Descendants<DW.DocProperties>())
            .Select(properties => properties.Id!.Value)
            .ToArray();
        Assert.Equal(drawingIds.Length, drawingIds.Distinct().Count());

        Assert.Null(document.PackageProperties.Creator);
        Assert.Null(document.PackageProperties.LastModifiedBy);
        Assert.Equal("合成業務報告書", document.PackageProperties.Title);
        Assert.Equal(12_240U, root.Body!.Elements<W.SectionProperties>().Last().GetFirstChild<W.PageSize>()!.Width!.Value);
        Assert.False(OoxmlDigitalSignaturePolicy.IsPresent(destination));
    }

    [Fact]
    public void GenerateRejectsRelationshipBearingDesignPartsBeforeCloningThem()
    {
        using var files = new TestDocumentFactory();
        var template = files.CreatePopulateTemplate("related-numbering-template.dotx");
        using (var document = WordprocessingDocument.Open(template, true))
        {
            var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing template main part.");
            var numbering = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
            numbering.Numbering ??= new W.Numbering();
            numbering.AddHyperlinkRelationship(
                new Uri("https://example.com/template-numbering-dependency"),
                isExternal: true);
            numbering.Numbering.Save();
        }

        var destination = files.OutputPath("related-numbering-output.docx");
        var error = Assert.Throws<WordMcpException>(() => CreateEngine().Generate(
            new WordGenerationRequest(
                destination,
                TestDocumentFactory.JapaneseReportDefinition(includeImage: false),
                "usr_test",
                "cnv_test",
                TemplatePath: template),
            TestContext.Current.CancellationToken));

        Assert.Equal("invalid_generation_template", error.Code);
        Assert.False(File.Exists(destination));
    }

    private static OpenXmlWordDocumentEngine CreateEngine() => new(new WordMcpOptions());

    private static WordAnalysisRequest AnalysisRequest(string source, string sha256) => new(
        source,
        Path.GetFileName(source),
        sha256,
        "usr_test",
        "cnv_test");

    private static TargetRecord Target(AnalysisSnapshot analysis, string kind, string snippet) =>
        analysis.Targets.Values.Single(value => value.Kind == kind && value.Story == "main" && value.Snippet == snippet);
}
