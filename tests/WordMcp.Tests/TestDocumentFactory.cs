using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Domain;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Tests;

internal sealed class TestDocumentFactory : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"word-mcp-engine-tests-{Guid.NewGuid():N}");

    public TestDocumentFactory()
    {
        Directory.CreateDirectory(root);
    }

    public string OutputPath(string fileName) => Path.Combine(root, fileName);

    public string CreateStoryDocument(string fileName = "source.docx")
    {
        var path = OutputPath(fileName);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document, autoSave: true);
        var main = document.AddMainDocumentPart();
        AddStyles(main);
        AddSettings(main);

        var header = main.AddNewPart<HeaderPart>();
        header.Header = new W.Header(new W.Paragraph(new W.Run(new W.Text("Synthetic header"))));
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new W.Footer(CreatePageFieldParagraph("Synthetic footer "));
        AddNotesAndComments(main);

        var splitRunParagraph = new W.Paragraph(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
            new W.Run(new W.Text("Alpha ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.RunProperties(new W.Bold()), new W.Text("Beta")));
        var ordinaryParagraph = new W.Paragraph(new W.Run(new W.Text("Second paragraph")));
        var boundaryParagraph = new W.Paragraph(
            new W.Run(new W.Text("Before")),
            new W.BookmarkStart { Id = "7", Name = "ProtectedBoundary" },
            new W.Run(new W.Text("After")),
            new W.BookmarkEnd { Id = "7" });
        var noteReferences = new W.Paragraph(
            new W.CommentRangeStart { Id = "0" },
            new W.Run(new W.Text("Commented")),
            new W.CommentRangeEnd { Id = "0" },
            new W.Run(new W.CommentReference { Id = "0" }),
            new W.Run(new W.Text(" ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.FootnoteReference { Id = 1 }),
            new W.Run(new W.Text(" ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.EndnoteReference { Id = 1 }));
        var table = CreateTable();
        var section = new W.SectionProperties(
            new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
            new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new W.PageSize { Width = 11_906U, Height = 16_838U },
            new W.PageMargin
            {
                Top = 1_134,
                Right = 1_134U,
                Bottom = 1_134,
                Left = 1_134U,
                Header = 720U,
                Footer = 720U,
                Gutter = 0U,
            });
        main.Document = new W.Document(
            new W.Body(splitRunParagraph, ordinaryParagraph, boundaryParagraph, noteReferences, table, section));
        document.PackageProperties.Title = "Synthetic test document";
        document.PackageProperties.Subject = "No customer data";
        main.Document.Save();
        return path;
    }

    public string CreatePopulateTemplate(string fileName = "template.dotx")
    {
        var path = OutputPath(fileName);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Template, autoSave: true);
        var main = document.AddMainDocumentPart();
        AddStyles(main);
        AddSettings(main);

        var inline = new W.SdtRun(
            SdtProperties("CustomerName", "Customer name"),
            new W.SdtContentRun(new W.Run(new W.Text("inline placeholder"))));
        var block = new W.SdtBlock(
            SdtProperties("Summary", "Summary"),
            new W.SdtContentBlock(new W.Paragraph(new W.Run(new W.Text("block placeholder")))));
        var cell = new W.SdtCell(
            SdtProperties("Status", "Status"),
            new W.SdtContentCell(new W.TableCell(
                new W.TableCellProperties(),
                new W.Paragraph(new W.Run(new W.Text("cell placeholder"))))));
        var bookmark = new W.Paragraph(
            new W.BookmarkStart { Id = "10", Name = "LegacyField" },
            new W.Run(new W.Text("bookmark placeholder")),
            new W.BookmarkEnd { Id = "10" });
        var table = new W.Table(
            new W.TableProperties(new W.TableWidth { Width = "4000", Type = W.TableWidthUnitValues.Dxa }),
            new W.TableGrid(new W.GridColumn { Width = "4000" }),
            new W.TableRow(cell));
        main.Document = new W.Document(
            new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Name: ") { Space = SpaceProcessingModeValues.Preserve }), inline),
                block,
                table,
                bookmark,
                new W.SectionProperties()));
        main.Document.Save();
        return path;
    }

    public string CreateLeakyGenerationTemplate(string fileName = "leaky-template.dotx")
    {
        var path = OutputPath(fileName);
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Template, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            AddStyles(main);
            var settings = main.AddNewPart<DocumentSettingsPart>();
            settings.Settings = new W.Settings(
                new W.EvenAndOddHeaders(),
                new W.DocumentProtection
                {
                    Edit = W.DocumentProtectionValues.ReadOnly,
                    Enforcement = true,
                });

            var defaultHeader = main.AddNewPart<HeaderPart>();
            var imageBytes = DocxTestPackage.Png(96, 48);
            var imagePart = defaultHeader.AddImagePart(ImagePartType.Png);
            using (var image = new MemoryStream(imageBytes, writable: false))
            {
                imagePart.FeedData(image);
            }

            var external = defaultHeader.AddHyperlinkRelationship(
                new Uri("file:///synthetic-unsafe-target"),
                isExternal: true);
            defaultHeader.Header = new W.Header(
                new W.Paragraph(
                    new W.Run(new W.Text("Template default header")),
                    new W.Run(
                        new W.RunProperties(new W.Vanish()),
                        new W.Text("SECRET HEADER HIDDEN")),
                    new W.DeletedRun(new W.Run(new W.DeletedText("SECRET HEADER DELETED")))
                    {
                        Id = "8",
                        Author = "Sensitive Author",
                    },
                    new W.InsertedRun(new W.Run(new W.Text(" Template accepted revision")))
                    {
                        Id = "9",
                        Author = "Sensitive Author",
                    },
                    new W.CommentRangeStart { Id = "0" },
                    new W.Run(new W.CommentReference { Id = "0" }),
                    new W.CommentRangeEnd { Id = "0" },
                    new M.OfficeMath(new M.Run(new M.Text("SECRET EQUATION")))),
                new W.Paragraph(new W.Run(CreateDrawing(
                    defaultHeader.GetIdOfPart(imagePart),
                    "Synthetic template mark",
                    41U))),
                new W.Paragraph(new W.Hyperlink(
                    new W.Run(new W.Text("Template relationship label")))
                {
                    Id = external.Id,
                }));

            var firstHeader = main.AddNewPart<HeaderPart>();
            firstHeader.Header = new W.Header(new W.Paragraph(new W.Run(new W.Text("Template first header"))));
            var evenHeader = main.AddNewPart<HeaderPart>();
            evenHeader.AddPart(imagePart);
            evenHeader.Header = new W.Header(
                new W.Paragraph(new W.Run(new W.Text("Template even header"))),
                new W.Paragraph(new W.Run(CreateDrawing(
                    evenHeader.GetIdOfPart(imagePart),
                    "Synthetic even-page template mark",
                    42U))));
            var defaultFooter = main.AddNewPart<FooterPart>();
            defaultFooter.Footer = new W.Footer(CreatePageCountFieldParagraph("Template default footer "));
            var firstFooter = main.AddNewPart<FooterPart>();
            firstFooter.Footer = new W.Footer(new W.Paragraph(new W.Run(new W.Text("Template first footer"))));
            var evenFooter = main.AddNewPart<FooterPart>();
            evenFooter.Footer = new W.Footer(CreatePageCountFieldParagraph("Template even footer "));

            var comments = main.AddNewPart<WordprocessingCommentsPart>();
            comments.Comments = new W.Comments(
                new W.Comment(new W.Paragraph(new W.Run(new W.Text("SECRET COMMENT"))))
                {
                    Id = "0",
                    Author = "Sensitive Author",
                });
            var customXml = main.AddCustomXmlPart(CustomXmlPartType.CustomXml);
            var customBytes = Encoding.UTF8.GetBytes("<secret>SECRET CUSTOM XML</secret>");
            using (var stream = customXml.GetStream(FileMode.Create, FileAccess.Write))
            {
                stream.Write(customBytes);
            }

            main.Document = new W.Document(
                new W.Body(
                    new W.Paragraph(
                        new W.Run(new W.Text("SECRET SAMPLE BODY")),
                        new W.Run(new W.RunProperties(new W.Vanish()), new W.Text("SECRET HIDDEN TEXT")),
                        new W.InsertedRun(new W.Run(new W.Text("SECRET REVISION")))
                        {
                            Id = "7",
                            Author = "Sensitive Author",
                        }),
                    new W.SectionProperties(
                        new W.HeaderReference
                        {
                            Type = W.HeaderFooterValues.Default,
                            Id = main.GetIdOfPart(defaultHeader),
                        },
                        new W.HeaderReference
                        {
                            Type = W.HeaderFooterValues.First,
                            Id = main.GetIdOfPart(firstHeader),
                        },
                        new W.HeaderReference
                        {
                            Type = W.HeaderFooterValues.Even,
                            Id = main.GetIdOfPart(evenHeader),
                        },
                        new W.FooterReference
                        {
                            Type = W.HeaderFooterValues.Default,
                            Id = main.GetIdOfPart(defaultFooter),
                        },
                        new W.FooterReference
                        {
                            Type = W.HeaderFooterValues.First,
                            Id = main.GetIdOfPart(firstFooter),
                        },
                        new W.FooterReference
                        {
                            Type = W.HeaderFooterValues.Even,
                            Id = main.GetIdOfPart(evenFooter),
                        },
                        new W.PageSize { Width = 12_240U, Height = 15_840U },
                        new W.PageMargin
                        {
                            Top = 1_000,
                            Right = 1_100U,
                            Bottom = 1_200,
                            Left = 1_300U,
                            Header = 500U,
                            Footer = 500U,
                            Gutter = 0U,
                        },
                        new W.TitlePage())));
            document.PackageProperties.Title = "SECRET TEMPLATE TITLE";
            document.PackageProperties.Creator = "Sensitive Author";
            document.PackageProperties.LastModifiedBy = "Sensitive Reviewer";
            main.Document.Save();
        }

        AddSyntheticLeakyPackageParts(path);
        return path;
    }

    public string CreateInvalidBodyDocument(int invalidRunCount, string fileName)
    {
        var path = CreateStoryDocument(fileName);
        using var document = WordprocessingDocument.Open(path, true);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing synthetic main part.");
        var body = main.Document?.Body ?? throw new InvalidDataException("Missing synthetic body.");
        var section = body.Elements<W.SectionProperties>().Single();
        for (var index = 0; index < invalidRunCount; index++)
        {
            section.InsertBeforeSelf(new W.Run(new W.Text($"invalid-{index}")));
        }

        main.Document.Save();
        return path;
    }

    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static byte[] ReadEntryPayload(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName)
                    ?? throw new InvalidDataException($"Missing test ZIP entry '{entryName}'.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static DocumentDefinition JapaneseReportDefinition(bool includeImage = false) => new(
        Title: "合成業務報告書",
        Purpose: "Word MCP の宣言型生成を検証する",
        Audience: "開発チーム",
        Subject: "合成テスト",
        Locale: "ja-JP",
        ExpectedSectionCount: 1,
        TemplateSource: "none",
        Layout: new DocumentLayoutSpec(),
        Theme: new DocumentThemeSpec(),
        Design: new DocumentDesignSpec(Cover: true, TableOfContents: true),
        HeaderFooter: new HeaderFooterPolicy("合成ヘッダー", "合成フッター", PageNumbers: true),
        Sections:
        [
            new LogicalSectionSpec(
                "overview",
                "概要",
                [
                    new DocumentBlock(DocumentBlockKind.Heading, Text: "背景", Level: 2),
                    new DocumentBlock(
                        DocumentBlockKind.Paragraph,
                        Runs: [new SemanticRun("重要", Bold: true), new SemanticRun("な説明です。")]),
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
                    new DocumentBlock(DocumentBlockKind.Callout, Text: "注意事項"),
                    ..(includeImage
                        ? new[]
                        {
                            new DocumentBlock(
                                DocumentBlockKind.Image,
                                ImageFileId: "img_fixture",
                                AltText: "合成検証画像",
                                Caption: "図1 合成画像"),
                        }
                        : Array.Empty<DocumentBlock>()),
                ])
        ]);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new W.Styles(
            new W.Style(new W.StyleName { Val = "Normal" }, new W.PrimaryStyle())
            {
                Type = W.StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true,
            },
            new W.Style(
                new W.StyleName { Val = "heading 1" },
                new W.BasedOn { Val = "Normal" },
                new W.NextParagraphStyle { Val = "Normal" },
                new W.PrimaryStyle(),
                new W.StyleParagraphProperties(new W.KeepNext(), new W.OutlineLevel { Val = 0 }),
                new W.StyleRunProperties(new W.Bold()))
            {
                Type = W.StyleValues.Paragraph,
                StyleId = "Heading1",
            });
        part.Styles.Save();
    }

    private static void AddSettings(MainDocumentPart main)
    {
        var part = main.AddNewPart<DocumentSettingsPart>();
        part.Settings = new W.Settings(new W.UpdateFieldsOnOpen { Val = true });
        part.Settings.Save();
    }

    private static void AddNotesAndComments(MainDocumentPart main)
    {
        var footnotes = main.AddNewPart<FootnotesPart>();
        footnotes.Footnotes = new W.Footnotes(
            new W.Footnote(new W.Paragraph(new W.Run(new W.SeparatorMark())))
            {
                Type = W.FootnoteEndnoteValues.Separator,
                Id = -1,
            },
            new W.Footnote(new W.Paragraph(new W.Run(new W.ContinuationSeparatorMark())))
            {
                Type = W.FootnoteEndnoteValues.ContinuationSeparator,
                Id = 0,
            },
            new W.Footnote(new W.Paragraph(new W.Run(new W.FootnoteReferenceMark()), new W.Run(new W.Text("Synthetic footnote"))))
            {
                Id = 1,
            });

        var endnotes = main.AddNewPart<EndnotesPart>();
        endnotes.Endnotes = new W.Endnotes(
            new W.Endnote(new W.Paragraph(new W.Run(new W.SeparatorMark())))
            {
                Type = W.FootnoteEndnoteValues.Separator,
                Id = -1,
            },
            new W.Endnote(new W.Paragraph(new W.Run(new W.ContinuationSeparatorMark())))
            {
                Type = W.FootnoteEndnoteValues.ContinuationSeparator,
                Id = 0,
            },
            new W.Endnote(new W.Paragraph(new W.Run(new W.EndnoteReferenceMark()), new W.Run(new W.Text("Synthetic endnote"))))
            {
                Id = 1,
            });

        var comments = main.AddNewPart<WordprocessingCommentsPart>();
        comments.Comments = new W.Comments(
            new W.Comment(new W.Paragraph(new W.Run(new W.Text("Synthetic comment"))))
            {
                Id = "0",
                Author = "Synthetic",
                Initials = "S",
            });
    }

    private static W.Table CreateTable() => new(
        new W.TableProperties(
            new W.TableWidth { Width = "6000", Type = W.TableWidthUnitValues.Dxa },
            new W.TableLayout { Type = W.TableLayoutValues.Fixed }),
        new W.TableGrid(new W.GridColumn { Width = "3000" }, new W.GridColumn { Width = "3000" }),
        new W.TableRow(
            new W.TableRowProperties(new W.TableHeader()),
            CreateCell("Key", "3000"),
            CreateCell("Value", "3000")),
        new W.TableRow(CreateCell("One", "3000"), CreateCell("Two", "3000")));

    private static W.TableCell CreateCell(string text, string width) => new(
        new W.TableCellProperties(new W.TableCellWidth { Width = width, Type = W.TableWidthUnitValues.Dxa }),
        new W.Paragraph(new W.Run(new W.Text(text))));

    private static W.SdtProperties SdtProperties(string tag, string alias) => new(
        new W.SdtAlias { Val = alias },
        new W.Tag { Val = tag });

    private static W.Drawing CreateDrawing(string relationshipId, string altText, uint drawingId) => new(
        new DW.Inline(
            new DW.Extent { Cx = 914_400L, Cy = 457_200L },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties
            {
                Id = drawingId,
                Name = "Synthetic template image",
                Description = altText,
            },
            new DW.NonVisualGraphicFrameDrawingProperties(
                new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(
                new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties
                            {
                                Id = 0U,
                                Name = "synthetic-template.png",
                            },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relationshipId },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0, Y = 0 },
                                new A.Extents { Cx = 914_400L, Cy = 457_200L }),
                            new A.PresetGeometry(new A.AdjustValueList())
                            {
                                Preset = A.ShapeTypeValues.Rectangle,
                            })))
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
                }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U,
        });

    private static W.Paragraph CreatePageCountFieldParagraph(string prefix)
    {
        var paragraph = new W.Paragraph(
            new W.Run(new W.Text(prefix) { Space = SpaceProcessingModeValues.Preserve }));
        AppendField(paragraph, " PAGE ");
        paragraph.Append(new W.Run(new W.Text(" / ")));
        AppendField(paragraph, " NUMPAGES ");
        return paragraph;
    }

    private static void AppendField(W.Paragraph paragraph, string instruction)
    {
        paragraph.Append(
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin, Dirty = true }),
            new W.Run(new W.FieldCode(instruction) { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
            new W.Run(new W.Text("1")),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End }));
    }

    private static void AddSyntheticLeakyPackageParts(string path)
    {
        const string contentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
        const string relationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string customPropertiesContentType = "application/vnd.openxmlformats-officedocument.custom-properties+xml";
        const string customPropertiesRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties";
        const string originContentType = "application/vnd.openxmlformats-package.digital-signature-origin";
        const string signatureContentType = "application/vnd.openxmlformats-package.digital-signature-xmlsignature+xml";
        const string originRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin";
        const string signatureRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/signature";

        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var contentTypes = ReadXml(archive, "[Content_Types].xml");
        var types = contentTypes.Root ?? throw new InvalidDataException("Missing content-types root.");
        var contentTypeNamespace = (XNamespace)contentTypesNamespace;
        types.Add(
            new XElement(
                contentTypeNamespace + "Override",
                new XAttribute("PartName", "/docProps/custom.xml"),
                new XAttribute("ContentType", customPropertiesContentType)),
            new XElement(
                contentTypeNamespace + "Override",
                new XAttribute("PartName", "/_xmlsignatures/origin.sigs"),
                new XAttribute("ContentType", originContentType)),
            new XElement(
                contentTypeNamespace + "Override",
                new XAttribute("PartName", "/_xmlsignatures/sig1.xml"),
                new XAttribute("ContentType", signatureContentType)));
        ReplaceXml(archive, "[Content_Types].xml", contentTypes);

        var packageRelationships = ReadXml(archive, "_rels/.rels");
        var relationships = packageRelationships.Root ?? throw new InvalidDataException("Missing relationships root.");
        var relationshipNamespace = (XNamespace)relationshipsNamespace;
        relationships.Add(
            new XElement(
                relationshipNamespace + "Relationship",
                new XAttribute("Id", "rIdSyntheticCustomProperties"),
                new XAttribute("Type", customPropertiesRelationship),
                new XAttribute("Target", "docProps/custom.xml")),
            new XElement(
                relationshipNamespace + "Relationship",
                new XAttribute("Id", "rIdSyntheticSignatureOrigin"),
                new XAttribute("Type", originRelationship),
                new XAttribute("Target", "_xmlsignatures/origin.sigs")));
        ReplaceXml(archive, "_rels/.rels", packageRelationships);

        WriteEntry(
            archive,
            "docProps/custom.xml",
            Encoding.UTF8.GetBytes("""
                <?xml version="1.0" encoding="utf-8"?>
                <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/custom-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
                  <property fmtid="{D5CDD505-2E9C-101B-9397-08002B2CF9AE}" pid="2" name="SensitiveCompany"><vt:lpwstr>SECRET COMPANY</vt:lpwstr></property>
                </Properties>
                """));
        WriteEntry(archive, "_xmlsignatures/origin.sigs", []);
        WriteEntry(
            archive,
            "_xmlsignatures/_rels/origin.sigs.rels",
            Encoding.UTF8.GetBytes($"""
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="{relationshipsNamespace}">
                  <Relationship Id="rIdSyntheticSignature" Type="{signatureRelationship}" Target="sig1.xml" />
                </Relationships>
                """));
        WriteEntry(
            archive,
            "_xmlsignatures/sig1.xml",
            Encoding.UTF8.GetBytes("""
                <?xml version="1.0" encoding="utf-8"?>
                <Signature xmlns="http://www.w3.org/2000/09/xmldsig#" Id="SyntheticSignature" />
                """));
    }

    private static XDocument ReadXml(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
                    ?? throw new InvalidDataException($"Missing synthetic package entry '{entryName}'.");
        using var input = entry.Open();
        return XDocument.Load(input, LoadOptions.PreserveWhitespace);
    }

    private static void ReplaceXml(ZipArchive archive, string entryName, XDocument document)
    {
        archive.GetEntry(entryName)?.Delete();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var output = entry.Open();
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void WriteEntry(ZipArchive archive, string entryName, byte[] bytes)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static W.Paragraph CreatePageFieldParagraph(string prefix) => new(
        new W.Run(new W.Text(prefix) { Space = SpaceProcessingModeValues.Preserve }),
        new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin, Dirty = true }),
        new W.Run(new W.FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
        new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
        new W.Run(new W.Text("1")),
        new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End }));
}
