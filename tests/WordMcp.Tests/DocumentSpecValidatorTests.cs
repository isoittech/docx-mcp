using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Drafts;

namespace WordMcp.Tests;

public sealed class DocumentSpecValidatorTests
{
    [Fact]
    public void ValidateSectionBatchAcceptsJapaneseSemanticBlocks()
    {
        using var environment = new TestEnvironment();
        var validator = new DocumentSpecValidator(environment.Options);
        var section = new LogicalSectionSpec(
            "overview",
            "概要",
            [
                new DocumentBlock(DocumentBlockKind.Heading, Text: "背景", Level: 2),
                new DocumentBlock(DocumentBlockKind.Paragraph, Runs: [new SemanticRun("重要", Bold: true), new SemanticRun("な説明です。")]),
                new DocumentBlock(
                    DocumentBlockKind.OrderedList,
                    Items:
                    [
                        new ListItemSpec([new SemanticRun("確認する")]),
                        new ListItemSpec([new SemanticRun("実施する")], Level: 1),
                    ]),
                new DocumentBlock(
                    DocumentBlockKind.Table,
                    Table: new TableSpec(["項目", "結果"], [["検証", "成功"]], "結果表", "検証結果")),
            ]);

        validator.ValidateSectionBatch([section]);
    }

    [Fact]
    public void ValidateSectionBatchRejectsManualPayloadMixing()
    {
        using var environment = new TestEnvironment();
        var validator = new DocumentSpecValidator(environment.Options);
        var invalid = new LogicalSectionSpec(
            "bad",
            "不正",
            [new DocumentBlock(DocumentBlockKind.Paragraph, Text: "text", Runs: [new SemanticRun("run")])]);

        var exception = Assert.Throws<WordMcpException>(() => validator.ValidateSectionBatch([invalid]));

        Assert.Equal("invalid_text_block", exception.Code);
    }

