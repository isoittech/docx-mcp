using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class DocxPackageGuardTests
{
    [Fact]
    public async Task RejectsInputFileSizeBeforeOpeningThePackage()
    {
        using var fixture = DocxTestPackage.Create();

        var error = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxFileBytes = 8,
        }).ValidateSnapshotAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("file_size_limit", error.Code);
    }

    [Fact]
    public async Task VerifiesTheRecordedSnapshotSha256BeforeParsing()
    {
        using var fixture = DocxTestPackage.Create();
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                fixture.Path,
                TestContext.Current.CancellationToken)))
            .ToLowerInvariant();

        var accepted = await CreateGuard().ValidateSnapshotAsync(
            fixture.Path,
            expected,
            CancellationToken.None);
        var changed = string.Concat(expected[..63], expected[^1] == '0' ? '1' : '0');
        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
            fixture.Path,
            changed,
            CancellationToken.None));

        Assert.Equal(1, accepted.BlockCount);
        Assert.Equal("input_changed", error.Code);
    }

    [Theory]
    [InlineData(false, ".docx")]
    [InlineData(true, ".dotx")]
    public async Task AcceptsMinimalMacroFreeWordPackages(bool template, string extension)
    {
        using var fixture = DocxTestPackage.Create(template: template, extension: extension);

        var result = await CreateGuard().ValidateDocumentAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(template, result.IsTemplate);
        Assert.Equal(1, result.BlockCount);
        Assert.Equal(5, result.CharacterCount);
        Assert.Equal(0, result.ImageCount);
    }

    [Theory]
    [InlineData("../escape.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("C:/absolute.xml")]
    public async Task RejectsTraversalAndAbsoluteZipEntries(string entryName)
    {
        using var fixture = DocxTestPackage.Create(configure: archive =>
            DocxTestPackage.WriteEntry(archive, entryName, "<x/>", CompressionLevel.NoCompression));

        var error = await AssertUnsafeAsync(
            () => CreateGuard().ValidateDocumentAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("unsafe_zip_path", error.Code);
    }

    [Fact]
    public async Task RejectsDuplicateEntriesAfterPathNormalization()
    {
        using var fixture = DocxTestPackage.Create(configure: archive =>
            DocxTestPackage.WriteEntry(archive, "word\\document.xml", "<duplicate/>", CompressionLevel.NoCompression));

        var error = await AssertUnsafeAsync(
            () => CreateGuard().ValidateDocumentAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("duplicate_zip_entry", error.Code);
    }

    [Fact]
    public async Task RejectsZipEntryCountExpansionAndCompressionRatioLimits()
    {
        using var normal = DocxTestPackage.Create();
        var entryLimit = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxZipEntries = 2,
        }).ValidateDocumentAsync(normal.Path, CancellationToken.None));
        Assert.Equal("zip_entry_limit", entryLimit.Code);

        var expansionLimit = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxUncompressedBytes = 128,
        }).ValidateDocumentAsync(normal.Path, CancellationToken.None));
        Assert.Equal("zip_expansion_limit", expansionLimit.Code);

        using var compressed = DocxTestPackage.Create(configure: archive =>
            DocxTestPackage.WriteEntry(archive, "custom/high-ratio.bin", new byte[2 * 1024 * 1024], CompressionLevel.SmallestSize));
        var ratioLimit = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            compressed.Path,
            CancellationToken.None));
        Assert.Equal("zip_compression_ratio", ratioLimit.Code);
    }

    [Fact]
    public async Task RejectsBrokenZipEncryptedContainerBrokenXmlAndDtd()
    {
        using var brokenZip = TemporaryFile.WithBytes(".docx", [1, 2, 3, 4]);
        var invalidZip = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            brokenZip.Path,
            CancellationToken.None));
        Assert.Equal("invalid_zip", invalidZip.Code);

        using var encryptedContainer = TemporaryFile.WithBytes(
            ".docx",
            [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        var encrypted = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            encryptedContainer.Path,
            CancellationToken.None));
        Assert.Equal("encrypted_document", encrypted.Code);

        using var brokenXml = DocxTestPackage.Create(documentXml: "<w:document");
        var invalidXml = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            brokenXml.Path,
            CancellationToken.None));
        Assert.Equal("invalid_xml", invalidXml.Code);

        const string dtdDocument = """
            <!DOCTYPE w:document [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>
            """;
        using var dtd = DocxTestPackage.Create(documentXml: dtdDocument);
        var forbiddenDtd = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            dtd.Path,
            CancellationToken.None));
        Assert.Equal("invalid_xml", forbiddenDtd.Code);
    }

    [Theory]
    [InlineData("word/vbaProject.bin", "active_content")]
    [InlineData("word/activeX/activeX1.bin", "active_content")]
    [InlineData("word/embeddings/oleObject1.bin", "embedded_object")]
    [InlineData("word/embeddings/package1.bin", "embedded_object")]
    public async Task RejectsActiveAndEmbeddedContent(string entryName, string expectedCode)
    {
        using var fixture = DocxTestPackage.Create(configure: archive =>
            DocxTestPackage.WriteEntry(archive, entryName, [0x00], CompressionLevel.NoCompression));

        var error = await AssertUnsafeAsync(
            () => CreateGuard().ValidateDocumentAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task RejectsMacroEnabledMainContentTypeAndAltChunk()
    {
        var macroTypes = DocxTestPackage.ContentTypes(
            "application/vnd.ms-word.document.macroEnabled.main+xml");
        using var macro = DocxTestPackage.Create(contentTypesXml: macroTypes);
        var macroError = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            macro.Path,
            CancellationToken.None));
        Assert.Equal("active_content", macroError.Code);

        var altChunkDocument = DocxTestPackage.DocumentXml(
            "<w:altChunk xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId9\"/>");
        using var altChunk = DocxTestPackage.Create(documentXml: altChunkDocument);
        var altChunkError = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            altChunk.Path,
            CancellationToken.None));
        Assert.Equal("alt_chunk", altChunkError.Code);
    }

    [Theory]
    [InlineData("https://example.com/reference", "http")]
    [InlineData("mailto:owner@example.com", "mailto")]
    public async Task AllowsCanonicalPassiveHyperlinksWithoutFetching(string target, string expectedScheme)
    {
        var relationships = DocxTestPackage.Relationships(
            $"<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{target}\" TargetMode=\"External\"/>");
        using var fixture = DocxTestPackage.Create(documentRelationshipsXml: relationships);

        var result = await CreateGuard().ValidateDocumentAsync(fixture.Path, CancellationToken.None);

        Assert.Contains(result.PassiveHyperlinks, item => item.StartsWith(expectedScheme, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://user:password@example.com/", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink")]
    [InlineData("file:///etc/passwd", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink")]
    [InlineData("https://example.com/image.png", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image")]
    public async Task RejectsCredentialedUnsupportedAndNonHyperlinkExternalTargets(string target, string type)
    {
        var relationships = DocxTestPackage.Relationships(
            $"<Relationship Id=\"rId2\" Type=\"{type}\" Target=\"{target}\" TargetMode=\"External\"/>");
        using var fixture = DocxTestPackage.Create(documentRelationshipsXml: relationships);

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            fixture.Path,
            CancellationToken.None));

        Assert.Equal("external_relationship", error.Code);
    }

    [Fact]
    public async Task RejectsDuplicateRelationshipIdsAndMissingInternalTargets()
    {
        var duplicateRelationships = DocxTestPackage.Relationships("""
            <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.com/a" TargetMode="External"/>
            <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.com/b" TargetMode="External"/>
            """);
        using var duplicate = DocxTestPackage.Create(documentRelationshipsXml: duplicateRelationships);
        var duplicateError = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
            duplicate.Path,
            CancellationToken.None));

        var missingRelationships = DocxTestPackage.Relationships(
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/missing.png\"/>");
        using var missing = DocxTestPackage.Create(documentRelationshipsXml: missingRelationships);
        var missingError = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
            missing.Path,
            CancellationToken.None));

        Assert.Equal("invalid_relationship", duplicateError.Code);
        Assert.Equal("invalid_relationship", missingError.Code);
    }

    [Fact]
    public async Task RejectsDanglingXmlRelationshipReferencesAndOrphanRelationshipParts()
    {
        var danglingDocument = DocxTestPackage.DocumentXml(
            "<w:p><w:hyperlink xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId404\"><w:r><w:t>missing</w:t></w:r></w:hyperlink></w:p>");
        using var dangling = DocxTestPackage.Create(documentXml: danglingDocument);
        var danglingError = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
            dangling.Path,
            TestContext.Current.CancellationToken));

        using var orphan = DocxTestPackage.Create(configure: archive =>
            DocxTestPackage.WriteEntry(
                archive,
                "word/_rels/missing.xml.rels",
                DocxTestPackage.Relationships(
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"document.xml\"/>"),
                CompressionLevel.NoCompression));
        var orphanError = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
            orphan.Path,
            TestContext.Current.CancellationToken));

        Assert.Equal("invalid_relationship", danglingError.Code);
        Assert.Equal("invalid_relationship", orphanError.Code);
    }

    [Fact]
    public async Task RejectsDuplicateContentTypeDeclarationsAndRelationshipLimits()
    {
        var duplicateTypes = DocxTestPackage.ContentTypes(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")
            .Replace(
                "</Types>",
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>",
                StringComparison.Ordinal);
        using var duplicate = DocxTestPackage.Create(contentTypesXml: duplicateTypes);
        var duplicateError = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
            duplicate.Path,
            CancellationToken.None));

        using var normal = DocxTestPackage.Create();
        var relationshipLimit = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxRelationshipsPerPart = 0,
        }).ValidateSnapshotAsync(normal.Path, CancellationToken.None));

        Assert.Equal("invalid_content_types", duplicateError.Code);
        Assert.Equal("relationship_limit", relationshipLimit.Code);
    }

    [Fact]
    public async Task AcceptsAllowlistedFieldSplitAcrossInstructionRuns()
    {
        var body = """
            <w:p>
              <w:r><w:fldChar w:fldCharType="begin"/></w:r>
              <w:r><w:instrText>TO</w:instrText></w:r>
              <w:r><w:instrText>C \o &quot;1-3&quot;</w:instrText></w:r>
              <w:r><w:fldChar w:fldCharType="separate"/></w:r>
              <w:r><w:t>目次</w:t></w:r>
              <w:r><w:fldChar w:fldCharType="end"/></w:r>
            </w:p>
            """;
        using var fixture = DocxTestPackage.Create(documentXml: DocxTestPackage.DocumentXml(body));

        var result = await CreateGuard().ValidateDocumentAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(1, result.FieldCount);
        Assert.True(result.HasTocField);
    }

    [Theory]
    [InlineData("HYPERLINK \"https://example.com\"", "unsafe_field")]
    [InlineData("DDEAUTO cmd /c calc", "unsafe_field")]
    public async Task RejectsFieldsOutsideTheFixedAllowlist(string instruction, string expectedCode)
    {
        var body = $"<w:p><w:fldSimple w:instr=\"{System.Security.SecurityElement.Escape(instruction)}\"><w:r><w:t>x</w:t></w:r></w:fldSimple></w:p>";
        using var fixture = DocxTestPackage.Create(documentXml: DocxTestPackage.DocumentXml(body));

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            fixture.Path,
            CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
    }

    [Theory]
    [InlineData("TOC \\o \"1-3", "invalid_field_structure")]
    [InlineData("TOC \\o \"3-1\" \\h", "invalid_field_structure")]
    [InlineData("PAGE DDEAUTO", "unsafe_field")]
    [InlineData("DATE \\@ \"yyyy-MM-dd\" HYPERLINK", "unsafe_field")]
    [InlineData("REF bookmark \\h second", "unsafe_field")]
    [InlineData("PAGE \\x", "unsafe_field")]
    public async Task RejectsMalformedOrInjectedAllowlistedFieldInstructions(
        string instruction,
        string expectedCode)
    {
        var body = $"<w:p><w:fldSimple w:instr=\"{System.Security.SecurityElement.Escape(instruction)}\"><w:r><w:t>x</w:t></w:r></w:fldSimple></w:p>";
        using var fixture = DocxTestPackage.Create(documentXml: DocxTestPackage.DocumentXml(body));

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            fixture.Path,
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, error.Code);
    }

    [Theory]
    [InlineData("<w:p><w:bookmarkStart w:id=\"1\" w:name=\"only-start\"/></w:p>", "invalid_identifier_structure")]
    [InlineData("<w:p><w:moveFromRangeStart w:id=\"1\"/></w:p>", "invalid_identifier_structure")]
    [InlineData("<w:p><w:r><w:commentReference w:id=\"7\"/></w:r></w:p>", "invalid_identifier_structure")]
    [InlineData("<w:del w:id=\"1\"><w:r><w:t>wrong text element</w:t></w:r></w:del>", "invalid_revision_structure")]
    [InlineData("<w:p><w:delText>outside deletion</w:delText></w:p>", "invalid_revision_structure")]
    public async Task RejectsBrokenIdentifierAndRevisionIntegrity(string body, string expectedCode)
    {
        using var fixture = DocxTestPackage.Create(
            documentXml: DocxTestPackage.DocumentXml(body));

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            fixture.Path,
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, error.Code);
    }

    [Theory]
    [InlineData("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>PAGE</w:instrText></w:r>")]
    [InlineData("<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>")]
    [InlineData("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>")]
    public async Task RejectsUnterminatedOrNestedComplexFields(string fieldMarkup)
    {
        using var fixture = DocxTestPackage.Create(
            documentXml: DocxTestPackage.DocumentXml($"<w:p>{fieldMarkup}</w:p>"));

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            fixture.Path,
            CancellationToken.None));

        Assert.Equal("invalid_field_structure", error.Code);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    public async Task AcceptsBoundedPngAndJpegMediaWithMatchingContentType(string extension)
    {
        var bytes = extension == "png" ? DocxTestPackage.RenderablePng(320, 200) : DocxTestPackage.Jpeg(320, 200);
        using var fixture = DocxTestPackage.Create(
            contentTypesXml: DocxTestPackage.ContentTypesWithImage(extension),
            configure: archive => DocxTestPackage.WriteEntry(
                archive,
                $"word/media/image1.{extension}",
                bytes,
                CompressionLevel.NoCompression));

        var result = await CreateGuard().ValidateDocumentAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(1, result.ImageCount);
    }

    [Theory]
    [InlineData("gif", "unsupported_media")]
    [InlineData("png", "invalid_image")]
    public async Task RejectsUnsupportedOrMagicMismatchedMedia(string extension, string expectedCode)
    {
        var contentTypes = DocxTestPackage.ContentTypesWithImage(extension);
        using var fixture = DocxTestPackage.Create(
            contentTypesXml: contentTypes,
            configure: archive => DocxTestPackage.WriteEntry(
                archive,
                $"word/media/image1.{extension}",
                [0x47, 0x49, 0x46, 0x38, 0x39, 0x61],
                CompressionLevel.NoCompression));

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            fixture.Path,
            CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task RejectsTruncatedChecksumCorruptedOrScanlineInvalidPngMedia()
    {
        var valid = DocxTestPackage.RenderablePng(32, 24);
        var truncated = valid[..^12];
        var corrupted = valid.ToArray();
        corrupted[^1] ^= 0x01;
        var invalidScanlines = DocxTestPackage.PngEnvelope(32, 24);

        foreach (var bytes in new[] { truncated, corrupted, invalidScanlines })
        {
            using var fixture = DocxTestPackage.Create(
                contentTypesXml: DocxTestPackage.ContentTypesWithImage("png"),
                configure: archive => DocxTestPackage.WriteEntry(
                    archive,
                    "word/media/image1.png",
                    bytes,
                    CompressionLevel.NoCompression));

            var error = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
                fixture.Path,
                CancellationToken.None));

            Assert.Equal("invalid_image", error.Code);
        }
    }

    [Theory]
    [InlineData("svg")]
    [InlineData("tif")]
    [InlineData("tiff")]
    [InlineData("webp")]
    [InlineData("wmf")]
    [InlineData("emf")]
    public async Task RejectsAllInitialVersionUnsupportedEmbeddedMedia(string extension)
    {
        using var fixture = DocxTestPackage.Create(
            contentTypesXml: DocxTestPackage.ContentTypesWithImage(extension),
            configure: archive => DocxTestPackage.WriteEntry(
                archive,
                $"word/media/image1.{extension}",
                [1, 2, 3, 4],
                CompressionLevel.NoCompression));

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
            fixture.Path,
            CancellationToken.None));

        Assert.Equal("unsupported_media", error.Code);
    }

    [Fact]
    public async Task RejectsAggregateEmbeddedImageByteAndPixelLimits()
    {
        var firstImage = DocxTestPackage.RenderablePng(100, 100);
        var secondImage = DocxTestPackage.RenderablePng(100, 100);
        using var fixture = DocxTestPackage.Create(
            contentTypesXml: DocxTestPackage.ContentTypesWithImage("png"),
            configure: archive =>
            {
                DocxTestPackage.WriteEntry(
                    archive,
                    "word/media/image1.png",
                    firstImage,
                    CompressionLevel.NoCompression);
                DocxTestPackage.WriteEntry(
                    archive,
                    "word/media/image2.png",
                    secondImage,
                    CompressionLevel.NoCompression);
            });

        var pixels = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxTotalImagePixels = 15_000,
        }).ValidateSnapshotAsync(fixture.Path, CancellationToken.None));
        var bytes = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxTotalImageBytes = firstImage.LongLength + secondImage.LongLength - 1,
        }).ValidateSnapshotAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("image_limit", pixels.Code);
        Assert.Equal("image_limit", bytes.Code);
    }

    [Fact]
    public async Task RejectsImageDimensionAndPixelBombs()
    {
        using var fixture = DocxTestPackage.Create(
            contentTypesXml: DocxTestPackage.ContentTypesWithImage("png"),
            configure: archive => DocxTestPackage.WriteEntry(
                archive,
                "word/media/image1.png",
                DocxTestPackage.PngEnvelope(20_000, 20_000),
                CompressionLevel.NoCompression));

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            fixture.Path,
            CancellationToken.None));

        Assert.Equal("image_limit", error.Code);
    }

    [Fact]
    public async Task RejectsJpegWithoutScanOrWithEmptyEntropyData()
    {
        byte[] withoutScan =
        [
            0xff, 0xd8,
            0xff, 0xc0, 0x00, 0x0b, 0x08,
            0x00, 0x18, 0x00, 0x20,
            0x01, 0x01, 0x11, 0x00,
            0xff, 0xd9,
        ];
        byte[] emptyScan =
        [
            0xff, 0xd8,
            0xff, 0xc0, 0x00, 0x0b, 0x08,
            0x00, 0x18, 0x00, 0x20,
            0x01, 0x01, 0x11, 0x00,
            0xff, 0xda, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3f, 0x00,
            0xff, 0xd9,
        ];

        foreach (var bytes in new[] { withoutScan, emptyScan })
        {
            using var fixture = DocxTestPackage.Create(
                contentTypesXml: DocxTestPackage.ContentTypesWithImage("jpg"),
                configure: archive => DocxTestPackage.WriteEntry(
                    archive,
                    "word/media/image1.jpg",
                    bytes,
                    CompressionLevel.NoCompression));

            var error = await AssertUnsafeAsync(() => CreateGuard().ValidateSnapshotAsync(
                fixture.Path,
                CancellationToken.None));

            Assert.Equal("invalid_image", error.Code);
        }
    }

    [Fact]
    public async Task EnforcesSemanticCharacterBlockCellAndExplicitPageBreakLimits()
    {
        var body = """
            <w:p><w:pPr><w:pageBreakBefore/></w:pPr><w:r><w:t>abcdef</w:t><w:br w:type="page"/></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """;
        using var fixture = DocxTestPackage.Create(documentXml: DocxTestPackage.DocumentXml(body));

        var characters = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxCharacters = 5,
        }).ValidateDocumentAsync(fixture.Path, CancellationToken.None));
        Assert.Equal("semantic_limit", characters.Code);

        var blocks = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxBlocks = 2,
        }).ValidateDocumentAsync(fixture.Path, CancellationToken.None));
        Assert.Equal("semantic_limit", blocks.Code);

        var cells = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxTableCells = 0,
        }).ValidateDocumentAsync(fixture.Path, CancellationToken.None));
        Assert.Equal("semantic_limit", cells.Code);

        var breaks = await AssertUnsafeAsync(() => CreateGuard(new WordMcpOptions
        {
            MaxExplicitPageBreaks = 1,
        }).ValidateDocumentAsync(fixture.Path, CancellationToken.None));
        Assert.Equal("semantic_limit", breaks.Code);
    }

    [Fact]
    public async Task RejectsPackagesThatCannotBeReopenedAsWordprocessingDocuments()
    {
        using var fixture = DocxTestPackage.Create(rootRelationshipsXml: DocxTestPackage.Relationships(string.Empty));

        var error = await AssertUnsafeAsync(() => CreateGuard().ValidateDocumentAsync(
            fixture.Path,
            CancellationToken.None));

        Assert.Equal("invalid_docx", error.Code);
    }

    private static DocxPackageGuard CreateGuard(WordMcpOptions? options = null) =>
        new(Options.Create(options ?? new WordMcpOptions()));

    private static async Task<WordMcpException> AssertUnsafeAsync(Func<Task<DocxPackageInspection>> action)
    {
        var error = await Assert.ThrowsAsync<WordMcpException>(action);
        Assert.True(error.UnsafeDocument);
        return error;
    }
}

