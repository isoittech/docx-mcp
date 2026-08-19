using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Storage;
using WordMcp.Word;

namespace WordMcp.Tests;

public sealed class DigitalSignaturePolicyTests
{
    private const string OriginContentType = "application/vnd.openxmlformats-package.digital-signature-origin";
    private const string SignatureContentType = "application/vnd.openxmlformats-package.digital-signature-xmlsignature+xml";
    private const string OriginRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin";
    private const string SignatureRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/signature";

    [Fact]
    public async Task SignedPackagePassesPreflightAndAnalysisReportsReadOnlyFeature()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument("signed-source.docx");
        AddSyntheticDigitalSignature(source);

        var inspection = await new DocxPackageGuard(Options.Create(new WordMcpOptions()))
            .ValidateSnapshotAsync(source, TestContext.Current.CancellationToken);
        var analysis = CreateEngine().Analyze(
            AnalysisRequest(source),
            TestContext.Current.CancellationToken);

        Assert.False(inspection.IsTemplate);
        Assert.Contains("digital_signature", analysis.Summary.Features);
        Assert.Contains("digital_signature_editing", analysis.Summary.UnsupportedFeatures);
    }

    [Fact]
    public void SignedPackageRejectsReplaceAndAtomicEditBeforeCreatingOutput()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreateStoryDocument("signed-mutation-source.docx");
        AddSyntheticDigitalSignature(source);
        var engine = CreateEngine();
        var analysis = engine.Analyze(AnalysisRequest(source), TestContext.Current.CancellationToken);
        var target = analysis.Targets.Values.Single(value =>
            value.Kind == "paragraph" && value.Story == "main" && value.Snippet == "Second paragraph");
        var replaceOutput = files.OutputPath("signed-replace-output.docx");
        var atomicOutput = files.OutputPath("signed-atomic-output.docx");

        var replaceError = Assert.Throws<WordMcpException>(() => engine.ReplaceText(
            new WordMutationRequest(source, replaceOutput, analysis),
            [new TextReplacementRequest(target.TargetId, "Second paragraph", "Changed")],
            TestContext.Current.CancellationToken));
        var atomicError = Assert.Throws<WordMcpException>(() => engine.ApplyEdits(
            new WordMutationRequest(source, atomicOutput, analysis),
            [new AtomicEditRequest(WordEditOperations.ReplaceBlock, target.TargetId, Runs: [new SemanticRun("Changed")])],
            TestContext.Current.CancellationToken));

        Assert.Equal("digital_signature_editing_unsupported", replaceError.Code);
        Assert.Equal(replaceError.Code, atomicError.Code);
        Assert.False(replaceError.UnsafeDocument);
        Assert.False(File.Exists(replaceOutput));
        Assert.False(File.Exists(atomicOutput));
    }

    [Fact]
    public void SignedPopulateTemplateIsRejectedWithTheMutationPolicyCode()
    {
        using var files = new TestDocumentFactory();
        var source = files.CreatePopulateTemplate("signed-template.dotx");
        AddSyntheticDigitalSignature(source);
        var destination = files.OutputPath("signed-populate-output.docx");

        var error = Assert.Throws<WordMcpException>(() => CreateEngine().PopulateTemplate(
            new WordTemplatePopulationRequest(
                source,
                destination,
                "signed-template.dotx",
                TestDocumentFactory.Sha256(source),
                "usr_test",
                "cnv_test"),
            [new TemplateFieldRequest("CustomerName", [new SemanticRun("合成入力")])],
            TestContext.Current.CancellationToken));

        Assert.Equal("digital_signature_editing_unsupported", error.Code);
        Assert.False(error.UnsafeDocument);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void NewGenerationFromSignedTemplateRemovesAllSignaturePackageStructures()
    {
        using var files = new TestDocumentFactory();
        var template = files.CreatePopulateTemplate("signed-generation-template.dotx");
        AddSyntheticDigitalSignature(template);
        var destination = files.OutputPath("generated-from-signed-template.docx");

        var result = CreateEngine().Generate(
            new WordGenerationRequest(
                destination,
                TestDocumentFactory.JapaneseReportDefinition(),
                "usr_test",
                "cnv_test",
                TemplatePath: template),
            TestContext.Current.CancellationToken);

        Assert.True(result.Validation.IsAccepted);
        Assert.DoesNotContain("digital_signature", result.OutputAnalysis.Summary.Features);
        Assert.False(ContainsSyntheticDigitalSignatureEvidence(destination));
        using var output = WordprocessingDocument.Open(destination, false);
        Assert.NotNull(output.MainDocumentPart?.Document);
    }

    private static OpenXmlWordDocumentEngine CreateEngine() => new(new WordMcpOptions());

    private static WordAnalysisRequest AnalysisRequest(string source) => new(
        source,
        Path.GetFileName(source),
        TestDocumentFactory.Sha256(source),
        "usr_test",
        "cnv_test");

    private static void AddSyntheticDigitalSignature(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var contentTypes = ReadXml(archive, "[Content_Types].xml");
        var types = contentTypes.Root ?? throw new InvalidDataException("Missing content-types root.");
        var contentTypeNamespace = types.Name.Namespace;
        types.Add(
            new XElement(
                contentTypeNamespace + "Override",
                new XAttribute("PartName", "/_xmlsignatures/origin.sigs"),
                new XAttribute("ContentType", OriginContentType)),
            new XElement(
                contentTypeNamespace + "Override",
                new XAttribute("PartName", "/_xmlsignatures/sig1.xml"),
                new XAttribute("ContentType", SignatureContentType)));
        ReplaceXml(archive, "[Content_Types].xml", contentTypes);

        var packageRelationships = ReadXml(archive, "_rels/.rels");
        var relationships = packageRelationships.Root ?? throw new InvalidDataException("Missing relationships root.");
        var relationshipNamespace = relationships.Name.Namespace;
        relationships.Add(new XElement(
            relationshipNamespace + "Relationship",
            new XAttribute("Id", "rIdSyntheticSignatureOrigin"),
            new XAttribute("Type", OriginRelationship),
            new XAttribute("Target", "_xmlsignatures/origin.sigs")));
        ReplaceXml(archive, "_rels/.rels", packageRelationships);

        WriteEntry(archive, "_xmlsignatures/origin.sigs", []);
        WriteEntry(
            archive,
            "_xmlsignatures/_rels/origin.sigs.rels",
            Encoding.UTF8.GetBytes($"""
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdSyntheticSignature" Type="{SignatureRelationship}" Target="sig1.xml" />
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

    private static bool ContainsSyntheticDigitalSignatureEvidence(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var entry in archive.Entries.Where(entry =>
                     string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
                     || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            using var input = entry.Open();
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var xml = reader.ReadToEnd();
            if (xml.Contains(OriginContentType, StringComparison.OrdinalIgnoreCase)
                || xml.Contains(SignatureContentType, StringComparison.OrdinalIgnoreCase)
                || xml.Contains(OriginRelationship, StringComparison.OrdinalIgnoreCase)
                || xml.Contains(SignatureRelationship, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
}
