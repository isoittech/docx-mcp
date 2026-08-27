using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Rendering;
using WordMcp.Word;

namespace WordMcp.Tests;

public sealed class DocumentRendererIntegrationTests
{
    [Fact]
    public async Task RealRendererProducesUpdatedWarningFreeDocumentWithoutChangingPrimary()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("WORD_MCP_RENDER_INTEGRATION"));
        using var environment = new TestEnvironment();
        var definition = RepresentativeDefinition();
        var documentPath = Path.Combine(environment.Root, "representative.docx");
        var engine = new OpenXmlWordDocumentEngine(environment.Options.Value, environment.Time);
        var imageBytes = DocxTestPackage.RenderablePng(640, 360);
        var image = new WordImageAsset(
            "img_fixture",
            imageBytes,
            "image/png",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(imageBytes)));
        var generated = engine.Generate(
            new WordGenerationRequest(
                documentPath,
                definition,
                "usr_test",
                "cnv_test",
                Images: new Dictionary<string, WordImageAsset>(StringComparer.Ordinal)
                {
                    [image.FileId] = image,
                }),
            TestContext.Current.CancellationToken);
        Assert.True(generated.Validation.IsAccepted);
        Assert.Single(generated.OutputAnalysis.Items["images"]);
        Assert.True(generated.OutputAnalysis.Items["sections"].Count >= 2);
        var hashBefore = TestDocumentFactory.Sha256(documentPath);
        var renderer = CreateRenderer(environment);

        var result = await renderer.RenderAsync(
            documentPath,
            Path.Combine(environment.Root, "rendered"),
            requireIndexUpdate: true,
            finalizeDocumentForDistribution: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(hashBefore, TestDocumentFactory.Sha256(documentPath));
        Assert.NotNull(result.FinalizedDocumentPath);
        Assert.True(File.Exists(result.FinalizedDocumentPath));
        Assert.True(result.PageCount >= 3);
        Assert.Equal(result.PageCount, result.PreviewPaths.Count);
        Assert.Equal(result.ExpectedHeadingCount, result.MatchedHeadingCount);
        Assert.True(result.ExpectedHeadingCount >= 3);
        Assert.InRange(result.IndexUpdatePassCount, 2, 3);
        Assert.True(result.IndexConverged);
        Assert.True(result.TocPageNumberCount >= result.ExpectedHeadingCount);
        Assert.InRange(result.TocMaxPageNumber, 1, result.PageCount);
        Assert.All(result.PreviewPaths, path => Assert.True(new FileInfo(path).Length > 0));
        Assert.True(new FileInfo(result.PdfPath).Length > 0);
        Assert.Empty(result.Warnings);
        using (var finalized = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(
                   result.FinalizedDocumentPath,
                   false))
        {
            var main = finalized.MainDocumentPart
                       ?? throw new InvalidDataException("Missing finalized main document part.");
            Assert.Empty(main.DocumentSettingsPart?.Settings?.Elements<DocumentFormat.OpenXml.Wordprocessing.UpdateFieldsOnOpen>() ?? []);
            Assert.DoesNotContain(
                main.Document?.Descendants<DocumentFormat.OpenXml.Wordprocessing.FieldChar>() ?? [],
                field => field.Dirty?.Value == true);
            Assert.DoesNotContain(
                main.Document?.Descendants<DocumentFormat.OpenXml.Wordprocessing.SimpleField>() ?? [],
                field => field.Dirty?.Value == true);
            Assert.DoesNotContain(
                main.Document?.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>() ?? [],
                text => string.Equals(text.Text, "目次を更新してください", StringComparison.Ordinal));
            var tables = main.Document?.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().ToArray() ?? [];
            Assert.Equal(2, tables.Length);
            Assert.All(
                tables[1].Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>(),
                row => Assert.Null(row.TableRowProperties?.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.CantSplit>()));
        }

        var guard = new WordMcp.Storage.DocxPackageGuard(environment.Options);
        _ = await guard.ValidateSnapshotAsync(result.FinalizedDocumentPath, TestContext.Current.CancellationToken);
        Assert.Empty(engine.ValidateNewDocument(
            result.FinalizedDocumentPath,
            TestContext.Current.CancellationToken).CandidateErrors);
        var finalizedSha256 = TestDocumentFactory.Sha256(result.FinalizedDocumentPath);
        var finalizedAnalysis = engine.Analyze(
            new WordAnalysisRequest(
                result.FinalizedDocumentPath,
                "representative.docx",
                finalizedSha256,
                "usr_test",
                "cnv_test"),
            TestContext.Current.CancellationToken);
        Assert.Equal(finalizedSha256, finalizedAnalysis.SourceSha256);
        Assert.NotEqual(generated.OutputSha256, finalizedSha256);
        Assert.Contains(
            finalizedAnalysis.Items["fields"],
            item => string.Equals(item.Data["field_type"]?.ToString(), "TOC", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(environment.Root, "rendered", "render-work")));
    }

    [Fact]
    public void TableTextCoverageFindsContentMissingFromRenderedPdfText()
    {
        using var factory = new TestDocumentFactory();
        var documentPath = factory.CreateStoryDocument();
        var expected = DocumentRenderer.ExtractDistinctTableCellTexts(documentPath);

        Assert.Equal(["Key", "Value", "One", "Two"], expected);
        Assert.Equal(["Value", "Two"], DocumentRenderer.FindMissingTableTexts(expected, "Key One"));
        Assert.Empty(DocumentRenderer.FindMissingTableTexts(expected, "K e y Value\nO n e T w o"));
        Assert.Empty(DocumentRenderer.FindMissingTableTexts(
            ["QG-04の証跡確認", "最終承認会議の準備", "旧運用停止の周知"],
            "QG-04 の証跡 高橋レイ 2026-08-27 確認\n"
            + "最終承認会議 鈴木アオ 2026-08-28 の準備\n"
            + "旧運用停止の 田中ケイ 2026-08-30 周知"));
        Assert.Equal(
            ["QG-04の証跡確認"],
            DocumentRenderer.FindMissingTableTexts(["QG-04の証跡確認"], "QG-04 の証跡"));
    }

    [Fact]
    public async Task RealRendererSurfacesTableTextCoverageWarningFromPdfExtraction()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("WORD_MCP_RENDER_INTEGRATION"));
        using var environment = new TestEnvironment();
        var source = TestDocumentFactory.JapaneseReportDefinition();
        var sourceSection = source.Sections.Single();
        var definition = source with
        {
            Design = new DocumentDesignSpec(Cover: false, TableOfContents: false),
            HeaderFooter = new HeaderFooterPolicy(null, null, PageNumbers: false),
            Sections =
            [
                sourceSection with
                {
                    Blocks =
                    [
                        new DocumentBlock(DocumentBlockKind.Heading, Text: "横長表検証", Level: 2),
                        new DocumentBlock(
                            DocumentBlockKind.Table,
                            Table: new TableSpec(
                                ["指標", "計画", "実績", "単位", "判定"],
                                [["Gamma", "6", "4", "件", "要確認"]],
                                "KPI表",
                                "横長表のPDF欠落検証")),
                    ],
                },
            ],
        };
        var documentPath = Path.Combine(environment.Root, "wide-table.docx");
        var engine = new OpenXmlWordDocumentEngine(environment.Options.Value, environment.Time);
        var generated = engine.Generate(
            new WordGenerationRequest(documentPath, definition, "usr_test", "cnv_test"),
            TestContext.Current.CancellationToken);
        Assert.True(generated.Validation.IsAccepted);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The real renderer integration test requires Linux.");
        }

        // The test service intentionally mounts /tmp with noexec. Keep this fake executable in
        // the test assembly directory so the integration test exercises the configured process.
        var executableDirectory = Path.Combine(
            AppContext.BaseDirectory,
            $"renderer-extractor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(executableDirectory);
        try
        {
            var partialPdfTextExtractor = Path.Combine(executableDirectory, "partial-pdftotext.sh");
            await File.WriteAllTextAsync(
                partialPdfTextExtractor,
                "#!/bin/sh\nprintf 'Gamma\\n' > \"$3\"\n",
                TestContext.Current.CancellationToken);
            File.SetUnixFileMode(
                partialPdfTextExtractor,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await CreateRenderer(environment, partialPdfTextExtractor).RenderAsync(
                documentPath,
                Path.Combine(environment.Root, "wide-table-rendered"),
                requireIndexUpdate: false,
                finalizeDocumentForDistribution: false,
                TestContext.Current.CancellationToken);

            Assert.Contains(
                result.Warnings,
                warning => warning.StartsWith("preview_table_text_missing:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(executableDirectory, recursive: true);
        }
    }

    private static DocumentRenderer CreateRenderer(TestEnvironment environment, string? pdfToTextPath = null)
    {
        var options = Options.Create(new WordMcpOptions
        {
            LibreOfficePath = environment.Options.Value.LibreOfficePath,
            PythonPath = environment.Options.Value.PythonPath,
            UnoScriptPath = "/src/scripts/update-word-indexes.py",
            PdfInfoPath = environment.Options.Value.PdfInfoPath,
            PdfToTextPath = pdfToTextPath ?? environment.Options.Value.PdfToTextPath,
            PdfToPngPath = environment.Options.Value.PdfToPngPath,
        });
        return new DocumentRenderer(new ProcessRunner(), options);
    }

    private static DocumentDefinition RepresentativeDefinition()
    {
        var source = TestDocumentFactory.JapaneseReportDefinition(includeImage: true);
        var section = source.Sections.Single();
        return source with
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
                        new DocumentBlock(DocumentBlockKind.Heading, Text: "検証結果", Level: 2),
                        new DocumentBlock(DocumentBlockKind.Paragraph, Text: "全ページの組版と目次のページ番号を検証します。"),
                        new DocumentBlock(
                            DocumentBlockKind.Table,
                            Table: new TableSpec(
                                ["分割方針", "期待値"],
                                [["行分割", "許可"]],
                                "行分割検証",
                                "行分割を許可する合成表",
                                AllowRowSplit: true)),
                        new DocumentBlock(DocumentBlockKind.PageBreak),
                        new DocumentBlock(DocumentBlockKind.Heading, Text: "結論", Level: 2),
                        new DocumentBlock(DocumentBlockKind.Paragraph, Text: "合成データだけを使ったレンダリング統合試験です。"),
                    ],
                },
            ],
        };
    }
}