    [Fact]
    public void ValidateSectionBatchRejectsListDeeperThanFourLevels()
    {
        using var environment = new TestEnvironment();
        var validator = new DocumentSpecValidator(environment.Options);
        var invalid = new LogicalSectionSpec(
            "bad-list",
            "不正リスト",
            [new DocumentBlock(DocumentBlockKind.UnorderedList, Items: [new ListItemSpec([new SemanticRun("too deep")], 4)])]);

        var exception = Assert.Throws<WordMcpException>(() => validator.ValidateSectionBatch([invalid]));

        Assert.Equal("list_level_out_of_range", exception.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateSectionBatchRejectsAutomaticallyRenderedTitleAsFirstHeading(bool useRuns)
    {
        using var environment = new TestEnvironment();
        var validator = new DocumentSpecValidator(environment.Options);
        var duplicateHeading = useRuns
            ? new DocumentBlock(
                DocumentBlockKind.Heading,
                Runs: [new SemanticRun(" 課題と"), new SemanticRun("リスク　")],
                Level: 1)
            : new DocumentBlock(DocumentBlockKind.Heading, Text: "課題とリスク", Level: 1);
        var invalid = new LogicalSectionSpec(
            "risks",
            "課題とリスク",
            [
                new DocumentBlock(DocumentBlockKind.PageBreak),
                duplicateHeading,
                new DocumentBlock(DocumentBlockKind.Paragraph, Text: "本文です。"),
            ]);

        var exception = Assert.Throws<WordMcpException>(() => validator.ValidateSectionBatch([invalid]));

        Assert.Equal("section_title_repeated", exception.Code);
        Assert.Equal("$.sections[0].blocks[1]", exception.FieldPath);
    }

    [Fact]
    public void ValidateSectionBatchAllowsDistinctNestedHeading()
    {
        using var environment = new TestEnvironment();
        var validator = new DocumentSpecValidator(environment.Options);
        var section = new LogicalSectionSpec(
            "risks",
            "課題とリスク",
            [
                new DocumentBlock(DocumentBlockKind.Heading, Text: "主要リスク", Level: 2),
                new DocumentBlock(DocumentBlockKind.Paragraph, Text: "本文です。"),
            ]);

        validator.ValidateSectionBatch([section]);
    }

    [Fact]
    public void ValidateDefinitionRejectsNullNestedModelsAsStructuredInputError()
    {
        using var environment = new TestEnvironment();
        var validator = new DocumentSpecValidator(environment.Options);
        var invalid = new DocumentDefinition(
            "報告書",
            "検証",
            "関係者",
            null,
            "ja-JP",
            1,
            "none",
            null!,
            new DocumentThemeSpec(),
            new DocumentDesignSpec(),
            new HeaderFooterPolicy(),
            [null!]);

        var exception = Assert.Throws<WordMcpException>(() => validator.ValidateDefinition(invalid, requireComplete: true));

        Assert.Equal("layout_required", exception.Code);
        Assert.Equal("$.layout", exception.FieldPath);
    }

    [Fact]
    public void ValidateDefinitionAcceptsAggregateResourcesAtConfiguredLimits()
    {
        var validator = Validator(maxTableCells: 8, maxImages: 2, maxExplicitPageBreaks: 2);
        var definition = Definition(
            new LogicalSectionSpec(
                "first",
                "First",
                [
                    new DocumentBlock(
                        DocumentBlockKind.Table,
                        Table: new TableSpec(["A", "B"], [["1", "2"]])),
                    new DocumentBlock(
                        DocumentBlockKind.Image,
                        ImageFileId: "image_first",
                        AltText: "First image"),
                    new DocumentBlock(DocumentBlockKind.PageBreak),
                ]),
            new LogicalSectionSpec(
                "second",
                "Second",
                [
                    new DocumentBlock(
                        DocumentBlockKind.KeyValue,
                        KeyValues: [new KeyValueSpec("Key", "Value")]),
                    new DocumentBlock(
                        DocumentBlockKind.Image,
                        ImageFileId: "image_second",
                        AltText: "Second image"),
                    new DocumentBlock(DocumentBlockKind.PageBreak),
                ]));

        validator.ValidateDefinition(definition, requireComplete: true);
    }

    [Theory]
    [InlineData("table-cells", "table_cell_limit")]
    [InlineData("images", "image_limit")]
    [InlineData("page-breaks", "explicit_page_break_limit")]
    public void ValidateDefinitionRejectsAggregateResourcesAcrossSections(
        string resource,
        string expectedCode)
    {
        var validator = Validator(maxTableCells: 4, maxImages: 1, maxExplicitPageBreaks: 1);
        var definition = Definition(
            SectionWithResource("first", resource),
            SectionWithResource("second", resource));

        var exception = Assert.Throws<WordMcpException>(() =>
            validator.ValidateDefinition(definition, requireComplete: true));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal("$.sections", exception.FieldPath);
    }

    private static DocumentSpecValidator Validator(
        int maxTableCells,
        int maxImages,
        int maxExplicitPageBreaks) => new(Options.Create(new WordMcpOptions
    {
        MaxTableCells = maxTableCells,
        MaxImages = maxImages,
        MaxExplicitPageBreaks = maxExplicitPageBreaks,
    }));

    private static LogicalSectionSpec SectionWithResource(string key, string resource) => new(
        key,
        key,
        resource switch
        {
            "table-cells" =>
            [
                new DocumentBlock(
                    DocumentBlockKind.Table,
                    Table: new TableSpec(["A", "B"], [["1", "2"]])),
            ],
            "images" =>
            [
                new DocumentBlock(
                    DocumentBlockKind.Image,
                    ImageFileId: $"image_{key}",
                    AltText: $"Image {key}"),
            ],
            "page-breaks" => [new DocumentBlock(DocumentBlockKind.PageBreak)],
            _ => throw new InvalidOperationException("The test resource is not supported."),
        });

    private static DocumentDefinition Definition(params LogicalSectionSpec[] sections) => new(
        "Aggregate limits",
        "Validate complete document resource limits.",
        "Automated tests",
        null,
        "en-US",
        sections.Length,
        "none",
        new DocumentLayoutSpec(),
        new DocumentThemeSpec(),
        new DocumentDesignSpec(),
        new HeaderFooterPolicy(),
        sections);
}