internal static class DocxTestPackage
{
    private const string MainNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static TemporaryFile Create(
        bool template = false,
        string extension = ".docx",
        string? documentXml = null,
        string? contentTypesXml = null,
        string? documentRelationshipsXml = null,
        string? rootRelationshipsXml = null,
        Action<ZipArchive>? configure = null)
    {
        var temporary = TemporaryFile.Empty(extension);
        File.Delete(temporary.Path);
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "[Content_Types].xml",
                contentTypesXml ?? ContentTypes(template
                    ? "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml"
                    : "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
                CompressionLevel.NoCompression);
            WriteEntry(
                archive,
                "_rels/.rels",
                rootRelationshipsXml ?? Relationships(
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>"),
                CompressionLevel.NoCompression);
            WriteEntry(
                archive,
                "word/document.xml",
                documentXml ?? DocumentXml("<w:p><w:r><w:t>Hello</w:t></w:r></w:p>"),
                CompressionLevel.NoCompression);
            if (documentRelationshipsXml is not null)
            {
                WriteEntry(
                    archive,
                    "word/_rels/document.xml.rels",
                    documentRelationshipsXml,
                    CompressionLevel.NoCompression);
            }

            configure?.Invoke(archive);
        }

        return temporary;
    }

    public static string ContentTypes(string mainContentType) => $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="{{mainContentType}}"/>
        </Types>
        """;

    public static string ContentTypesWithImage(string extension)
    {
        var mediaType = extension switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            _ => "application/octet-stream",
        };
        return ContentTypes("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")
            .Replace("</Types>", $"<Default Extension=\"{extension}\" ContentType=\"{mediaType}\"/></Types>", StringComparison.Ordinal);
    }

    public static string DocumentXml(string body) => $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:document xmlns:w="{{MainNamespace}}"><w:body>{{body}}<w:sectPr/></w:body></w:document>
        """;

