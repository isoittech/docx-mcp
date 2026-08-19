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
        var options = Options.Create(new WordMcpOptions
        {
            LibreOfficePath = environment.Options.Value.LibreOfficePath,
            PythonPath = environment.Options.Value.PythonPath,
            UnoScriptPath = "/src/scripts/update-word-indexes.py",
            PdfInfoPath = environment.Options.Value.PdfInfoPath,
            PdfToPngPath = environment.Options.Value.PdfToPngPath,
        });
        var renderer = new DocumentRenderer(new ProcessRunner(), options);

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

        var guard = new WordMcp.Storage.DocxPackageGuard(options);
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
