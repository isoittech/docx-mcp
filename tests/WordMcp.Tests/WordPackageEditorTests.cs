using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Word;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Tests;

public sealed class WordPackageEditorTests
{
    [Fact]
    public void RewriteUsesEditedPayloadOnlyForExplicitlyAllowedEntries()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var edited = files.OutputPath("sdk-edited.docx");
        var destination = files.OutputPath("rewritten.docx");
        File.Copy(source, edited);
        using (var document = WordprocessingDocument.Open(edited, true))
        {
            var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing synthetic main part.");
            var root = main.Document ?? throw new InvalidDataException("Missing synthetic document root.");
            root.Descendants<W.Text>().First().Text = "Edited main text";
            main.HeaderParts.Single().Header!.Descendants<W.Text>().First().Text = "Undeclared header edit";
            root.Save();
            main.HeaderParts.Single().Header!.Save();
        }

        new WordPackageEditor().RewriteFromEditedPackage(
            source,
            edited,
            destination,
            ["word/document.xml"],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            TestDocumentFactory.ReadEntryPayload(edited, "word/document.xml"),
            TestDocumentFactory.ReadEntryPayload(destination, "word/document.xml"));
        Assert.Equal(
            TestDocumentFactory.ReadEntryPayload(source, "word/header1.xml"),
            TestDocumentFactory.ReadEntryPayload(destination, "word/header1.xml"));
        Assert.NotEqual(
            TestDocumentFactory.ReadEntryPayload(edited, "word/header1.xml"),
            TestDocumentFactory.ReadEntryPayload(destination, "word/header1.xml"));

        var sourceEntries = EntryPayloads(source);
        var destinationEntries = EntryPayloads(destination);
        Assert.Equal(sourceEntries.Keys.Order(StringComparer.Ordinal), destinationEntries.Keys.Order(StringComparer.Ordinal));
        foreach (var entry in sourceEntries.Where(pair => pair.Key != "word/document.xml"))
        {
            Assert.Equal(entry.Value, destinationEntries[entry.Key]);
        }
    }

    [Fact]
    public void EditRequiresTheMutationToDeclareChangedEntries()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var destination = files.OutputPath("undeclared.docx");

        var exception = Assert.Throws<InvalidOperationException>(() => new WordPackageEditor().Edit(
            source,
            destination,
            (document, _) =>
            {
                var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing synthetic main part.");
                var root = main.Document ?? throw new InvalidDataException("Missing synthetic document root.");
                root.Descendants<W.Text>().First().Text = "Untracked";
            },
            TestContext.Current.CancellationToken));

        Assert.Contains("declare", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void RewriteRejectsDeclaredEntryMissingFromEditedPackage()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var destination = files.OutputPath("missing-entry.docx");

        var exception = Assert.Throws<InvalidDataException>(() => new WordPackageEditor().RewriteFromEditedPackage(
            source,
            source,
            destination,
            ["word/not-present.xml"],
            TestContext.Current.CancellationToken));

        Assert.Contains("not-present", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    private static Dictionary<string, byte[]> EntryPayloads(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var input = entry.Open();
                using var output = new MemoryStream();
                input.CopyTo(output);
                return output.ToArray();
            },
            StringComparer.Ordinal);
    }
}
