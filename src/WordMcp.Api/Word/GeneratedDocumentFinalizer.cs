using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Domain;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Word;

internal static class GeneratedDocumentFinalizer
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static void FinalizeForDistribution(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var document = WordprocessingDocument.Open(path, true);
        var main = document.MainDocumentPart
                   ?? throw InvalidDocument("The finalized document has no main document part.");
        var root = main.Document
                   ?? throw InvalidDocument("The finalized document has no main document root.");
        var settings = main.DocumentSettingsPart?.Settings;
        if (settings is not null)
        {
            settings.RemoveAllChildren<W.UpdateFieldsOnOpen>();
            settings.Save();
        }

        ClearDirtyFields(root, cancellationToken);
        foreach (var header in main.HeaderParts)
        {
            ClearDirtyFields(header.Header, cancellationToken);
        }

        foreach (var footer in main.FooterParts)
        {
            ClearDirtyFields(footer.Footer, cancellationToken);
        }

        NormalizeLibreOfficeMarkup(root, cancellationToken);
        root.Save();
    }

    private static void ClearDirtyFields(
        OpenXmlPartRootElement? root,
        CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return;
        }

        foreach (var field in root.Descendants<W.FieldChar>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            field.Dirty = null;
        }

        foreach (var field in root.Descendants<W.SimpleField>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            field.Dirty = null;
        }

        root.Save();
    }

    private static void NormalizeLibreOfficeMarkup(
        W.Document root,
        CancellationToken cancellationToken)
    {
        foreach (var section in root.Descendants<W.SectionProperties>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var duplicate in section.Elements<W.PaperSource>().Skip(1).ToArray())
            {
                duplicate.Remove();
            }
        }

        foreach (var cantSplit in root.Descendants<W.CantSplit>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizeOnOffOnlyElement(cantSplit);
        }

        foreach (var tableHeader in root.Descendants<W.TableHeader>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizeOnOffOnlyElement(tableHeader);
        }

        NormalizeSchemaOrder<W.ParagraphProperties>(root, cancellationToken);
        NormalizeSchemaOrder<W.RunProperties>(root, cancellationToken);
        NormalizeSchemaOrder<W.ParagraphMarkRunProperties>(root, cancellationToken);
        NormalizeSchemaOrder<W.SectionProperties>(root, cancellationToken);
        NormalizeSchemaOrder<W.TableRowProperties>(root, cancellationToken);
    }

    private static void NormalizeOnOffOnlyElement(OpenXmlElement element)
    {
        var lexicalValue = element.GetAttribute("val", WordNamespace).Value;
        if (lexicalValue is not null
            && (lexicalValue.Equals("false", StringComparison.OrdinalIgnoreCase)
                || lexicalValue.Equals("off", StringComparison.OrdinalIgnoreCase)
                || lexicalValue.Equals("0", StringComparison.Ordinal)))
        {
            element.Remove();
            return;
        }

        element.RemoveAttribute("val", WordNamespace);
    }

    private static void NormalizeSchemaOrder<T>(
        OpenXmlPartRootElement root,
        CancellationToken cancellationToken)
        where T : OpenXmlCompositeElement
    {
        foreach (var element in root.Descendants<T>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var children = element.ChildElements
                .Select(static child => child.CloneNode(true))
                .ToArray();
            element.RemoveAllChildren();
            foreach (var child in children)
            {
                element.AddChild(child, throwOnError: true);
            }
        }
    }

    private static WordMcpException InvalidDocument(string message) => new(
        "finalized_document_invalid",
        "$.document",
        message,
        "Regenerate the document and do not publish a DOCX that still requests automatic field updates.");
}
