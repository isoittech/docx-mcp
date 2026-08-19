using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Configuration;
using WordMcp.Domain;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Word;

internal sealed class OpenXmlDocumentGenerator(WordMcpOptions options)
{
    private const long EmusPerInch = 914_400;
    private readonly WordMcpOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HashSet<string> bookmarkNames = new(StringComparer.Ordinal);
    private uint nextDrawingId = 1;
    private int nextBookmarkId = 1;

    public void Generate(
        string path,
        DocumentDefinition definition,
        string? templatePath,
        IReadOnlyDictionary<string, WordImageAsset> images,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(images);
        ValidateDefinition(definition);
        var imageBudget = ValidateImages(definition, images);
        cancellationToken.ThrowIfCancellationRequested();

        using var output = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document, autoSave: true);
        var main = output.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());

        W.SectionProperties? templateSection = null;
        TemplateHeaderFooterDesign? templateHeaderFooter = null;
        if (templatePath is not null)
        {
            using var template = WordprocessingDocument.Open(templatePath, false);
            var templateMain = template.MainDocumentPart
                               ?? throw InvalidTemplate("The template has no main document part.");
            var templateDocument = templateMain.Document
                                   ?? throw InvalidTemplate("The template main part has no document root.");
            var templateSections = templateDocument.Body?.Descendants<W.SectionProperties>().ToArray()
                                   ?? [];
            templateHeaderFooter = CopyTemplateDesign(
                templateMain,
                main,
                templateSections,
                imageBudget,
                cancellationToken);
            templateSection = SanitizeSectionProperties(templateSections.LastOrDefault());
        }

        EnsureStyles(main, definition);
        EnsureTheme(main, definition);
        var numbering = EnsureNumbering(main);
        EnsureSettings(
            main,
            definition,
            templateHeaderFooter?.HasParts == true
                ? templateHeaderFooter.DifferentEvenOdd
                : definition.HeaderFooter.DifferentEvenOdd);
        var finalSection = templateSection ?? CreateLayoutSection(definition.Layout);
        var availableWidthTwips = WordOpenXmlFactory.AvailableWidthTwips(finalSection);
        if (availableWidthTwips < 720)
        {
            throw new WordMcpException(
                "page_content_width_out_of_range",
                "$.layout",
                "The selected page, margins, and columns leave too little usable content width.",
                "Reduce the margins or column count before generating the document.");
        }

        var body = main.Document.Body!;
        if (definition.Design.Cover)
        {
            body.Append(CreateStyledParagraph("Title", [new SemanticRun(definition.Title)], keepNext: true));
            body.Append(CreateStyledParagraph("Subtitle", [new SemanticRun(definition.Purpose)]));
            body.Append(CreateStyledParagraph("Normal", [new SemanticRun($"対象読者: {definition.Audience}")], keepLines: true));
            body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
        }

        if (definition.Design.TableOfContents)
        {
            body.Append(CreateStyledParagraph("TOCHeading", [new SemanticRun("目次")], keepNext: true));
            body.Append(CreateTocParagraph());
            body.Append(new W.Paragraph());
        }

        foreach (var section in definition.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            body.Append(CreateSectionHeading(section));
            foreach (var block in section.Blocks)
            {
                foreach (var element in CreateBlock(
                             main,
                             block,
                             definition,
                             numbering,
                             images,
                             availableWidthTwips))
                {
                    body.Append(element);
                }
            }
        }

        body.Append(finalSection);
        ConfigureHeadersAndFooters(
            main,
            body.Descendants<W.SectionProperties>().ToArray(),
            definition.HeaderFooter,
            definition.Locale,
            templateHeaderFooter);
        output.PackageProperties.Title = definition.Title;
        output.PackageProperties.Subject = definition.Subject;
        output.PackageProperties.Description = definition.Purpose;
        output.PackageProperties.Creator = null;
        output.PackageProperties.LastModifiedBy = null;
        output.PackageProperties.Keywords = null;
        output.PackageProperties.Category = null;
        main.Document.Save();
    }

    private static void ValidateDefinition(DocumentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Title)
            || string.IsNullOrWhiteSpace(definition.Locale)
            || definition.Sections.Count != definition.ExpectedSectionCount
            || definition.Sections.Count is < 1 or > 50)
        {
            throw new WordMcpException(
                "incomplete_document_definition",
                "$.document",
                "The declarative document definition is incomplete or inconsistent.",
                "Finish the staged draft with the expected number of bounded logical sections.");
        }
    }

    private GenerationImageBudget ValidateImages(
        DocumentDefinition definition,
        IReadOnlyDictionary<string, WordImageAsset> images)
    {
        var requested = definition.Sections
            .SelectMany(section => section.Blocks)
            .Where(block => block.Kind == DocumentBlockKind.Image)
            .Select(block => block.ImageFileId!)
            .ToArray();
        if (requested.Length > options.MaxImages)
        {
            throw InvalidImage("The document contains too many image blocks.");
        }

        long totalBytes = 0;
        long totalPixels = 0;
        foreach (var fileId in requested.Distinct(StringComparer.Ordinal))
        {
            if (!images.TryGetValue(fileId, out var asset) || !string.Equals(asset.FileId, fileId, StringComparison.Ordinal))
            {
                throw InvalidImage("An opaque image snapshot is missing for an image block.");
            }

            if (asset.Bytes.LongLength > options.MaxImageBytes)
            {
                throw InvalidImage("An image exceeds the per-image byte limit.");
            }

            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(asset.Bytes));
            if (!string.Equals(actualSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidImage("An image snapshot does not match its SHA-256 binding.");
            }

            var dimensions = ImageDimensions.Read(asset.Bytes, asset.MediaType);
            var pixels = checked((long)dimensions.Width * dimensions.Height);
            if (dimensions.Width > options.MaxImageDimension
                || dimensions.Height > options.MaxImageDimension
                || pixels > options.MaxImagePixels)
            {
                throw InvalidImage("An image exceeds the supported dimension or pixel limit.");
            }

            totalBytes = checked(totalBytes + asset.Bytes.LongLength);
            totalPixels = checked(totalPixels + pixels);
        }

        if (totalBytes > options.MaxTotalImageBytes || totalPixels > options.MaxTotalImagePixels)
        {
            throw InvalidImage("The document exceeds the aggregate image limit.");
        }

        return new GenerationImageBudget(
            requested.Distinct(StringComparer.Ordinal).Count(),
            totalBytes,
            totalPixels);
    }

    private TemplateHeaderFooterDesign CopyTemplateDesign(
        MainDocumentPart source,
        MainDocumentPart destination,
        IReadOnlyList<W.SectionProperties> sourceSections,
        GenerationImageBudget imageBudget,
        CancellationToken cancellationToken)
    {
        RejectRelationshipBearingDesignPart(source.StyleDefinitionsPart, "styles");
        RejectRelationshipBearingDesignPart(source.ThemePart, "theme");
        RejectRelationshipBearingDesignPart(source.NumberingDefinitionsPart, "numbering");

        if (source.StyleDefinitionsPart?.Styles is { } styles)
        {
            var target = destination.AddNewPart<StyleDefinitionsPart>();
            target.Styles = (W.Styles)styles.CloneNode(true);
        }

        if (source.ThemePart is { } themePart)
        {
            var target = destination.AddNewPart<ThemePart>();
            using var input = themePart.GetStream(FileMode.Open, FileAccess.Read);
            target.FeedData(input);
        }

        if (source.NumberingDefinitionsPart?.Numbering is { } numbering)
        {
            var target = destination.AddNewPart<NumberingDefinitionsPart>();
            target.Numbering = (W.Numbering)numbering.CloneNode(true);
        }

        return CopyTemplateHeadersAndFooters(
            source,
            destination,
            sourceSections,
            imageBudget,
            cancellationToken);
    }

    private static void RejectRelationshipBearingDesignPart(OpenXmlPart? part, string partKind)
    {
        if (part is null)
        {
            return;
        }

        if (part.Parts.Any()
            || part.ExternalRelationships.Any()
            || part.HyperlinkRelationships.Any()
            || part.DataPartReferenceRelationships.Any())
        {
            throw InvalidTemplate(
                $"The template {partKind} part contains relationships that cannot be copied safely.");
        }
    }

    private TemplateHeaderFooterDesign CopyTemplateHeadersAndFooters(
        MainDocumentPart source,
        MainDocumentPart destination,
        IReadOnlyList<W.SectionProperties> sourceSections,
        GenerationImageBudget imageBudget,
        CancellationToken cancellationToken)
    {
        var sourceHeaders = ResolveEffectiveHeaderReferences(sourceSections);
        var sourceFooters = ResolveEffectiveFooterReferences(sourceSections);
        var headers = new Dictionary<W.HeaderFooterValues, string>();
        var footers = new Dictionary<W.HeaderFooterValues, string>();
        var headerParts = new Dictionary<Uri, HeaderPart>();
        var footerParts = new Dictionary<Uri, FooterPart>();
        var media = new TemplateMediaCopyContext(options, imageBudget);
        var finalSection = sourceSections.Count == 0 ? null : sourceSections[^1];
        var differentFirstPage = finalSection?.GetFirstChild<W.TitlePage>() is not null;
        var differentEvenOdd = source.DocumentSettingsPart?.Settings?.GetFirstChild<W.EvenAndOddHeaders>() is not null;

        foreach (var (type, relationshipId) in sourceHeaders)
        {
            if ((type == W.HeaderFooterValues.First && !differentFirstPage)
                || (type == W.HeaderFooterValues.Even && !differentEvenOdd))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!source.TryGetPartById(relationshipId, out var sourcePart) || sourcePart is not HeaderPart sourceHeader)
            {
                throw InvalidTemplate("A template header reference does not resolve to a header part.");
            }

            if (!headerParts.TryGetValue(sourceHeader.Uri, out var targetHeader))
            {
                targetHeader = destination.AddNewPart<HeaderPart>();
                var root = sourceHeader.Header is { } sourceRoot
                    ? (W.Header)sourceRoot.CloneNode(true)
                    : throw InvalidTemplate("A referenced template header has no document root.");
                SanitizeTemplateStory(sourceHeader, targetHeader, root, media, cancellationToken);
                targetHeader.Header = root;
                targetHeader.Header.Save();
                headerParts.Add(sourceHeader.Uri, targetHeader);
                ReserveTemplateIdentifiers(root);
            }

            headers.Add(type, destination.GetIdOfPart(targetHeader));
        }

        foreach (var (type, relationshipId) in sourceFooters)
        {
            if ((type == W.HeaderFooterValues.First && !differentFirstPage)
                || (type == W.HeaderFooterValues.Even && !differentEvenOdd))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!source.TryGetPartById(relationshipId, out var sourcePart) || sourcePart is not FooterPart sourceFooter)
            {
                throw InvalidTemplate("A template footer reference does not resolve to a footer part.");
            }

            if (!footerParts.TryGetValue(sourceFooter.Uri, out var targetFooter))
            {
                targetFooter = destination.AddNewPart<FooterPart>();
                var root = sourceFooter.Footer is { } sourceRoot
                    ? (W.Footer)sourceRoot.CloneNode(true)
                    : throw InvalidTemplate("A referenced template footer has no document root.");
                SanitizeTemplateStory(sourceFooter, targetFooter, root, media, cancellationToken);
                targetFooter.Footer = root;
                targetFooter.Footer.Save();
                footerParts.Add(sourceFooter.Uri, targetFooter);
                ReserveTemplateIdentifiers(root);
            }

            footers.Add(type, destination.GetIdOfPart(targetFooter));
        }

        return new TemplateHeaderFooterDesign(
            headers,
            footers,
            differentFirstPage,
            differentEvenOdd);
    }

    private static Dictionary<W.HeaderFooterValues, string> ResolveEffectiveHeaderReferences(
        IReadOnlyList<W.SectionProperties> sections)
    {
        var result = new Dictionary<W.HeaderFooterValues, string>();
        foreach (var type in HeaderFooterTypes())
        {
            for (var sectionIndex = sections.Count - 1; sectionIndex >= 0; sectionIndex--)
            {
                var matches = sections[sectionIndex].Elements<W.HeaderReference>()
                    .Where(reference => reference.Type?.Value == type)
                    .ToArray();
                if (matches.Length > 1)
                {
                    throw InvalidTemplate("A template section contains duplicate header reference types.");
                }

                if (matches.Length == 0)
                {
                    continue;
                }

                var relationshipId = matches[0].Id?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId))
                {
                    throw InvalidTemplate("A template header reference has no relationship ID.");
                }

                result.Add(type, relationshipId);
                break;
            }
        }

        return result;
    }

    private static Dictionary<W.HeaderFooterValues, string> ResolveEffectiveFooterReferences(
        IReadOnlyList<W.SectionProperties> sections)
    {
        var result = new Dictionary<W.HeaderFooterValues, string>();
        foreach (var type in HeaderFooterTypes())
        {
            for (var sectionIndex = sections.Count - 1; sectionIndex >= 0; sectionIndex--)
            {
                var matches = sections[sectionIndex].Elements<W.FooterReference>()
                    .Where(reference => reference.Type?.Value == type)
                    .ToArray();
                if (matches.Length > 1)
                {
                    throw InvalidTemplate("A template section contains duplicate footer reference types.");
                }

                if (matches.Length == 0)
                {
                    continue;
                }

                var relationshipId = matches[0].Id?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId))
                {
                    throw InvalidTemplate("A template footer reference has no relationship ID.");
                }

                result.Add(type, relationshipId);
                break;
            }
        }

        return result;
    }

    private static IEnumerable<W.HeaderFooterValues> HeaderFooterTypes()
    {
        yield return W.HeaderFooterValues.Default;
        yield return W.HeaderFooterValues.First;
        yield return W.HeaderFooterValues.Even;
    }

    private static void SanitizeTemplateStory(
        OpenXmlPart source,
        OpenXmlPart destination,
        OpenXmlPartRootElement root,
        TemplateMediaCopyContext media,
        CancellationToken cancellationToken)
    {
        RemoveNonInheritedTemplateContent(root);

        const string relationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        foreach (var drawing in root.Descendants<W.Drawing>().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relationshipAttributes = drawing.Descendants()
                .SelectMany(element => element.GetAttributes().Select(attribute => (Element: element, Attribute: attribute)))
                .Where(item => string.Equals(item.Attribute.NamespaceUri, relationshipsNamespace, StringComparison.Ordinal))
                .ToArray();
            var blips = drawing.Descendants<A.Blip>().ToArray();
            if (blips.Length == 0
                || blips.Any(blip => string.IsNullOrWhiteSpace(blip.Embed?.Value) || blip.Link is not null)
                || relationshipAttributes.Any(item => item.Element is not A.Blip || item.Attribute.LocalName != "embed"))
            {
                drawing.Remove();
            }
        }

        foreach (var blip in root.Descendants<A.Blip>().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blip.Parent is null)
            {
                continue;
            }

            var sourceRelationshipId = blip.Embed?.Value
                                       ?? throw InvalidTemplate("A template drawing has no embedded image relationship.");
            blip.Embed = media.CopyImage(
                source,
                destination,
                sourceRelationshipId,
                cancellationToken);
            blip.Link = null;
        }

        foreach (var element in root.Descendants().ToArray())
        {
            if (element.Parent is null)
            {
                continue;
            }

            var unsupportedRelationship = element.GetAttributes().FirstOrDefault(attribute =>
                string.Equals(attribute.NamespaceUri, relationshipsNamespace, StringComparison.Ordinal)
                && !(element is A.Blip && attribute.LocalName == "embed"));
            if (string.IsNullOrEmpty(unsupportedRelationship.LocalName))
            {
                continue;
            }

            if (element is W.Hyperlink hyperlink)
            {
                Unwrap(hyperlink);
                continue;
            }

            var containingDrawing = element.Ancestors<W.Drawing>().FirstOrDefault();
            (containingDrawing ?? element).Remove();
        }
    }

    private static void RemoveNonInheritedTemplateContent(OpenXmlElement root)
    {
        foreach (var run in root.Descendants<W.Run>().Where(run =>
                     run.RunProperties?.GetFirstChild<W.Vanish>() is not null
                     || run.RunProperties?.GetFirstChild<W.WebHidden>() is not null).ToArray())
        {
            run.Remove();
        }

        foreach (var element in root.Descendants().Reverse().ToArray())
        {
            if (element.Parent is null)
            {
                continue;
            }

            if (element.LocalName is "oMath" or "oMathPara")
            {
                element.Remove();
            }
            else if (!string.Equals(
                         element.NamespaceUri,
                         "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
                         StringComparison.Ordinal))
            {
                continue;
            }
            else if (element.LocalName is "ins" or "moveTo")
            {
                Unwrap(element);
            }
            else if (element.LocalName is
                     "del" or "moveFrom" or "customXml" or "sdt" or "altChunk" or "object" or "pict"
                     or "commentRangeStart" or "commentRangeEnd" or "commentReference"
                     or "moveFromRangeStart" or "moveFromRangeEnd" or "moveToRangeStart" or "moveToRangeEnd"
                     or "permStart" or "permEnd" or "rPrChange" or "pPrChange" or "tblPrChange"
                     or "tblGridChange" or "trPrChange" or "tcPrChange" or "sectPrChange")
            {
                element.Remove();
            }
        }
    }

    private static void Unwrap(OpenXmlElement element)
    {
        if (element.Parent is null)
        {
            return;
        }

        foreach (var child in element.ChildElements.ToArray())
        {
            child.Remove();
            element.InsertBeforeSelf(child);
        }

        element.Remove();
    }

    private void ReserveTemplateIdentifiers(OpenXmlElement root)
    {
        var maximumDrawingId = root.Descendants<DW.DocProperties>()
            .Select(properties => properties.Id?.Value ?? 0)
            .DefaultIfEmpty(0U)
            .Max();
        if (maximumDrawingId == uint.MaxValue)
        {
            throw InvalidTemplate("A template drawing ID leaves no safe ID for generated content.");
        }

        nextDrawingId = Math.Max(nextDrawingId, maximumDrawingId + 1);
        foreach (var bookmark in root.Descendants<W.BookmarkStart>())
        {
            if (!string.IsNullOrWhiteSpace(bookmark.Name?.Value))
            {
                bookmarkNames.Add(bookmark.Name.Value);
            }

            if (!int.TryParse(bookmark.Id?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id < 0)
            {
                continue;
            }

            if (id == int.MaxValue)
            {
                throw InvalidTemplate("A template bookmark ID leaves no safe ID for generated content.");
            }

            nextBookmarkId = Math.Max(nextBookmarkId, id + 1);
        }
    }

    private static W.SectionProperties? SanitizeSectionProperties(W.SectionProperties? source)
    {
        if (source is null)
        {
            return null;
        }

        var result = new W.SectionProperties();
        foreach (var child in source.ChildElements)
        {
            if (child is W.PageSize or W.PageMargin or W.Columns or W.SectionType or W.PageNumberType or W.TitlePage)
            {
                result.Append(child.CloneNode(true));
            }
        }

        return result;
    }

    private static void EnsureSettings(
        MainDocumentPart main,
        DocumentDefinition definition,
        bool differentEvenOdd)
    {
        var part = main.DocumentSettingsPart ?? main.AddNewPart<DocumentSettingsPart>();
        part.Settings = new W.Settings(new W.DefaultTabStop { Val = 720 });
        if (differentEvenOdd)
        {
            part.Settings.Append(new W.EvenAndOddHeaders());
        }

        part.Settings.Append(
            new W.CharacterSpacingControl { Val = W.CharacterSpacingValues.DoNotCompress });
    }

    private static void EnsureStyles(MainDocumentPart main, DocumentDefinition definition)
    {
        var part = main.StyleDefinitionsPart ?? main.AddNewPart<StyleDefinitionsPart>();
        part.Styles ??= new W.Styles();
        if (part.Styles.DocDefaults is null)
        {
            part.Styles.PrependChild(new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                    Fonts(definition.Theme.BodyFont),
                    Languages(definition.Locale))),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                    new W.WidowControl(),
                    new W.Kinsoku(),
                    new W.AutoSpaceDE(),
                    new W.AutoSpaceDN()))));
        }

        AddStyleIfMissing(part.Styles, ParagraphStyle("Normal", "Normal", definition.Theme.BodyFont, 22, null));
        AddStyleIfMissing(part.Styles, ParagraphStyle("Title", "Title", definition.Theme.HeadingFont, 40, "Normal", bold: true, keepNext: true));
        AddStyleIfMissing(part.Styles, ParagraphStyle("Subtitle", "Subtitle", definition.Theme.BodyFont, 24, "Normal"));
        for (var level = 1; level <= 4; level++)
        {
            AddStyleIfMissing(part.Styles, HeadingStyle(level, definition));
        }

        AddStyleIfMissing(part.Styles, ParagraphStyle(
            "TOCHeading",
            "TOC Heading",
            definition.Theme.HeadingFont,
            28,
            "Normal",
            bold: true,
            keepNext: true));
        AddStyleIfMissing(part.Styles, ParagraphStyle("Caption", "Caption", definition.Theme.BodyFont, 18, "Normal", italic: true));
        AddStyleIfMissing(part.Styles, ParagraphStyle("Quote", "Quote", definition.Theme.BodyFont, 20, "Normal", italic: true));
        AddStyleIfMissing(part.Styles, ParagraphStyle("Callout", "Callout", definition.Theme.BodyFont, 20, "Normal", bold: true));
        AddStyleIfMissing(part.Styles, CharacterStyle("CodeChar", "Code Char", definition.Theme.CodeFont));
        part.Styles.Save();
    }

    private static W.Style ParagraphStyle(
        string id,
        string name,
        string font,
        int halfPoints,
        string? basedOn,
        bool bold = false,
        bool italic = false,
        bool keepNext = false)
    {
        var style = new W.Style { Type = W.StyleValues.Paragraph, StyleId = id, CustomStyle = id is not ("Normal" or "Title" or "Subtitle" or "Caption") };
        style.Append(new W.StyleName { Val = name });
        if (basedOn is not null)
        {
            style.Append(new W.BasedOn { Val = basedOn }, new W.NextParagraphStyle { Val = "Normal" });
        }

        style.Append(new W.PrimaryStyle());
        var paragraphProperties = new W.StyleParagraphProperties();
        if (keepNext)
        {
            paragraphProperties.Append(new W.KeepNext());
        }

        paragraphProperties.Append(
            new W.KeepLines(),
            new W.WidowControl(),
            new W.SpacingBetweenLines { After = "160", Line = "360", LineRule = W.LineSpacingRuleValues.Auto });
        style.Append(paragraphProperties);
        var runProperties = new W.StyleRunProperties(Fonts(font));
        if (bold)
        {
            runProperties.Append(new W.Bold(), new W.BoldComplexScript());
        }

        if (italic)
        {
            runProperties.Append(new W.Italic(), new W.ItalicComplexScript());
        }

        runProperties.Append(
            new W.FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) },
            new W.FontSizeComplexScript { Val = halfPoints.ToString(CultureInfo.InvariantCulture) },
            Languages("ja-JP"));

        style.Append(runProperties);
        return style;
    }

    private static W.Style HeadingStyle(int level, DocumentDefinition definition)
    {
        var sizes = new[] { 32, 28, 24, 22 };
        var style = ParagraphStyle(
            $"Heading{level}",
            $"heading {level}",
            definition.Theme.HeadingFont,
            sizes[level - 1],
            "Normal",
            bold: true,
            keepNext: true);
        var spacing = style.StyleParagraphProperties!.GetFirstChild<W.SpacingBetweenLines>()!;
        spacing.Before = level == 1 ? "360" : "240";
        spacing.After = "120";
        style.StyleParagraphProperties.Append(new W.OutlineLevel { Val = level - 1 });
        var firstSize = style.StyleRunProperties!.GetFirstChild<W.FontSize>();
        style.StyleRunProperties.InsertBefore(new W.Color { Val = definition.Theme.Accent }, firstSize);
        return style;
    }

    private static W.Style CharacterStyle(string id, string name, string font) => new(
        new W.StyleName { Val = name },
        new W.BasedOn { Val = "DefaultParagraphFont" },
        new W.StyleRunProperties(
            Fonts(font),
            new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F2F2F2" }))
    {
        Type = W.StyleValues.Character,
        StyleId = id,
        CustomStyle = true,
    };

    private static W.RunFonts Fonts(string font) => new()
    {
        Ascii = font,
        HighAnsi = font,
        EastAsia = font,
        ComplexScript = font,
    };

    private static W.Languages Languages(string locale) => new()
    {
        Val = locale,
        EastAsia = locale,
        Bidi = locale,
    };

    private static void AddStyleIfMissing(W.Styles styles, W.Style style)
    {
        if (!styles.Elements<W.Style>().Any(existing => string.Equals(existing.StyleId?.Value, style.StyleId?.Value, StringComparison.Ordinal)))
        {
            styles.Append(style);
        }
    }

    private static void EnsureTheme(MainDocumentPart main, DocumentDefinition definition)
    {
        if (main.ThemePart is not null)
        {
            return;
        }

        var part = main.AddNewPart<ThemePart>();
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false };
        using var writer = XmlWriter.Create(stream, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("a", "theme", "http://schemas.openxmlformats.org/drawingml/2006/main");
        writer.WriteAttributeString("name", "WordMcp Theme");
        writer.WriteStartElement("a", "themeElements", null);
        WriteColorScheme(writer, definition.Theme.Accent);
        WriteFontScheme(writer, definition.Theme);
        WriteFormatScheme(writer);
        writer.WriteEndElement();
        writer.WriteStartElement("a", "objectDefaults", null);
        writer.WriteEndElement();
        writer.WriteStartElement("a", "extraClrSchemeLst", null);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteColorScheme(XmlWriter writer, string accent)
    {
        writer.WriteStartElement("a", "clrScheme", null);
        writer.WriteAttributeString("name", "WordMcp");
        foreach (var (name, value, system) in new[]
                 {
                     ("dk1", "000000", true), ("lt1", "FFFFFF", true),
                     ("dk2", "1F1F1F", false), ("lt2", "F2F2F2", false),
                     ("accent1", accent, false), ("accent2", "70AD47", false),
                     ("accent3", "A5A5A5", false), ("accent4", "FFC000", false),
                     ("accent5", "5B9BD5", false), ("accent6", "ED7D31", false),
                     ("hlink", "0563C1", false), ("folHlink", "954F72", false),
                 })
        {
            writer.WriteStartElement("a", name, null);
            writer.WriteStartElement("a", system ? "sysClr" : "srgbClr", null);
            if (system)
            {
                writer.WriteAttributeString("val", name == "dk1" ? "windowText" : "window");
                writer.WriteAttributeString("lastClr", value);
            }
            else
            {
                writer.WriteAttributeString("val", value);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteFontScheme(XmlWriter writer, DocumentThemeSpec theme)
    {
        writer.WriteStartElement("a", "fontScheme", null);
        writer.WriteAttributeString("name", "WordMcp");
        WriteFontCollection(writer, "majorFont", theme.HeadingFont);
        WriteFontCollection(writer, "minorFont", theme.BodyFont);
        writer.WriteEndElement();
    }

    private static void WriteFontCollection(XmlWriter writer, string name, string font)
    {
        writer.WriteStartElement("a", name, null);
        foreach (var child in new[] { "latin", "ea", "cs" })
        {
            writer.WriteStartElement("a", child, null);
            writer.WriteAttributeString("typeface", font);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteFormatScheme(XmlWriter writer)
    {
        writer.WriteStartElement("a", "fmtScheme", null);
        writer.WriteAttributeString("name", "WordMcp");
        writer.WriteStartElement("a", "fillStyleLst", null);
        WriteSolidPlaceholderFill(writer);
        WriteSolidPlaceholderFill(writer);
        WriteSolidPlaceholderFill(writer);
        writer.WriteEndElement();
        writer.WriteStartElement("a", "lnStyleLst", null);
        for (var index = 0; index < 3; index++)
        {
            writer.WriteStartElement("a", "ln", null);
            writer.WriteAttributeString("w", "9525");
            WriteSolidPlaceholderFill(writer);
            writer.WriteStartElement("a", "prstDash", null);
            writer.WriteAttributeString("val", "solid");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("a", "effectStyleLst", null);
        for (var index = 0; index < 3; index++)
        {
            writer.WriteStartElement("a", "effectStyle", null);
            writer.WriteStartElement("a", "effectLst", null);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("a", "bgFillStyleLst", null);
        WriteSolidPlaceholderFill(writer);
        WriteSolidPlaceholderFill(writer);
        WriteSolidPlaceholderFill(writer);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteSolidPlaceholderFill(XmlWriter writer)
    {
        writer.WriteStartElement("a", "solidFill", null);
        writer.WriteStartElement("a", "schemeClr", null);
        writer.WriteAttributeString("val", "phClr");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static GenerationNumberingIds EnsureNumbering(MainDocumentPart main)
    {
        var part = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering ??= new W.Numbering();
        var maxAbstract = part.Numbering.Elements<W.AbstractNum>()
            .Select(value => value.AbstractNumberId?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var maxNumber = part.Numbering.Elements<W.NumberingInstance>()
            .Select(value => value.NumberID?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var unorderedAbstract = maxAbstract + 1;
        var orderedAbstract = maxAbstract + 2;
        var unordered = maxNumber + 1;
        var ordered = maxNumber + 2;
        part.Numbering.Append(
            WordOpenXmlFactory.CreateAbstractNumbering(unorderedAbstract, ordered: false),
            WordOpenXmlFactory.CreateAbstractNumbering(orderedAbstract, ordered: true),
            new W.NumberingInstance(new W.AbstractNumId { Val = unorderedAbstract }) { NumberID = unordered },
            new W.NumberingInstance(new W.AbstractNumId { Val = orderedAbstract }) { NumberID = ordered });
        part.Numbering.Save();
        return new GenerationNumberingIds(unordered, ordered);
    }

    private W.Paragraph CreateSectionHeading(LogicalSectionSpec section)
    {
        var paragraph = CreateStyledParagraph("Heading1", [new SemanticRun(section.Title)], keepNext: true);
        var id = nextBookmarkId++.ToString(CultureInfo.InvariantCulture);
        var baseName = string.Concat("wmsec_", section.SectionKey).Replace('-', '_');
        var name = baseName;
        while (!bookmarkNames.Add(name))
        {
            name = string.Concat(baseName, "_", nextBookmarkId.ToString(CultureInfo.InvariantCulture));
            nextBookmarkId++;
        }

        paragraph.InsertAt(new W.BookmarkStart { Id = id, Name = name }, 1);
        paragraph.Append(new W.BookmarkEnd { Id = id });
        return paragraph;
    }

    private IEnumerable<OpenXmlElement> CreateBlock(
        MainDocumentPart main,
        DocumentBlock block,
        DocumentDefinition definition,
        GenerationNumberingIds numbering,
        IReadOnlyDictionary<string, WordImageAsset> images,
        int availableWidthTwips)
    {
        switch (block.Kind)
        {
            case DocumentBlockKind.Heading:
                yield return CreateStyledParagraph($"Heading{block.Level ?? 1}", Runs(block), keepNext: true);
                break;
            case DocumentBlockKind.Paragraph:
                yield return CreateStyledParagraph("Normal", Runs(block), keepLines: true);
                break;
            case DocumentBlockKind.Callout:
                yield return CreateStyledParagraph("Callout", Runs(block), keepLines: true);
                break;
            case DocumentBlockKind.Quote:
                yield return CreateStyledParagraph("Quote", Runs(block), keepLines: true);
                break;
            case DocumentBlockKind.Caption:
                yield return CreateStyledParagraph("Caption", Runs(block), keepLines: true);
                break;
            case DocumentBlockKind.UnorderedList:
            case DocumentBlockKind.OrderedList:
                foreach (var item in block.Items ?? throw InvalidBlock(block.Kind))
                {
                    var paragraph = CreateStyledParagraph("Normal", item.Runs, keepLines: true);
                    paragraph.ParagraphProperties!.Append(new W.NumberingProperties(
                        new W.NumberingLevelReference { Val = item.Level },
                        new W.NumberingId
                        {
                            Val = block.Kind == DocumentBlockKind.OrderedList ? numbering.Ordered : numbering.Unordered,
                        }));
                    yield return paragraph;
                }

                break;
            case DocumentBlockKind.Table:
                if (!string.IsNullOrWhiteSpace(block.Table?.Caption))
                {
                    yield return CreateStyledParagraph("Caption", [new SemanticRun(block.Table.Caption)], keepNext: true);
                }

                yield return WordOpenXmlFactory.CreateTable(
                    block.Table ?? throw InvalidBlock(block.Kind),
                    availableWidthTwips);
                break;
            case DocumentBlockKind.KeyValue:
                yield return WordOpenXmlFactory.CreateKeyValueTable(
                    block.KeyValues ?? throw InvalidBlock(block.Kind),
                    availableWidthTwips);
                break;
            case DocumentBlockKind.Image:
                if (!images.TryGetValue(block.ImageFileId!, out var asset))
                {
                    throw InvalidImage("An image block has no corresponding immutable image asset.");
                }

                yield return CreateImageParagraph(main, asset, block.AltText!, availableWidthTwips);
                if (!string.IsNullOrWhiteSpace(block.Caption))
                {
                    yield return CreateStyledParagraph("Caption", [new SemanticRun(block.Caption)], keepNext: true);
                }

                break;
            case DocumentBlockKind.PageBreak:
                yield return new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page }));
                break;
            case DocumentBlockKind.SectionBreak:
                yield return CreateSectionBreak(block.SectionBreakKind ?? SectionBreakKind.NextPage, definition.Layout);
                break;
            default:
                throw InvalidBlock(block.Kind);
        }
    }

    private static W.Paragraph CreateStyledParagraph(
        string styleId,
        IReadOnlyList<SemanticRun> runs,
        bool keepNext = false,
        bool keepLines = false) =>
        WordOpenXmlFactory.CreateParagraph(runs, styleId, keepNext, keepLines);

    private static W.Paragraph CreateTocParagraph()
    {
        var begin = new W.FieldChar { FieldCharType = W.FieldCharValues.Begin, Dirty = true };
        var instruction = new W.FieldCode(" TOC \\o \"1-4\" \\h \\z \\u ") { Space = SpaceProcessingModeValues.Preserve };
        var separate = new W.FieldChar { FieldCharType = W.FieldCharValues.Separate };
        var end = new W.FieldChar { FieldCharType = W.FieldCharValues.End };
        return new W.Paragraph(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Normal" }),
            new W.Run(begin),
            new W.Run(instruction),
            new W.Run(separate),
            new W.Run(new W.Text("目次を更新してください")),
            new W.Run(end));
    }

    private W.Paragraph CreateImageParagraph(
        MainDocumentPart main,
        WordImageAsset asset,
        string altText,
        int availableWidthTwips)
    {
        var dimensions = ImageDimensions.Read(asset.Bytes, asset.MediaType);
        var imageType = asset.MediaType switch
        {
            "image/png" => ImagePartType.Png,
            "image/jpeg" => ImagePartType.Jpeg,
            _ => throw InvalidImage("Only embedded PNG and JPEG images are supported."),
        };
        var imagePart = main.AddImagePart(imageType);
        using (var stream = new MemoryStream(asset.Bytes, writable: false))
        {
            imagePart.FeedData(stream);
        }

        var relationshipId = main.GetIdOfPart(imagePart);
        var maximumWidth = Math.Min(6L * EmusPerInch, availableWidthTwips * EmusPerInch / 1_440L);
        var width = Math.Min(maximumWidth, dimensions.Width * EmusPerInch / 96L);
        var height = Math.Max(1, width * dimensions.Height / dimensions.Width);
        var id = nextDrawingId++;
        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = width, Cy = height },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = id, Name = $"Image {id}", Description = altText },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"image-{id}" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0, Y = 0 },
                                    new A.Extents { Cx = width, Cy = height }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
            });
        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.ParagraphStyleId { Val = "Normal" },
                new W.Justification { Val = W.JustificationValues.Center }),
            new W.Run(drawing));
    }

    private static W.SectionProperties CreateLayoutSection(DocumentLayoutSpec layout)
    {
        var (width, height) = layout.PageSize == PageSizeKind.Letter
            ? (12_240U, 15_840U)
            : (11_906U, 16_838U);
        if (layout.Orientation == PageOrientationKind.Landscape)
        {
            (width, height) = (height, width);
        }

        return new W.SectionProperties(
            new W.PageSize
            {
                Width = width,
                Height = height,
                Orient = layout.Orientation == PageOrientationKind.Landscape ? W.PageOrientationValues.Landscape : null,
            },
            new W.PageMargin
            {
                Top = MillimetersToTwips(layout.MarginTopMm),
                Right = (UInt32Value)(uint)MillimetersToTwips(layout.MarginRightMm),
                Bottom = MillimetersToTwips(layout.MarginBottomMm),
                Left = (UInt32Value)(uint)MillimetersToTwips(layout.MarginLeftMm),
                Header = 720U,
                Footer = 720U,
                Gutter = 0U,
            },
            new W.Columns { ColumnCount = (Int16Value)(short)layout.Columns, Space = "720" },
            new W.DocGrid { Type = W.DocGridValues.Lines, LinePitch = 312 });
    }

    private static W.Paragraph CreateSectionBreak(SectionBreakKind kind, DocumentLayoutSpec layout)
    {
        var section = CreateLayoutSection(layout);
        section.PrependChild(new W.SectionType
        {
            Val = kind switch
            {
                SectionBreakKind.Continuous => W.SectionMarkValues.Continuous,
                SectionBreakKind.EvenPage => W.SectionMarkValues.EvenPage,
                SectionBreakKind.OddPage => W.SectionMarkValues.OddPage,
                _ => W.SectionMarkValues.NextPage,
            },
        });
        return new W.Paragraph(new W.ParagraphProperties(section));
    }

    private static int MillimetersToTwips(decimal millimeters) =>
        decimal.ToInt32(decimal.Round(millimeters * 1440m / 25.4m, 0, MidpointRounding.AwayFromZero));

    private static void ConfigureHeadersAndFooters(
        MainDocumentPart main,
        W.SectionProperties[] sections,
        HeaderFooterPolicy policy,
        string locale,
        TemplateHeaderFooterDesign? template)
    {
        if (sections.Length == 0)
        {
            throw new InvalidOperationException("A generated Word document requires section properties.");
        }

        var templateOwnsPolicy = template?.HasParts == true;
        var differentFirstPage = templateOwnsPolicy
            ? template!.DifferentFirstPage
            : policy.DifferentFirstPage;
        var differentEvenOdd = templateOwnsPolicy
            ? template!.DifferentEvenOdd
            : policy.DifferentEvenOdd;
        var headerRelationshipIds = template is null
            ? new Dictionary<W.HeaderFooterValues, string>()
            : new Dictionary<W.HeaderFooterValues, string>(template.Headers);
        var footerRelationshipIds = template is null
            ? new Dictionary<W.HeaderFooterValues, string>()
            : new Dictionary<W.HeaderFooterValues, string>(template.Footers);
        if (!headerRelationshipIds.ContainsKey(W.HeaderFooterValues.Default))
        {
            headerRelationshipIds.Add(
                W.HeaderFooterValues.Default,
                AddGeneratedHeader(main, policy.HeaderText, locale));
        }

        if (!footerRelationshipIds.ContainsKey(W.HeaderFooterValues.Default))
        {
            footerRelationshipIds.Add(
                W.HeaderFooterValues.Default,
                AddGeneratedFooter(main, policy.FooterText, policy.PageNumbers, locale));
        }

        if (differentFirstPage)
        {
            if (!headerRelationshipIds.ContainsKey(W.HeaderFooterValues.First))
            {
                headerRelationshipIds.Add(
                    W.HeaderFooterValues.First,
                    AddGeneratedHeader(main, text: null, locale: locale));
            }

            if (!footerRelationshipIds.ContainsKey(W.HeaderFooterValues.First))
            {
                footerRelationshipIds.Add(
                    W.HeaderFooterValues.First,
                    AddGeneratedFooter(main, text: null, pageNumbers: false, locale: locale));
            }
        }

        if (differentEvenOdd)
        {
            if (!headerRelationshipIds.ContainsKey(W.HeaderFooterValues.Even))
            {
                headerRelationshipIds.Add(
                    W.HeaderFooterValues.Even,
                    AddGeneratedHeader(main, policy.HeaderText, locale));
            }

            if (!footerRelationshipIds.ContainsKey(W.HeaderFooterValues.Even))
            {
                footerRelationshipIds.Add(
                    W.HeaderFooterValues.Even,
                    AddGeneratedFooter(main, policy.FooterText, policy.PageNumbers, locale));
            }
        }

        var headerReferences = headerRelationshipIds.Select(item => new W.HeaderReference
        {
            Type = item.Key,
            Id = item.Value,
        }).ToArray();
        var footerReferences = footerRelationshipIds.Select(item => new W.FooterReference
        {
            Type = item.Key,
            Id = item.Value,
        }).ToArray();

        foreach (var section in sections)
        {
            foreach (var existing in section.Elements<W.HeaderReference>().Cast<OpenXmlElement>()
                         .Concat(section.Elements<W.FooterReference>()).ToArray())
            {
                existing.Remove();
            }

            section.GetFirstChild<W.TitlePage>()?.Remove();
            if (differentFirstPage)
            {
                var titlePage = new W.TitlePage();
                var docGrid = section.GetFirstChild<W.DocGrid>();
                if (docGrid is null)
                {
                    section.Append(titlePage);
                }
                else
                {
                    section.InsertBefore(titlePage, docGrid);
                }
            }

            foreach (var reference in headerReferences.Cast<OpenXmlElement>()
                         .Concat(footerReferences)
                         .Select(reference => reference.CloneNode(true))
                         .Reverse())
            {
                section.PrependChild(reference);
            }
        }
    }

    private static string AddGeneratedHeader(MainDocumentPart main, string? text, string locale)
    {
        var header = main.AddNewPart<HeaderPart>();
        header.Header = new W.Header(CreateHeaderFooterParagraph(text, pageNumbers: false, locale));
        return main.GetIdOfPart(header);
    }

    private static string AddGeneratedFooter(
        MainDocumentPart main,
        string? text,
        bool pageNumbers,
        string locale)
    {
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new W.Footer(CreateHeaderFooterParagraph(text, pageNumbers, locale));
        return main.GetIdOfPart(footer);
    }

    private static W.Paragraph CreateHeaderFooterParagraph(string? text, bool pageNumbers, string locale)
    {
        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Normal" }, new W.Justification { Val = W.JustificationValues.Center }));
        if (!string.IsNullOrWhiteSpace(text))
        {
            paragraph.Append(new W.Run(new W.RunProperties(Languages(locale)), new W.Text(text)));
        }

        if (pageNumbers)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                paragraph.Append(new W.Run(new W.Text("  ") { Space = SpaceProcessingModeValues.Preserve }));
            }

            AppendField(paragraph, " PAGE ", "1");
            paragraph.Append(new W.Run(new W.Text(" / ")));
            AppendField(paragraph, " NUMPAGES ", "1");
        }

        return paragraph;
    }

    private static void AppendField(W.Paragraph paragraph, string instruction, string placeholder)
    {
        paragraph.Append(
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin, Dirty = true }),
            new W.Run(new W.FieldCode(instruction) { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
            new W.Run(new W.Text(placeholder)),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End }));
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

    private static WordMcpException InvalidBlock(DocumentBlockKind kind) => new(
        "invalid_or_unsupported_block",
        "$.document.sections[].blocks",
        $"The {kind} block is missing its required constrained semantic payload.",
        "Use one supported block kind with exactly one matching payload.");

    private static WordMcpException InvalidTemplate(string message) => new(
        "invalid_generation_template",
        "$.template_source",
        message,
        "Use a macro-free DOCX or DOTX whose allowlisted design parts pass preflight validation.");

    private static WordMcpException InvalidImage(string message) => new(
        "invalid_image_snapshot",
        "$.document.sections[].blocks[].image_file_id",
        message,
        "Use an embedded PNG or JPEG opaque snapshot resolved inside the same user boundary.",
        unsafeDocument: true);

    private sealed record GenerationNumberingIds(int Unordered, int Ordered);

    private sealed record TemplateHeaderFooterDesign(
        IReadOnlyDictionary<W.HeaderFooterValues, string> Headers,
        IReadOnlyDictionary<W.HeaderFooterValues, string> Footers,
        bool DifferentFirstPage,
        bool DifferentEvenOdd)
    {
        public bool HasParts => Headers.Count > 0 || Footers.Count > 0;
    }

    private readonly record struct GenerationImageBudget(int Count, long Bytes, long Pixels);

    private sealed class TemplateMediaCopyContext(
        WordMcpOptions options,
        GenerationImageBudget initialBudget)
    {
        private readonly Dictionary<Uri, ImagePart> copiedParts = [];
        private readonly Dictionary<(Uri Destination, Uri Source), string> relationships = [];
        private int imageCount = initialBudget.Count;
        private long totalBytes = initialBudget.Bytes;
        private long totalPixels = initialBudget.Pixels;

        public string CopyImage(
            OpenXmlPart source,
            OpenXmlPart destination,
            string relationshipId,
            CancellationToken cancellationToken)
        {
            if (!source.TryGetPartById(relationshipId, out var sourcePart) || sourcePart is not ImagePart sourceImage)
            {
                throw InvalidTemplate("A template drawing does not resolve to an embedded image part.");
            }

            var relationshipKey = (destination.Uri, sourceImage.Uri);
            if (relationships.TryGetValue(relationshipKey, out var existingRelationshipId))
            {
                return existingRelationshipId;
            }

            if (!copiedParts.TryGetValue(sourceImage.Uri, out var targetImage))
            {
                var mediaType = sourceImage.ContentType.ToLowerInvariant();
                if (mediaType is not ("image/png" or "image/jpeg"))
                {
                    throw InvalidTemplate("A template header or footer references unsupported media.");
                }

                var bytes = ReadBoundedImage(sourceImage, options.MaxImageBytes, cancellationToken);
                ImageDimensions dimensions;
                try
                {
                    dimensions = ImageDimensions.Read(bytes, mediaType);
                }
                catch (WordMcpException)
                {
                    throw InvalidTemplate("A template header or footer image has invalid magic bytes or dimensions.");
                }

                var pixels = checked((long)dimensions.Width * dimensions.Height);
                try
                {
                    imageCount = checked(imageCount + 1);
                    totalBytes = checked(totalBytes + bytes.LongLength);
                    totalPixels = checked(totalPixels + pixels);
                }
                catch (OverflowException)
                {
                    throw InvalidTemplate("Template media exceeds the aggregate image safety limits.");
                }

                if (dimensions.Width > options.MaxImageDimension
                    || dimensions.Height > options.MaxImageDimension
                    || pixels > options.MaxImagePixels
                    || imageCount > options.MaxImages
                    || totalBytes > options.MaxTotalImageBytes
                    || totalPixels > options.MaxTotalImagePixels)
                {
                    throw InvalidTemplate("Template media exceeds the image safety limits.");
                }

                targetImage = AddImagePart(destination, mediaType);
                using (var input = new MemoryStream(bytes, writable: false))
                {
                    targetImage.FeedData(input);
                }

                copiedParts.Add(sourceImage.Uri, targetImage);
            }
            else
            {
                destination.AddPart(targetImage);
            }

            var targetRelationshipId = destination.GetIdOfPart(targetImage);
            relationships.Add(relationshipKey, targetRelationshipId);
            return targetRelationshipId;
        }

        private static ImagePart AddImagePart(OpenXmlPart destination, string mediaType) =>
            (destination, mediaType) switch
            {
                (HeaderPart header, "image/png") => header.AddImagePart(ImagePartType.Png),
                (HeaderPart header, "image/jpeg") => header.AddImagePart(ImagePartType.Jpeg),
                (FooterPart footer, "image/png") => footer.AddImagePart(ImagePartType.Png),
                (FooterPart footer, "image/jpeg") => footer.AddImagePart(ImagePartType.Jpeg),
                _ => throw new InvalidOperationException("Template media can only be copied into a header or footer."),
            };

        private static byte[] ReadBoundedImage(
            ImagePart source,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            using var input = source.GetStream(FileMode.Open, FileAccess.Read);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (output.Length > maximumBytes - read)
                {
                    throw InvalidTemplate("A template header or footer image exceeds the per-image byte limit.");
                }

                output.Write(buffer, 0, read);
            }

            if (output.Length == 0)
            {
                throw InvalidTemplate("A template header or footer image is empty.");
            }

            return output.ToArray();
        }
    }

    private readonly record struct ImageDimensions(int Width, int Height)
    {
        public static ImageDimensions Read(ReadOnlySpan<byte> bytes, string mediaType)
        {
            return mediaType switch
            {
                "image/png" => ReadPng(bytes),
                "image/jpeg" => ReadJpeg(bytes),
                _ => throw InvalidImage("Only image/png and image/jpeg snapshots are accepted."),
            };
        }

        private static ImageDimensions ReadPng(ReadOnlySpan<byte> bytes)
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature) || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            {
                throw InvalidImage("The PNG magic bytes or IHDR header are invalid.");
            }

            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
            return Positive(width, height);
        }

        private static ImageDimensions ReadJpeg(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            {
                throw InvalidImage("The JPEG magic bytes are invalid.");
            }

            var offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                while (offset < bytes.Length && bytes[offset] != 0xFF)
                {
                    offset++;
                }

                while (offset < bytes.Length && bytes[offset] == 0xFF)
                {
                    offset++;
                }

                if (offset >= bytes.Length)
                {
                    break;
                }

                var marker = bytes[offset++];
                if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                {
                    continue;
                }

                if (offset + 2 > bytes.Length)
                {
                    break;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                if (length < 2 || offset + length > bytes.Length)
                {
                    break;
                }

                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                {
                    if (length < 7)
                    {
                        break;
                    }

                    var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                    var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                    return Positive(width, height);
                }

                offset += length;
            }

            throw InvalidImage("The JPEG dimensions could not be read safely.");
        }

        private static ImageDimensions Positive(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw InvalidImage("Image dimensions must be positive.");
            }

            return new ImageDimensions(width, height);
        }
    }
}