    public static string Relationships(string body) => $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{{body}}</Relationships>
        """;

    public static byte[] Png(int width, int height) => RenderablePng(width, height);

    public static byte[] PngEnvelope(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;
        header[9] = 2;
        WritePngChunk(png, "IHDR"u8, header);
        WritePngChunk(png, "IDAT"u8, [0x78, 0x9c, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        WritePngChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    public static byte[] RenderablePng(int width, int height)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                raw.WriteByte((byte)(30 + (180 * x / Math.Max(1, width - 1))));
                raw.WriteByte((byte)(70 + (130 * y / Math.Max(1, height - 1))));
                raw.WriteByte((byte)(180 - (100 * x / Math.Max(1, width - 1))));
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;
        header[9] = 2;
        WritePngChunk(png, "IHDR"u8, header);
        WritePngChunk(png, "IDAT"u8, compressed.ToArray());
        WritePngChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    public static byte[] Jpeg(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xC0, 0x00, 0x0B, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
        0x00,
        0xFF, 0xD9,
    ];

    public static void WriteEntry(ZipArchive archive, string name, string text, CompressionLevel compression) =>
        WriteEntry(archive, name, Encoding.UTF8.GetBytes(text), compression);

    public static void WriteEntry(ZipArchive archive, string name, byte[] bytes, CompressionLevel compression)
    {
        var entry = archive.CreateEntry(name, compression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static void WritePngChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        destination.Write(length);
        destination.Write(type);
        destination.Write(data);
        var checksumInput = new byte[type.Length + data.Length];
        type.CopyTo(checksumInput);
        data.CopyTo(checksumInput.AsSpan(type.Length));
        BinaryPrimitives.WriteUInt32BigEndian(length, PngCrc32(checksumInput));
        destination.Write(length);
    }

    private static uint PngCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}

internal sealed class TemporaryFile : IDisposable
{
    private TemporaryFile(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryFile Empty(string extension)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"word-mcp-test-{Guid.NewGuid():N}{extension}");
        using (File.Create(path))
        {
        }

        return new TemporaryFile(path);
    }

    public static TemporaryFile WithBytes(string extension, byte[] bytes)
    {
        var file = Empty(extension);
        File.WriteAllBytes(file.Path, bytes);
        return file;
    }

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
