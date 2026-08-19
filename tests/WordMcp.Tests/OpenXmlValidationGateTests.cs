using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Domain;
using WordMcp.Word;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WordMcp.Tests;

public sealed class OpenXmlValidationGateTests
{
    [Fact]
    public void ValidateNewDocumentAcceptsAnOffice2019ValidPackage()
    {
        using var files = new TestDocumentFactory();
        var candidate = files.CreateStoryDocument();

        var report = new OpenXmlValidationGate().ValidateNewDocument(candidate, TestContext.Current.CancellationToken);

        Assert.True(report.IsAccepted);
        Assert.Empty(report.BaselineErrors);
        Assert.Empty(report.CandidateErrors);
        Assert.Empty(report.NewErrors);
    }

    [Fact]
    public void ValidateExistingEditAllowsOnlyPreExistingValidationFingerprints()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateInvalidBodyDocument(1, "invalid-source.docx");
        var candidate = files.OutputPath("invalid-candidate.docx");
        File.Copy(source, candidate);
        using (var document = WordprocessingDocument.Open(candidate, true))
        {
            var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing synthetic main part.");
            var root = main.Document ?? throw new InvalidDataException("Missing synthetic document root.");
            root.Descendants<W.Text>().First().Text = "A harmless text edit";
            root.Save();
        }

        var report = new OpenXmlValidationGate().ValidateExistingEdit(source, candidate, TestContext.Current.CancellationToken);

        Assert.True(report.IsAccepted);
        Assert.NotEmpty(report.BaselineErrors);
        Assert.Equal(report.BaselineErrors, report.CandidateErrors);
        Assert.Empty(report.NewErrors);
    }

    [Fact]
    public void ValidateExistingEditRejectsANewFingerprint()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument();
        var candidate = files.OutputPath("regressed.docx");
        File.Copy(source, candidate);
        using (var document = WordprocessingDocument.Open(candidate, true))
        {
            var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing synthetic main part.");
            var root = main.Document ?? throw new InvalidDataException("Missing synthetic document root.");
            var body = root.Body ?? throw new InvalidDataException("Missing synthetic body.");
            body.Elements<W.SectionProperties>().Single().InsertBeforeSelf(new W.Run(new W.Text("invalid")));
            root.Save();
        }

        var exception = Assert.Throws<WordMcpException>(() => new OpenXmlValidationGate().ValidateExistingEdit(
            source,
            candidate,
            TestContext.Current.CancellationToken));

        Assert.Equal("openxml_regression", exception.Code);
    }

    [Fact]
    public void ValidateNewDocumentRejectsAnySchemaError()
    {
        using var files = new TestDocumentFactory();
        var candidate = files.CreateInvalidBodyDocument(1, "invalid-generated.docx");

        var exception = Assert.Throws<WordMcpException>(() => new OpenXmlValidationGate().ValidateNewDocument(
            candidate,
            TestContext.Current.CancellationToken));

        Assert.Equal("generated_openxml_invalid", exception.Code);
        Assert.Contains("first:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateExistingEditRejectsCandidateErrorsBeyondTheComparisonBound()
    {
        using var files = new TestDocumentFactory();
        var source = CreateManyValidationErrors(files, 1_000, "bounded-source.docx");
        var candidate = CreateManyValidationErrors(files, 1_001, "over-limit-candidate.docx");
        var gate = new OpenXmlValidationGate();

        Assert.Equal(1_000, gate.Validate(source, TestContext.Current.CancellationToken).Count);
        var exception = Assert.Throws<WordMcpException>(() => gate.ValidateExistingEdit(
            source,
            candidate,
            TestContext.Current.CancellationToken));

        Assert.Equal("openxml_validation_error_limit", exception.Code);
        Assert.Contains("candidate document", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateExistingEditExplicitlyRejectsAnOverLimitBaseline()
    {
        using var files = new TestDocumentFactory();
        var source = CreateManyValidationErrors(files, 1_001, "over-limit-baseline.docx");
        var candidate = files.OutputPath("copied-over-limit-baseline.docx");
        File.Copy(source, candidate);

        var exception = Assert.Throws<WordMcpException>(() => new OpenXmlValidationGate().ValidateExistingEdit(
            source,
            candidate,
            TestContext.Current.CancellationToken));

        Assert.Equal("openxml_validation_error_limit", exception.Code);
        Assert.Contains("baseline document", exception.Message, StringComparison.Ordinal);
    }

    private static string CreateManyValidationErrors(
        TestDocumentFactory files,
        int errorCount,
        string fileName)
    {
        const string wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var path = files.CreateStoryDocument(fileName);
        using var document = WordprocessingDocument.Open(path, true);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing synthetic main part.");
        var root = main.Document ?? throw new InvalidDataException("Missing synthetic document root.");
        var body = root.Body ?? throw new InvalidDataException("Missing synthetic body.");
        var section = body.Elements<W.SectionProperties>().Single();
        for (var index = 0; index < errorCount; index++)
        {
            var paragraph = new W.Paragraph(new W.Run(new W.Text($"invalid-{index}")));
            paragraph.SetAttribute(new OpenXmlAttribute("w", "unsupported", wordNamespace, "1"));
            section.InsertBeforeSelf(paragraph);
        }

        root.Save();
        return path;
    }
}
