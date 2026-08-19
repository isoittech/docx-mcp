using System.Text;
using DocumentFormat.OpenXml;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Word;

internal sealed class WordTextMap
{
    private const string WordprocessingNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly List<TextSegment> segments = [];
    private readonly HashSet<int> boundaries = [];
    private readonly StringBuilder text = new();

    private WordTextMap(W.Paragraph paragraph)
    {
        var fieldDepth = 0;
        Walk(paragraph, unsafeDepth: 0, skipText: false, ref fieldDepth);
    }

    public string Text => text.ToString();

    public static WordTextMap Create(W.Paragraph paragraph)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        return new WordTextMap(paragraph);
    }

    public IReadOnlyList<TextRange> Find(string expectedText)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedText);
        var result = new List<TextRange>();
        var offset = 0;
        while (offset <= text.Length - expectedText.Length)
        {
            var index = Text.IndexOf(expectedText, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            result.Add(new TextRange(index, expectedText.Length));
            offset = index + expectedText.Length;
        }

        return result;
    }

    public bool CanReplace(TextRange range)
    {
        if (range.Length <= 0 || range.Start < 0 || range.End > text.Length)
        {
            return false;
        }

        if (boundaries.Any(offset => offset > range.Start && offset < range.End))
        {
            return false;
        }

        var overlapping = segments.Where(segment => segment.Start < range.End && segment.End > range.Start).ToArray();
        return overlapping.Length > 0
               && overlapping.All(segment => segment.IsSafe)
               && overlapping[0].Start <= range.Start
               && overlapping[^1].End >= range.End;
    }

    public void Replace(TextRange range, string replacement)
    {
        if (!CanReplace(range))
        {
            throw new InvalidOperationException("The requested range crosses a protected WordprocessingML boundary.");
        }

        var overlapping = segments.Where(segment => segment.Start < range.End && segment.End > range.Start).ToArray();
        var first = overlapping[0];
        var last = overlapping[^1];
        var firstOffset = range.Start - first.Start;
        var lastOffset = range.End - last.Start;

        if (ReferenceEquals(first.Text, last.Text))
        {
            var value = first.Text.Text;
            first.Text.Text = string.Concat(value.AsSpan(0, firstOffset), replacement, value.AsSpan(lastOffset));
            UpdateSpace(first.Text);
            return;
        }

        first.Text.Text = string.Concat(first.Text.Text.AsSpan(0, firstOffset), replacement);
        UpdateSpace(first.Text);
        for (var index = 1; index < overlapping.Length - 1; index++)
        {
            overlapping[index].Text.Text = string.Empty;
            UpdateSpace(overlapping[index].Text);
        }

        last.Text.Text = last.Text.Text[lastOffset..];
        UpdateSpace(last.Text);
    }

    public static string VisibleText(OpenXmlElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element is W.Paragraph paragraph)
        {
            return Create(paragraph).Text;
        }

        return string.Concat(element.Descendants<W.Paragraph>().Select(paragraphValue => Create(paragraphValue).Text));
    }

    public static void UpdateSpace(W.Text node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var value = node.Text;
        node.Space = value.Length > 0 && (value[0] == ' ' || value[^1] == ' ')
            ? SpaceProcessingModeValues.Preserve
            : null;
    }

    private void Walk(OpenXmlElement element, int unsafeDepth, bool skipText, ref int fieldDepth)
    {
        if (IsZeroWidthBoundary(element))
        {
            boundaries.Add(text.Length);
            return;
        }

        if (element is W.FieldChar fieldCharacter)
        {
            boundaries.Add(text.Length);
            var fieldType = fieldCharacter.FieldCharType?.Value;
            if (fieldType == W.FieldCharValues.Begin)
            {
                fieldDepth++;
            }
            else if (fieldType == W.FieldCharValues.End)
            {
                fieldDepth = Math.Max(0, fieldDepth - 1);
            }

            return;
        }

        if (element is W.FieldCode or W.DeletedText)
        {
            boundaries.Add(text.Length);
            return;
        }

        if (element is W.TabChar)
        {
            AppendSynthetic("\t");
            return;
        }

        if (element is W.Break or W.CarriageReturn)
        {
            AppendSynthetic("\n");
            return;
        }

        if (element is W.Text textNode)
        {
            if (!skipText)
            {
                var start = text.Length;
                text.Append(textNode.Text);
                if (textNode.Text.Length > 0)
                {
                    segments.Add(new TextSegment(textNode, start, textNode.Text.Length, unsafeDepth == 0 && fieldDepth == 0));
                }
            }

            return;
        }

        var localName = element.LocalName;
        var isWordElement = string.Equals(element.NamespaceUri, WordprocessingNamespace, StringComparison.Ordinal);
        var skipsChildren = isWordElement && localName is "del" or "moveFrom" or "txbxContent";
        var protectsChildren = isWordElement && localName is
            "sdt" or "ins" or "moveTo" or "hyperlink" or "fldSimple" or "customXml" or "smartTag";

        if (skipsChildren || protectsChildren)
        {
            boundaries.Add(text.Length);
        }

        foreach (var child in element.ChildElements)
        {
            Walk(
                child,
                unsafeDepth + (protectsChildren ? 1 : 0),
                skipText || skipsChildren,
                ref fieldDepth);
        }

        if (skipsChildren || protectsChildren)
        {
            boundaries.Add(text.Length);
        }
    }

    private void AppendSynthetic(string value)
    {
        boundaries.Add(text.Length);
        text.Append(value);
        boundaries.Add(text.Length);
    }

    private static bool IsZeroWidthBoundary(OpenXmlElement element)
    {
        if (!string.Equals(element.NamespaceUri, WordprocessingNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        return element.LocalName is
            "bookmarkStart" or "bookmarkEnd" or
            "commentRangeStart" or "commentRangeEnd" or
            "moveFromRangeStart" or "moveFromRangeEnd" or
            "moveToRangeStart" or "moveToRangeEnd" or
            "permStart" or "permEnd";
    }

    private sealed record TextSegment(W.Text Text, int Start, int Length, bool IsSafe)
    {
        public int End => Start + Length;
    }
}

internal readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}
