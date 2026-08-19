using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Storage;

public sealed record DocxPackageInspection(
    bool IsTemplate,
    int BlockCount,
    int CharacterCount,
    int TableCellCount,
    int ImageCount,
    int ExplicitPageBreakCount,
    int FieldCount,
    bool HasTocField,
    IReadOnlyList<string> PassiveHyperlinks);

public sealed record ImageInspection(string MediaType, int Width, int Height, long Bytes, long Pixels);

/// <summary>Fail-closed validation for immutable DOCX/DOTX job snapshots.</summary>
public sealed class DocxPackageGuard(IOptions<WordMcpOptions> options)
{
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string WordprocessingDrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string VmlNamespace = "urn:schemas-microsoft-com:vml";
    private const string HyperlinkRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    private const string DocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string TemplateContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml";
    private const int ExternalTargetMaximumLength = 2_048;
    private const long RatioCheckMinimumBytes = 1L * 1024 * 1024;

    private static readonly HashSet<string> AllowedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAGE",
        "NUMPAGES",
        "SECTION",
        "SECTIONPAGES",
        "TOC",
        "REF",
        "PAGEREF",
        "SEQ",
        "STYLEREF",
        "DATE",
        "TIME",
    };

    private readonly WordMcpOptions settings = options.Value;

    /// <summary>Worker-facing API. The path must be the immutable snapshot recorded on the job.</summary>
    public Task<DocxPackageInspection> ValidateSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken) =>
        ValidateSnapshotCoreAsync(snapshotPath, expectedSha256: null, cancellationToken);

    public Task<DocxPackageInspection> ValidateSnapshotAsync(
        string snapshotPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ValidateExpectedHash(expectedSha256);
        return ValidateSnapshotCoreAsync(snapshotPath, expectedSha256, cancellationToken);
    }

    private async Task<DocxPackageInspection> ValidateSnapshotCoreAsync(
        string snapshotPath,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        byte[] packageBytes;
        try
        {
            await using var input = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length <= 0 || input.Length > settings.MaxFileBytes)
            {
                throw Unsafe("file_size_limit", "The Word package exceeds the accepted file size.");
            }

            packageBytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(packageBytes, cancellationToken).ConfigureAwait(false);
            if (input.Length != packageBytes.LongLength)
            {
                throw Unsafe("input_changed", "The Word snapshot changed while it was being inspected.");
            }
        }
        catch (WordMcpException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Unsafe("invalid_docx", "The Word snapshot could not be read.", exception);
        }

        ValidateSnapshotHash(packageBytes, expectedSha256);
        cancellationToken.ThrowIfCancellationRequested();
        if (HasCompoundFileSignature(packageBytes))
        {
            throw Unsafe("encrypted_document", "Encrypted or compound Word containers are not accepted.");
        }

        if (ContainsEncryptedZipEntry(packageBytes))
        {
            throw Unsafe("encrypted_document", "Encrypted ZIP entries are not accepted.");
        }

        try
        {
            using var packageStream = new MemoryStream(packageBytes, writable: false);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = ValidateEnvelope(archive, cancellationToken);
            RejectDangerousEntryNames(entries);
            var xmlParts = ReadXmlParts(entries, cancellationToken);
            var contentTypes = ParseContentTypes(xmlParts, entries);
            RejectActiveContent(entries, contentTypes);
            RejectAltChunk(xmlParts);
            var hyperlinks = ValidateRelationships(xmlParts, entries);
            var fields = ValidateFields(xmlParts);
            ValidateDocumentIdentifiersAndRevisions(xmlParts);
            var imageSummary = ValidatePackageImages(entries, contentTypes, cancellationToken);
            var semanticSummary = ValidateSemanticLimits(xmlParts, imageSummary.Count);
            var isTemplate = ValidateMainDocumentType(contentTypes, snapshotPath);
            ValidateOpenXmlReopen(packageBytes);

            return new DocxPackageInspection(
                isTemplate,
                semanticSummary.Blocks,
                semanticSummary.Characters,
                semanticSummary.TableCells,
                semanticSummary.Images,
                semanticSummary.ExplicitPageBreaks,
                fields.Count,
                fields.HasToc,
                hyperlinks.AsReadOnly());
        }
        catch (WordMcpException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw Unsafe("invalid_zip", "The Word package is not a valid ZIP package.", exception);
        }
    }

    // Compatibility name for callers that already distinguish source files by method name.
    public Task<DocxPackageInspection> ValidateDocumentAsync(
        string snapshotPath,
        CancellationToken cancellationToken) =>
        ValidateSnapshotAsync(snapshotPath, cancellationToken);

    public Task<ImageInspection> ValidateImageSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken) =>
        ValidateImageSnapshotCoreAsync(snapshotPath, expectedSha256: null, cancellationToken);

    public Task<ImageInspection> ValidateImageSnapshotAsync(
        string snapshotPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ValidateExpectedHash(expectedSha256);
        return ValidateImageSnapshotCoreAsync(snapshotPath, expectedSha256, cancellationToken);
    }

    private async Task<ImageInspection> ValidateImageSnapshotCoreAsync(
        string snapshotPath,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            await using var input = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length <= 0 || input.Length > settings.MaxImageBytes || input.Length > settings.MaxFileBytes)
            {
                throw Unsafe("image_limit", "The image exceeds the accepted file size.");
            }

            bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (WordMcpException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Unsafe("invalid_image", "The image snapshot could not be read.", exception);
        }

        ValidateSnapshotHash(bytes, expectedSha256);
        return ValidateImage(bytes, Path.GetExtension(snapshotPath));
    }

    private Dictionary<string, ZipArchiveEntry> ValidateEnvelope(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count == 0)
        {
            throw Unsafe("invalid_zip", "The Word package is empty.");
        }

        if (archive.Entries.Count > settings.MaxZipEntries)
        {
            throw Unsafe("zip_entry_limit", "The Word package contains too many ZIP entries.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedName = NormalizeEntryName(entry.FullName);
            if (!entries.TryAdd(normalizedName, entry))
            {
                throw Unsafe("duplicate_zip_entry", "The Word package contains duplicate normalized ZIP entries.");
            }

            expandedBytes = CheckedAdd(expandedBytes, entry.Length, "zip_expansion_limit");
            if (expandedBytes > settings.MaxUncompressedBytes)
            {
                throw Unsafe("zip_expansion_limit", "The expanded Word package is too large.");
            }

            if (entry.Length >= RatioCheckMinimumBytes)
            {
                if (entry.CompressedLength == 0
                    || entry.Length > CheckedMultiply(entry.CompressedLength, settings.MaxCompressionRatio))
                {
                    throw Unsafe("zip_compression_ratio", "A ZIP entry exceeds the accepted compression ratio.");
                }
            }

            if (IsXmlPart(normalizedName) && entry.Length > settings.MaxXmlPartBytes)
            {
                throw Unsafe("xml_part_limit", "An XML part exceeds the accepted size.");
            }
        }

        if (!entries.ContainsKey("[Content_Types].xml"))
        {
            throw Unsafe("invalid_docx", "The Word package has no content type manifest.");
        }

        return entries;
    }

    private Dictionary<string, XDocument> ReadXmlParts(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in entries)
        {
            if (!IsXmlPart(name))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.Length < 0 || entry.Length > settings.MaxXmlPartBytes || entry.Length > int.MaxValue)
                {
                    throw Unsafe("xml_part_limit", "An XML part exceeds the accepted size.");
                }

                using var stream = entry.Open();
                var bytes = new byte[checked((int)entry.Length)];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1)
                {
                    throw Unsafe("invalid_xml", "An XML part length is inconsistent.");
                }

                using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = settings.MaxXmlPartBytes,
                    IgnoreComments = false,
                    IgnoreWhitespace = false,
                    CloseInput = false,
                });
                while (reader.Read())
                {
                    if (reader.Depth > settings.MaxXmlDepth)
                    {
                        throw Unsafe("xml_depth_limit", "An XML part exceeds the accepted nesting depth.");
                    }

                    if (reader.NodeType == XmlNodeType.Element
                        && reader.AttributeCount > settings.MaxXmlAttributesPerElement)
                    {
                        throw Unsafe("xml_attribute_limit", "An XML element contains too many attributes.");
                    }
                }

                using var documentReader = XmlReader.Create(new MemoryStream(bytes, writable: false), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = settings.MaxXmlPartBytes,
                });
                var document = XDocument.Load(
                    documentReader,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                result.Add(name, document);
            }
            catch (WordMcpException)
            {
                throw;
            }
            catch (Exception exception) when (exception is XmlException or InvalidDataException or IOException)
            {
                throw Unsafe("invalid_xml", "A Word XML part is malformed or unsafe.", exception);
            }
        }

        return result;
    }

    private static ContentTypeMap ParseContentTypes(
        Dictionary<string, XDocument> xmlParts,
        Dictionary<string, ZipArchiveEntry> entries)
    {
        if (!xmlParts.TryGetValue("[Content_Types].xml", out var document)
            || document.Root?.Name != XName.Get("Types", ContentTypesNamespace))
        {
            throw Unsafe("invalid_content_types", "The content type manifest is malformed.");
        }

        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Root.Elements())
        {
            if (element.Name == XName.Get("Default", ContentTypesNamespace))
            {
                var extension = element.Attribute("Extension")?.Value.TrimStart('.');
                var contentType = element.Attribute("ContentType")?.Value;
                if (string.IsNullOrWhiteSpace(extension)
                    || extension.Any(character => !char.IsAsciiLetterOrDigit(character))
                    || string.IsNullOrWhiteSpace(contentType)
                    || !defaults.TryAdd(extension, contentType))
                {
                    throw Unsafe("invalid_content_types", "The content type manifest has an invalid default.");
                }
            }
            else if (element.Name == XName.Get("Override", ContentTypesNamespace))
            {
                var partName = element.Attribute("PartName")?.Value;
                var contentType = element.Attribute("ContentType")?.Value;
                if (string.IsNullOrWhiteSpace(partName)
                    || string.IsNullOrWhiteSpace(contentType)
                    || !partName.StartsWith('/'))
                {
                    throw Unsafe("invalid_content_types", "The content type manifest has an invalid override.");
                }

                var normalized = NormalizeEntryName(partName[1..]);
                if (!overrides.TryAdd(normalized, contentType))
                {
                    throw Unsafe("invalid_content_types", "The content type manifest has duplicate overrides.");
                }
            }
            else
            {
                throw Unsafe("invalid_content_types", "The content type manifest contains an unknown element.");
            }
        }

        var map = new ContentTypeMap(defaults, overrides);
        foreach (var name in entries.Keys)
        {
            if (name == "[Content_Types].xml" || name.EndsWith('/'))
            {
                continue;
            }

            if (map.Get(name) is null)
            {
                throw Unsafe("missing_content_type", "A Word package part has no declared content type.");
            }
        }

        return map;
    }

    private static void RejectActiveContent(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        ContentTypeMap contentTypes)
    {
        foreach (var name in entries.Keys)
        {
            var lowerName = name.ToLowerInvariant();
            if (lowerName.EndsWith("vbaproject.bin", StringComparison.Ordinal)
                || lowerName.Contains("/activex/", StringComparison.Ordinal))
            {
                throw Unsafe("active_content", "The Word package contains active content.");
            }

            if (lowerName.Contains("/embeddings/", StringComparison.Ordinal))
            {
                throw Unsafe("embedded_object", "The Word package contains an embedded object.");
            }

            var contentType = contentTypes.Get(name)?.ToLowerInvariant();
            if (contentType is null)
            {
                continue;
            }

            if (contentType.Contains("macroenabled", StringComparison.Ordinal)
                || contentType.Contains("vbaproject", StringComparison.Ordinal)
                || contentType.Contains("activex", StringComparison.Ordinal))
            {
                throw Unsafe("active_content", "The Word package declares active content.");
            }

            if (contentType.Contains("oleobject", StringComparison.Ordinal)
                || contentType.Contains("officedocument.package", StringComparison.Ordinal))
            {
                throw Unsafe("embedded_object", "The Word package declares an embedded object.");
            }
        }
    }

    private static void RejectDangerousEntryNames(IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        foreach (var name in entries.Keys)
        {
            var lowerName = name.ToLowerInvariant();
            if (lowerName.EndsWith("vbaproject.bin", StringComparison.Ordinal)
                || lowerName.Contains("/activex/", StringComparison.Ordinal))
            {
                throw Unsafe("active_content", "The Word package contains active content.");
            }

            if (lowerName.Contains("/embeddings/", StringComparison.Ordinal))
            {
                throw Unsafe("embedded_object", "The Word package contains an embedded object.");
            }
        }
    }

    private static void RejectAltChunk(IReadOnlyDictionary<string, XDocument> xmlParts)
    {
        var altChunk = XName.Get("altChunk", WordNamespace);
        if (xmlParts.Values.Any(document => document.Descendants(altChunk).Any()))
        {
            throw Unsafe("alt_chunk", "The Word package contains altChunk content.");
        }
    }

    private List<string> ValidateRelationships(
        Dictionary<string, XDocument> xmlParts,
        Dictionary<string, ZipArchiveEntry> entries)
    {
        var passiveHyperlinks = new List<string>();
        var relationshipIdsBySource = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relationshipName, document) in xmlParts.Where(item => item.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            if (document.Root?.Name != XName.Get("Relationships", RelationshipsNamespace))
            {
                throw Unsafe("invalid_relationship", "A package relationship part is malformed.");
            }

            var relationships = document.Root.Elements(XName.Get("Relationship", RelationshipsNamespace)).ToList();
            if (relationships.Count > settings.MaxRelationshipsPerPart)
            {
                throw Unsafe("relationship_limit", "A package part contains too many relationships.");
            }

            if (document.Root.Elements().Count() != relationships.Count)
            {
                throw Unsafe("invalid_relationship", "A package relationship part contains an unknown element.");
            }

            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var sourcePart = RelationshipSourcePart(relationshipName);
            if (sourcePart.Length > 0 && !entries.ContainsKey(sourcePart))
            {
                throw Unsafe("invalid_relationship", "A relationship part has no owning source part.");
            }

            foreach (var relationship in relationships)
            {
                var identifier = relationship.Attribute("Id")?.Value;
                var type = relationship.Attribute("Type")?.Value;
                var target = relationship.Attribute("Target")?.Value;
                var targetMode = relationship.Attribute("TargetMode")?.Value;
                if (string.IsNullOrWhiteSpace(identifier)
                    || !identifiers.Add(identifier)
                    || string.IsNullOrWhiteSpace(type)
                    || string.IsNullOrWhiteSpace(target))
                {
                    throw Unsafe("invalid_relationship", "A package relationship is incomplete or duplicated.");
                }

                RejectDangerousRelationshipType(type);
                if (string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(type, HyperlinkRelationship, StringComparison.Ordinal)
                        || !IsPassiveHyperlink(target))
                    {
                        throw Unsafe("external_relationship", "Only passive HTTP(S) or mailto hyperlinks are accepted.");
                    }

                    passiveHyperlinks.Add(target);
                    continue;
                }

                if (!string.IsNullOrEmpty(targetMode))
                {
                    throw Unsafe("invalid_relationship", "A package relationship has an unsupported target mode.");
                }

                var resolvedTarget = ResolveInternalTarget(relationshipName, target);
                if (!entries.ContainsKey(resolvedTarget))
                {
                    throw Unsafe("invalid_relationship", "A package relationship targets a missing part.");
                }
            }

            relationshipIdsBySource[sourcePart] = identifiers;
        }

        foreach (var (partName, document) in xmlParts.Where(item =>
                     !item.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var reference in (document.Root?.DescendantsAndSelf() ?? Enumerable.Empty<XElement>())
                         .Attributes()
                         .Where(attribute => attribute.Name.NamespaceName == OfficeRelationshipsNamespace
                                             && attribute.Name.LocalName is "id" or "embed" or "link"))
            {
                if (string.IsNullOrWhiteSpace(reference.Value)
                    || !relationshipIdsBySource.TryGetValue(partName, out var identifiers)
                    || !identifiers.Contains(reference.Value))
                {
                    throw Unsafe("invalid_relationship", "An XML relationship reference has no matching relationship.");
                }
            }
        }

        return passiveHyperlinks;
    }

    private static void RejectDangerousRelationshipType(string type)
    {
        var lower = type.ToLowerInvariant();
        if (lower.Contains("vbaproject", StringComparison.Ordinal)
            || lower.Contains("activex", StringComparison.Ordinal))
        {
            throw Unsafe("active_content", "The Word package contains an active relationship.");
        }

        if (lower.Contains("oleobject", StringComparison.Ordinal)
            || lower.EndsWith("/package", StringComparison.Ordinal))
        {
            throw Unsafe("embedded_object", "The Word package contains an embedded relationship.");
        }

        if (lower.Contains("altchunk", StringComparison.Ordinal)
            || lower.Contains("afchunk", StringComparison.Ordinal))
        {
            throw Unsafe("alt_chunk", "The Word package contains altChunk content.");
        }
    }

    private static bool IsPassiveHyperlink(string target)
    {
        string decodedTarget;
        try
        {
            decodedTarget = Uri.UnescapeDataString(target);
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            return false;
        }

        if (target.Length is 0 or > ExternalTargetMaximumLength
            || target.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || decodedTarget.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || target.Contains('\\')
            || !Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return false;
        }

        if (uri.Scheme is "http" or "https")
        {
            return string.IsNullOrEmpty(uri.UserInfo)
                   && !string.IsNullOrEmpty(uri.Host)
                   && uri.AbsoluteUri == target;
        }

        return target.Length > "mailto:".Length
               && target.StartsWith("mailto:", StringComparison.Ordinal)
               && uri.AbsoluteUri == target;
    }

    private static string ResolveInternalTarget(string relationshipPartName, string target)
    {
        if (target.Any(character => char.IsControl(character)) || target.Contains('\\'))
        {
            throw Unsafe("invalid_relationship", "A package relationship target is malformed.");
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(target.Normalize(NormalizationForm.FormC));
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            throw Unsafe("invalid_relationship", "A package relationship target is malformed.", exception);
        }

        var fragmentIndex = decoded.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            decoded = decoded[..fragmentIndex];
        }

        if (decoded.Length == 0 || decoded.Contains('?'))
        {
            throw Unsafe("invalid_relationship", "A package relationship target is malformed.");
        }

        var sourcePart = RelationshipSourcePart(relationshipPartName);
        var baseSegments = sourcePart.Length == 0
            ? new List<string>()
            : sourcePart.Split('/').SkipLast(1).ToList();
        if (decoded.StartsWith('/'))
        {
            baseSegments.Clear();
            decoded = decoded[1..];
        }

        foreach (var segment in decoded.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (baseSegments.Count == 0)
                {
                    throw Unsafe("invalid_relationship", "A package relationship escapes the package root.");
                }

                baseSegments.RemoveAt(baseSegments.Count - 1);
            }
            else
            {
                if (segment.Contains(':'))
                {
                    throw Unsafe("invalid_relationship", "A package relationship target is malformed.");
                }

                baseSegments.Add(segment);
            }
        }

        if (baseSegments.Count == 0)
        {
            throw Unsafe("invalid_relationship", "A package relationship does not identify a part.");
        }

        return NormalizeEntryName(string.Join('/', baseSegments));
    }

    private static string RelationshipSourcePart(string relationshipPartName)
    {
        if (relationshipPartName == "_rels/.rels")
        {
            return string.Empty;
        }

        const string marker = "/_rels/";
        var markerIndex = relationshipPartName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || !relationshipPartName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
        {
            throw Unsafe("invalid_relationship", "A package relationship part has an invalid location.");
        }

        var directory = relationshipPartName[..markerIndex];
        var sourceName = relationshipPartName[(markerIndex + marker.Length)..^5];
        if (sourceName.Length == 0)
        {
            throw Unsafe("invalid_relationship", "A package relationship part has an invalid location.");
        }

        return string.Concat(directory, "/", sourceName);
    }

    private static FieldSummary ValidateFields(IReadOnlyDictionary<string, XDocument> xmlParts)
    {
        var word = (XNamespace)WordNamespace;
        var fieldCount = 0;
        var hasToc = false;
        foreach (var document in xmlParts
                     .Where(item => item.Key.StartsWith("word/", StringComparison.OrdinalIgnoreCase))
                     .Select(item => item.Value))
        {
            foreach (var simpleField in document.Descendants(word + "fldSimple"))
            {
                var instruction = simpleField.Attribute(word + "instr")?.Value;
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    throw Unsafe("invalid_field_structure", "A simple Word field has no instruction.");
                }

                hasToc |= string.Equals(
                    ValidateFieldInstruction(instruction),
                    "TOC",
                    StringComparison.OrdinalIgnoreCase);
                fieldCount++;
            }

            StringBuilder? instructionBuilder = null;
            var separated = false;
            foreach (var element in document.Descendants())
            {
                if (element.Name == word + "fldChar")
                {
                    var fieldType = element.Attribute(word + "fldCharType")?.Value;
                    switch (fieldType)
                    {
                        case "begin":
                            if (instructionBuilder is not null)
                            {
                                throw Unsafe("invalid_field_structure", "Nested complex Word fields are not accepted.");
                            }

                            instructionBuilder = new StringBuilder();
                            separated = false;
                            break;
                        case "separate":
                            if (instructionBuilder is null || separated)
                            {
                                throw Unsafe("invalid_field_structure", "A complex Word field has an invalid separator.");
                            }

                            separated = true;
                            break;
                        case "end":
                            if (instructionBuilder is null)
                            {
                                throw Unsafe("invalid_field_structure", "A complex Word field has an unmatched end marker.");
                            }

                            hasToc |= string.Equals(
                                ValidateFieldInstruction(instructionBuilder.ToString()),
                                "TOC",
                                StringComparison.OrdinalIgnoreCase);
                            fieldCount++;
                            instructionBuilder = null;
                            separated = false;
                            break;
                        default:
                            throw Unsafe("invalid_field_structure", "A complex Word field has an unknown marker.");
                    }
                }
                else if (element.Name == word + "instrText")
                {
                    if (instructionBuilder is null || separated)
                    {
                        if (!string.IsNullOrWhiteSpace(element.Value))
                        {
                            throw Unsafe("invalid_field_structure", "A Word field instruction is outside a field boundary.");
                        }
                    }
                    else
                    {
                        instructionBuilder.Append(element.Value);
                    }
                }
            }

            if (instructionBuilder is not null)
            {
                throw Unsafe("invalid_field_structure", "A complex Word field is unterminated.");
            }
        }

        return new FieldSummary(fieldCount, hasToc);
    }

    private static string ValidateFieldInstruction(string instruction)
    {
        if (instruction.Length > 512
            || instruction.Any(character => char.IsControl(character) && character != '\t'))
        {
            throw Unsafe("invalid_field_structure", "A Word field instruction is malformed or too long.");
        }

        var trimmed = instruction.Trim();
        var tokenEnd = 0;
        while (tokenEnd < trimmed.Length && char.IsAsciiLetter(trimmed[tokenEnd]))
        {
            tokenEnd++;
        }

        if (tokenEnd == 0
            || (tokenEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[tokenEnd]))
            || !AllowedFields.Contains(trimmed[..tokenEnd]))
        {
            throw Unsafe("unsafe_field", "The Word package contains a field outside the fixed allowlist.");
        }

        var fieldType = trimmed[..tokenEnd].ToUpperInvariant();
        ValidateFieldArguments(fieldType, TokenizeFieldArguments(trimmed[tokenEnd..]));
        return fieldType;
    }

    private static void ValidateDocumentIdentifiersAndRevisions(
        IReadOnlyDictionary<string, XDocument> xmlParts)
    {
        var word = (XNamespace)WordNamespace;
        var wordprocessingDrawing = (XNamespace)WordprocessingDrawingNamespace;
        var drawingIds = new HashSet<uint>();
        foreach (var document in xmlParts.Values)
        {
            foreach (var drawing in document.Descendants(wordprocessingDrawing + "docPr"))
            {
                if (!uint.TryParse(
                        drawing.Attribute("id")?.Value,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var identifier)
                    || identifier == 0
                    || !drawingIds.Add(identifier))
                {
                    throw Unsafe("invalid_identifier_structure", "Drawing object identifiers must be unique positive integers package-wide.");
                }
            }
        }

        var comments = DefinitionIds(xmlParts, "word/comments.xml", word + "comment", allowNegative: false);
        var footnotes = DefinitionIds(xmlParts, "word/footnotes.xml", word + "footnote", allowNegative: true);
        var endnotes = DefinitionIds(xmlParts, "word/endnotes.xml", word + "endnote", allowNegative: true);
        foreach (var (partName, document) in xmlParts.Where(item =>
                     item.Key.StartsWith("word/", StringComparison.OrdinalIgnoreCase)
                     && !item.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            ValidatePairedIds(document, word + "bookmarkStart", word + "bookmarkEnd", "bookmark");
            ValidatePairedIds(document, word + "commentRangeStart", word + "commentRangeEnd", "comment range");
            ValidatePairedIds(document, word + "moveFromRangeStart", word + "moveFromRangeEnd", "move-from range");
            ValidatePairedIds(document, word + "moveToRangeStart", word + "moveToRangeEnd", "move-to range");
            ValidatePairedIds(document, word + "customXmlInsRangeStart", word + "customXmlInsRangeEnd", "custom XML insertion range");
            ValidatePairedIds(document, word + "customXmlDelRangeStart", word + "customXmlDelRangeEnd", "custom XML deletion range");
            ValidatePairedIds(document, word + "customXmlMoveFromRangeStart", word + "customXmlMoveFromRangeEnd", "custom XML move-from range");
            ValidatePairedIds(document, word + "customXmlMoveToRangeStart", word + "customXmlMoveToRangeEnd", "custom XML move-to range");

            ValidateRevisionIds(document, word + "ins");
            ValidateRevisionIds(document, word + "del");
            ValidateRevisionIds(document, word + "moveFrom");
            ValidateRevisionIds(document, word + "moveTo");
            if (document.Descendants(word + "del")
                .Any(deletion => deletion.Descendants(word + "t").Any()))
            {
                throw Unsafe("invalid_revision_structure", "Deleted revisions must use deleted-text elements, not normal text elements.");
            }

            if (document.Descendants(word + "delText")
                .Any(text => !text.Ancestors(word + "del").Any()))
            {
                throw Unsafe("invalid_revision_structure", "Deleted-text elements must be contained by a deleted revision.");
            }

            ValidateReferences(document, word + "commentReference", comments, "comment");
            ValidateReferences(document, word + "commentRangeStart", comments, "comment");
            ValidateReferences(document, word + "commentRangeEnd", comments, "comment");
            if (!partName.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase))
            {
                ValidateReferences(document, word + "footnoteReference", footnotes, "footnote", ignoreNegative: true);
            }

            if (!partName.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase))
            {
                ValidateReferences(document, word + "endnoteReference", endnotes, "endnote", ignoreNegative: true);
            }
        }
    }

    private static HashSet<int> DefinitionIds(
        IReadOnlyDictionary<string, XDocument> xmlParts,
        string partName,
        XName definitionName,
        bool allowNegative)
    {
        var identifiers = new HashSet<int>();
        if (!xmlParts.TryGetValue(partName, out var document))
        {
            return identifiers;
        }

        var word = (XNamespace)WordNamespace;
        foreach (var definition in document.Descendants(definitionName))
        {
            if (!TryParseWordId(definition.Attribute(word + "id")?.Value, out var identifier)
                || (!allowNegative && identifier < 0)
                || !identifiers.Add(identifier))
            {
                throw Unsafe("invalid_identifier_structure", "A Word definition contains a missing or duplicate identifier.");
            }
        }

        return identifiers;
    }

    private static void ValidatePairedIds(
        XDocument document,
        XName startName,
        XName endName,
        string kind)
    {
        var word = (XNamespace)WordNamespace;
        var starts = ReadUniqueIds(document.Descendants(startName), word + "id", kind);
        var ends = ReadUniqueIds(document.Descendants(endName), word + "id", kind);
        if (!starts.SetEquals(ends))
        {
            throw Unsafe("invalid_identifier_structure", $"A {kind} start/end range is unmatched.");
        }
    }

    private static HashSet<int> ReadUniqueIds(
        IEnumerable<XElement> elements,
        XName attributeName,
        string kind)
    {
        var identifiers = new HashSet<int>();
        foreach (var element in elements)
        {
            if (!TryParseWordId(element.Attribute(attributeName)?.Value, out var identifier)
                || identifier < 0
                || !identifiers.Add(identifier))
            {
                throw Unsafe("invalid_identifier_structure", $"A {kind} contains a missing or duplicate identifier.");
            }
        }

        return identifiers;
    }

    private static void ValidateRevisionIds(XDocument document, XName revisionName)
    {
        var word = (XNamespace)WordNamespace;
        _ = ReadUniqueIds(
            document.Descendants(revisionName),
            word + "id",
            $"{revisionName.LocalName} revision");
    }

    private static void ValidateReferences(
        XDocument document,
        XName referenceName,
        HashSet<int> definitions,
        string kind,
        bool ignoreNegative = false)
    {
        var word = (XNamespace)WordNamespace;
        foreach (var reference in document.Descendants(referenceName))
        {
            if (!TryParseWordId(reference.Attribute(word + "id")?.Value, out var identifier)
                || (!ignoreNegative && identifier < 0)
                || (identifier >= 0 && !definitions.Contains(identifier)))
            {
                throw Unsafe("invalid_identifier_structure", $"A {kind} reference has no matching definition.");
            }
        }
    }

    private static bool TryParseWordId(string? value, out int identifier) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture,
            out identifier);

    private static List<FieldInstructionToken> TokenizeFieldArguments(string arguments)
    {
        var tokens = new List<FieldInstructionToken>();
        var cursor = 0;
        while (cursor < arguments.Length)
        {
            while (cursor < arguments.Length && char.IsWhiteSpace(arguments[cursor]))
            {
                cursor++;
            }

            if (cursor >= arguments.Length)
            {
                break;
            }

            if (arguments[cursor] == '"')
            {
                cursor++;
                var start = cursor;
                while (cursor < arguments.Length && arguments[cursor] != '"')
                {
                    if (arguments[cursor] == '\\' || char.IsControl(arguments[cursor]))
                    {
                        throw Unsafe("invalid_field_structure", "A quoted Word field argument is malformed.");
                    }

                    cursor++;
                }

                if (cursor >= arguments.Length || cursor == start || cursor - start > 128)
                {
                    throw Unsafe("invalid_field_structure", "A quoted Word field argument is malformed.");
                }

                tokens.Add(new FieldInstructionToken(arguments[start..cursor], Quoted: true, IsSwitch: false));
                cursor++;
                if (cursor < arguments.Length && !char.IsWhiteSpace(arguments[cursor]))
                {
                    throw Unsafe("invalid_field_structure", "A quoted Word field argument is not token-delimited.");
                }

                continue;
            }

            if (arguments[cursor] == '\\')
            {
                if (++cursor >= arguments.Length
                    || !(char.IsAsciiLetter(arguments[cursor]) || arguments[cursor] is '*' or '#' or '@'))
                {
                    throw Unsafe("invalid_field_structure", "A Word field switch is malformed.");
                }

                tokens.Add(new FieldInstructionToken(
                    string.Concat("\\", char.ToLowerInvariant(arguments[cursor])),
                    Quoted: false,
                    IsSwitch: true));
                cursor++;
                if (cursor < arguments.Length && !char.IsWhiteSpace(arguments[cursor]))
                {
                    throw Unsafe("invalid_field_structure", "A Word field switch is not token-delimited.");
                }

                continue;
            }

            var tokenStart = cursor;
            while (cursor < arguments.Length && !char.IsWhiteSpace(arguments[cursor]))
            {
                var character = arguments[cursor];
                if (!(char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))
                {
                    throw Unsafe("invalid_field_structure", "A Word field argument contains an unsupported token.");
                }

                cursor++;
            }

            if (cursor == tokenStart || cursor - tokenStart > 128)
            {
                throw Unsafe("invalid_field_structure", "A Word field argument is malformed or too long.");
            }

            tokens.Add(new FieldInstructionToken(arguments[tokenStart..cursor], Quoted: false, IsSwitch: false));
        }

        return tokens;
    }

    private static void ValidateFieldArguments(
        string fieldType,
        IReadOnlyList<FieldInstructionToken> tokens)
    {
        var positionalCount = 0;
        var switches = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!token.IsSwitch)
            {
                positionalCount++;
                continue;
            }

            if (!switches.Add(token.Value)
                || !TryGetFieldSwitch(fieldType, token.Value, out var argumentKind))
            {
                throw Unsafe("unsafe_field", "A Word field contains a switch outside its fixed grammar.");
            }

            if (argumentKind == FieldSwitchArgument.None)
            {
                continue;
            }

            if (++index >= tokens.Count || tokens[index].IsSwitch)
            {
                throw Unsafe("invalid_field_structure", "A Word field switch is missing its argument.");
            }

            ValidateFieldSwitchArgument(fieldType, token.Value, argumentKind, tokens[index]);
        }

        var validPositionalCount = fieldType switch
        {
            "REF" or "PAGEREF" or "SEQ" or "STYLEREF" => positionalCount == 1,
            _ => positionalCount == 0,
        };
        if (!validPositionalCount)
        {
            throw Unsafe("unsafe_field", "A Word field contains unexpected positional instructions.");
        }
    }

    private static bool TryGetFieldSwitch(
        string fieldType,
        string fieldSwitch,
        out FieldSwitchArgument argumentKind)
    {
        argumentKind = FieldSwitchArgument.None;
        switch (fieldType)
        {
            case "PAGE":
            case "NUMPAGES":
            case "SECTION":
            case "SECTIONPAGES":
                if (fieldSwitch is "\\*" or "\\#")
                {
                    argumentKind = fieldSwitch == "\\*" ? FieldSwitchArgument.NumberFormat : FieldSwitchArgument.Picture;
                    return true;
                }

                return false;
            case "TOC":
                if (fieldSwitch == "\\o")
                {
                    argumentKind = FieldSwitchArgument.TocLevels;
                    return true;
                }

                return fieldSwitch is "\\h" or "\\z" or "\\u";
            case "REF":
                if (fieldSwitch == "\\*")
                {
                    argumentKind = FieldSwitchArgument.NumberFormat;
                    return true;
                }

                return fieldSwitch is "\\h" or "\\p" or "\\n" or "\\w" or "\\r" or "\\f";
            case "PAGEREF":
                if (fieldSwitch == "\\*")
                {
                    argumentKind = FieldSwitchArgument.NumberFormat;
                    return true;
                }

                return fieldSwitch is "\\h" or "\\p";
            case "SEQ":
                if (fieldSwitch is "\\r" or "\\s")
                {
                    argumentKind = FieldSwitchArgument.Integer;
                    return true;
                }

                if (fieldSwitch == "\\*")
                {
                    argumentKind = FieldSwitchArgument.NumberFormat;
                    return true;
                }

                return fieldSwitch is "\\c" or "\\h" or "\\n";
            case "STYLEREF":
                if (fieldSwitch == "\\*")
                {
                    argumentKind = FieldSwitchArgument.NumberFormat;
                    return true;
                }

                return fieldSwitch is "\\l" or "\\n" or "\\p" or "\\r" or "\\t" or "\\w" or "\\s";
            case "DATE":
            case "TIME":
                if (fieldSwitch == "\\@")
                {
                    argumentKind = FieldSwitchArgument.DateTimePicture;
                    return true;
                }

                if (fieldSwitch == "\\*")
                {
                    argumentKind = FieldSwitchArgument.NumberFormat;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static void ValidateFieldSwitchArgument(
        string fieldType,
        string fieldSwitch,
        FieldSwitchArgument argumentKind,
        FieldInstructionToken token)
    {
        var valid = argumentKind switch
        {
            FieldSwitchArgument.TocLevels => token.Quoted
                                             && token.Value.Length == 3
                                             && token.Value[0] is >= '1' and <= '9'
                                             && token.Value[1] == '-'
                                             && token.Value[2] is >= '1' and <= '9'
                                             && token.Value[0] <= token.Value[2],
            FieldSwitchArgument.Integer => !token.Quoted
                                           && int.TryParse(
                                               token.Value,
                                               System.Globalization.NumberStyles.None,
                                               System.Globalization.CultureInfo.InvariantCulture,
                                               out var number)
                                           && number is >= 0 and <= 32_767,
            FieldSwitchArgument.NumberFormat => !token.Quoted
                                                && token.Value is "ARABIC" or "alphabetic" or "ALPHABETIC"
                                                    or "roman" or "ROMAN" or "MERGEFORMAT" or "CHARFORMAT",
            FieldSwitchArgument.DateTimePicture => token.Quoted,
            FieldSwitchArgument.Picture => token.Quoted,
            _ => false,
        };
        if (!valid)
        {
            throw Unsafe(
                "invalid_field_structure",
                $"The {fieldType} field has an invalid {fieldSwitch} switch argument.");
        }
    }

    private ImageSummary ValidatePackageImages(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        ContentTypeMap contentTypes,
        CancellationToken cancellationToken)
    {
        var count = 0;
        long totalBytes = 0;
        long totalPixels = 0;
        foreach (var (name, entry) in entries)
        {
            var contentType = contentTypes.Get(name);
            if (!name.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)
                && !(contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length <= 0 || entry.Length > settings.MaxImageBytes || entry.Length > int.MaxValue)
            {
                throw Unsafe("image_limit", "An embedded image exceeds the accepted size.");
            }

            byte[] bytes;
            try
            {
                using var stream = entry.Open();
                bytes = new byte[checked((int)entry.Length)];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1)
                {
                    throw Unsafe("invalid_image", "An embedded image length is inconsistent.");
                }
            }
            catch (WordMcpException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                throw Unsafe("invalid_image", "An embedded image is malformed.", exception);
            }

            var inspection = ValidateImage(bytes, Path.GetExtension(name), contentType);
            count++;
            totalBytes = CheckedAdd(totalBytes, inspection.Bytes, "image_limit");
            totalPixels = CheckedAdd(totalPixels, inspection.Pixels, "image_limit");
            if (count > settings.MaxImages
                || totalBytes > settings.MaxTotalImageBytes
                || totalPixels > settings.MaxTotalImagePixels)
            {
                throw Unsafe("image_limit", "The embedded images exceed an aggregate safety limit.");
            }
        }

        return new ImageSummary(count, totalBytes, totalPixels);
    }

    private ImageInspection ValidateImage(byte[] bytes, string extension, string? declaredContentType = null)
    {
        extension = extension.ToLowerInvariant();
        var isPng = extension == ".png";
        var isJpeg = extension is ".jpg" or ".jpeg";
        if (!isPng && !isJpeg)
        {
            throw Unsafe("unsupported_media", "Only embedded PNG and JPEG images are accepted.");
        }

        var expectedContentType = isPng ? "image/png" : "image/jpeg";
        if (declaredContentType is not null
            && !string.Equals(declaredContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw Unsafe("unsupported_media", "An embedded image content type does not match its format.");
        }

        (int Width, int Height) dimensions;
        try
        {
            dimensions = isPng ? ReadPngDimensions(bytes) : ReadJpegDimensions(bytes);
        }
        catch (WordMcpException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException
                                          or OverflowException
                                          or InvalidDataException
                                          or IOException)
        {
            throw Unsafe("invalid_image", "An embedded image header is malformed.", exception);
        }

        var pixels = checked((long)dimensions.Width * dimensions.Height);
        if (bytes.LongLength > settings.MaxImageBytes
            || dimensions.Width > settings.MaxImageDimension
            || dimensions.Height > settings.MaxImageDimension
            || pixels > settings.MaxImagePixels)
        {
            throw Unsafe("image_limit", "An embedded image exceeds a dimension or pixel safety limit.");
        }

        return new ImageInspection(expectedContentType, dimensions.Width, dimensions.Height, bytes.LongLength, pixels);
    }

    private (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (bytes.Length < 8 || !bytes[..8].SequenceEqual(signature))
        {
            throw Unsafe("invalid_image", "An embedded PNG header is malformed.");
        }

        var offset = 8;
        var chunkIndex = 0;
        var sawIdat = false;
        var sawIend = false;
        var idatEnded = false;
        var idatBytes = 0L;
        byte? firstIdatByte = null;
        byte? secondIdatByte = null;
        int? width = null;
        int? height = null;
        byte? bitDepth = null;
        byte? colorType = null;
        byte? interlaceMethod = null;
        var sawPalette = false;
        var paletteEntries = 0;
        using var compressedImageData = new MemoryStream();
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
            {
                throw Unsafe("invalid_image", "An embedded PNG has a truncated chunk.");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > int.MaxValue || length > (uint)(bytes.Length - offset - 12))
            {
                throw Unsafe("invalid_image", "An embedded PNG chunk length is invalid.");
            }

            var chunkLength = checked((int)length);
            var type = bytes.Slice(offset + 4, 4);
            if (!IsPngChunkType(type))
            {
                throw Unsafe("invalid_image", "An embedded PNG chunk type is invalid.");
            }

            var data = bytes.Slice(offset + 8, chunkLength);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + chunkLength, 4));
            if (PngCrc32(type, data) != expectedCrc)
            {
                throw Unsafe("invalid_image", "An embedded PNG chunk checksum is invalid.");
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (chunkIndex != 0 || width is not null || chunkLength != 13)
                {
                    throw Unsafe("invalid_image", "An embedded PNG has an invalid IHDR chunk.");
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                bitDepth = data[8];
                colorType = data[9];
                interlaceMethod = data[12];
                if (width <= 0
                    || height <= 0
                    || !IsValidPngBitDepth(bitDepth.Value, colorType.Value)
                    || data[10] != 0
                    || data[11] != 0
                    || interlaceMethod > 1)
                {
                    throw Unsafe("invalid_image", "An embedded PNG has invalid image parameters.");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (width is null || sawIdat || sawPalette || chunkLength is <= 0 or > 768 || chunkLength % 3 != 0)
                {
                    throw Unsafe("invalid_image", "An embedded PNG has an invalid palette.");
                }

                sawPalette = true;
                paletteEntries = chunkLength / 3;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (width is null || idatEnded)
                {
                    throw Unsafe("invalid_image", "An embedded PNG has an invalid IDAT sequence.");
                }

                sawIdat = true;
                idatBytes = checked(idatBytes + chunkLength);
                compressedImageData.Write(data);
                foreach (var value in data)
                {
                    if (firstIdatByte is null)
                    {
                        firstIdatByte = value;
                    }
                    else if (secondIdatByte is null)
                    {
                        secondIdatByte = value;
                        break;
                    }
                }
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (width is null || !sawIdat || chunkLength != 0 || offset + 12 != bytes.Length)
                {
                    throw Unsafe("invalid_image", "An embedded PNG has an invalid IEND chunk.");
                }

                sawIend = true;
            }
            else
            {
                if (type[0] is >= (byte)'A' and <= (byte)'Z')
                {
                    throw Unsafe("invalid_image", "An embedded PNG contains an unknown critical chunk.");
                }

                if (sawIdat)
                {
                    idatEnded = true;
                }
            }

            offset = checked(offset + 12 + chunkLength);
            chunkIndex++;
            if (sawIend)
            {
                break;
            }
        }

        if (width is null
            || height is null
            || !sawIend
            || idatBytes < 6
            || firstIdatByte is null
            || secondIdatByte is null
            || bitDepth is null
            || colorType is null
            || interlaceMethod is null
            || (firstIdatByte.Value & 0x0f) != 8
            || (firstIdatByte.Value >> 4) > 7
            || ((firstIdatByte.Value << 8) + secondIdatByte.Value) % 31 != 0
            || colorType == 3 && (!sawPalette || paletteEntries > 1 << bitDepth.Value)
            || (colorType is 0 or 4) && sawPalette)
        {
            throw Unsafe("invalid_image", "An embedded PNG is incomplete or has an invalid compressed stream header.");
        }

        var pixels = checked((long)width.Value * height.Value);
        if (width.Value > settings.MaxImageDimension
            || height.Value > settings.MaxImageDimension
            || pixels > settings.MaxImagePixels)
        {
            throw Unsafe("image_limit", "An embedded image exceeds a dimension or pixel safety limit.");
        }

        ValidatePngScanlines(
            compressedImageData.ToArray(),
            width.Value,
            height.Value,
            bitDepth.Value,
            colorType.Value,
            interlaceMethod.Value);
        return (width.Value, height.Value);
    }

    private static void ValidatePngScanlines(
        byte[] compressedImageData,
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte interlaceMethod)
    {
        var channels = colorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw Unsafe("invalid_image", "An embedded PNG has an unsupported color type."),
        };
        var bitsPerPixel = checked(channels * bitDepth);
        using var compressed = new MemoryStream(compressedImageData, writable: false);
        using var decompressed = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        Span<byte> buffer = stackalloc byte[8 * 1024];

        if (interlaceMethod == 0)
        {
            ValidatePngPass(decompressed, buffer, width, height, bitsPerPixel);
        }
        else
        {
            ReadOnlySpan<int> xStarts = [0, 4, 0, 2, 0, 1, 0];
            ReadOnlySpan<int> yStarts = [0, 0, 4, 0, 2, 0, 1];
            ReadOnlySpan<int> xSteps = [8, 8, 4, 4, 2, 2, 1];
            ReadOnlySpan<int> ySteps = [8, 8, 8, 4, 4, 2, 2];
            for (var pass = 0; pass < 7; pass++)
            {
                var passWidth = PngPassSize(width, xStarts[pass], xSteps[pass]);
                var passHeight = PngPassSize(height, yStarts[pass], ySteps[pass]);
                if (passWidth > 0 && passHeight > 0)
                {
                    ValidatePngPass(decompressed, buffer, passWidth, passHeight, bitsPerPixel);
                }
            }
        }

        if (decompressed.ReadByte() != -1)
        {
            throw Unsafe("invalid_image", "An embedded PNG expands beyond its declared scanlines.");
        }
    }

    private static void ValidatePngPass(
        Stream decompressed,
        Span<byte> buffer,
        int width,
        int height,
        int bitsPerPixel)
    {
        var rowBytes = checked((int)(((long)width * bitsPerPixel + 7) / 8));
        for (var row = 0; row < height; row++)
        {
            var filter = decompressed.ReadByte();
            if (filter is < 0 or > 4)
            {
                throw Unsafe("invalid_image", "An embedded PNG has a missing or invalid scanline filter.");
            }

            var remaining = rowBytes;
            while (remaining > 0)
            {
                var count = decompressed.Read(buffer[..Math.Min(buffer.Length, remaining)]);
                if (count <= 0)
                {
                    throw Unsafe("invalid_image", "An embedded PNG has truncated scanline data.");
                }

                remaining -= count;
            }
        }
    }

    private static int PngPassSize(int size, int start, int step) =>
        size <= start ? 0 : checked((size - start + step - 1) / step);

    private static bool IsPngChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var value in type)
        {
            if (!((value >= (byte)'A' && value <= (byte)'Z')
                  || (value >= (byte)'a' && value <= (byte)'z')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPngBitDepth(byte bitDepth, byte colorType) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        4 => bitDepth is 8 or 16,
        6 => bitDepth is 8 or 16,
        _ => false,
    };

    private static uint PngCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type)
        {
            crc = UpdatePngCrc32(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdatePngCrc32(crc, value);
        }

        return ~crc;
    }

    private static uint UpdatePngCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }

    private static (int Width, int Height) ReadJpegDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0xff || bytes[1] != 0xd8)
        {
            throw Unsafe("invalid_image", "An embedded JPEG header is malformed.");
        }

        var offset = 2;
        int? width = null;
        int? height = null;
        HashSet<byte>? frameComponents = null;
        var sawScan = false;
        var hasEndMarker = false;
        byte? pendingMarker = null;
        while (offset < bytes.Length)
        {
            byte marker;
            if (pendingMarker is { } pending)
            {
                marker = pending;
                pendingMarker = null;
            }
            else
            {
                if (bytes[offset] != 0xff)
                {
                    throw Unsafe("invalid_image", "An embedded JPEG has data outside an image scan.");
                }

                while (offset < bytes.Length && bytes[offset] == 0xff)
                {
                    offset++;
                }

                if (offset >= bytes.Length)
                {
                    throw Unsafe("invalid_image", "An embedded JPEG has a truncated marker.");
                }

                marker = bytes[offset++];
            }

            if (marker is 0x00 or 0xd8 or >= 0xd0 and <= 0xd7)
            {
                throw Unsafe("invalid_image", "An embedded JPEG has a marker in an invalid location.");
            }

            if (marker == 0xd9)
            {
                throw Unsafe("invalid_image", "An embedded JPEG ended outside a non-empty image scan.");
            }

            if (marker == 0x01)
            {
                continue;
            }

            if (offset + 2 > bytes.Length)
            {
                throw Unsafe("invalid_image", "An embedded JPEG segment is truncated.");
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length)
            {
                throw Unsafe("invalid_image", "An embedded JPEG segment length is invalid.");
            }

            if (IsStartOfFrame(marker))
            {
                if (width is not null || segmentLength < 11)
                {
                    throw Unsafe("invalid_image", "An embedded JPEG has an invalid or duplicate frame header.");
                }

                var precision = bytes[offset + 2];
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                var componentCount = bytes[offset + 7];
                if (precision is not (8 or 12)
                    || width <= 0
                    || height <= 0
                    || componentCount is < 1 or > 4
                    || segmentLength != 8 + (3 * componentCount))
                {
                    throw Unsafe("invalid_image", "An embedded JPEG frame header is malformed.");
                }

                frameComponents = new HashSet<byte>();
                for (var component = 0; component < componentCount; component++)
                {
                    var componentOffset = offset + 8 + (component * 3);
                    var identifier = bytes[componentOffset];
                    var sampling = bytes[componentOffset + 1];
                    var quantizationTable = bytes[componentOffset + 2];
                    if (!frameComponents.Add(identifier)
                        || (sampling >> 4) is < 1 or > 4
                        || (sampling & 0x0f) is < 1 or > 4
                        || quantizationTable > 3)
                    {
                        throw Unsafe("invalid_image", "An embedded JPEG frame component is invalid.");
                    }
                }
            }

            if (marker == 0xda)
            {
                if (frameComponents is null || segmentLength < 8)
                {
                    throw Unsafe("invalid_image", "An embedded JPEG scan has no valid frame.");
                }

                var scanComponentCount = bytes[offset + 2];
                if (scanComponentCount is < 1 or > 4
                    || segmentLength != 6 + (2 * scanComponentCount)
                    || scanComponentCount > frameComponents.Count)
                {
                    throw Unsafe("invalid_image", "An embedded JPEG scan header is malformed.");
                }

                var scanComponents = new HashSet<byte>();
                for (var component = 0; component < scanComponentCount; component++)
                {
                    var componentOffset = offset + 3 + (component * 2);
                    var identifier = bytes[componentOffset];
                    var tables = bytes[componentOffset + 1];
                    if (!frameComponents.Contains(identifier)
                        || !scanComponents.Add(identifier)
                        || (tables >> 4) > 3
                        || (tables & 0x0f) > 3)
                    {
                        throw Unsafe("invalid_image", "An embedded JPEG scan component is invalid.");
                    }
                }

                var spectralOffset = offset + 3 + (2 * scanComponentCount);
                var spectralStart = bytes[spectralOffset];
                var spectralEnd = bytes[spectralOffset + 1];
                var approximation = bytes[spectralOffset + 2];
                if (spectralStart > 63
                    || spectralEnd > 63
                    || spectralStart > spectralEnd
                    || (approximation >> 4) > 13
                    || (approximation & 0x0f) > 13)
                {
                    throw Unsafe("invalid_image", "An embedded JPEG scan parameters are invalid.");
                }

                sawScan = true;
                offset += segmentLength;
                var hasEntropyData = false;
                while (offset < bytes.Length)
                {
                    var value = bytes[offset++];
                    if (value != 0xff)
                    {
                        hasEntropyData = true;
                        continue;
                    }

                    while (offset < bytes.Length && bytes[offset] == 0xff)
                    {
                        offset++;
                    }

                    if (offset >= bytes.Length)
                    {
                        throw Unsafe("invalid_image", "An embedded JPEG scan is truncated.");
                    }

                    var scanMarker = bytes[offset++];
                    if (scanMarker == 0x00)
                    {
                        hasEntropyData = true;
                        continue;
                    }

                    if (scanMarker is >= 0xd0 and <= 0xd7)
                    {
                        continue;
                    }

                    if (!hasEntropyData)
                    {
                        throw Unsafe("invalid_image", "An embedded JPEG scan has no entropy data.");
                    }

                    if (scanMarker == 0xd9)
                    {
                        if (offset != bytes.Length)
                        {
                            throw Unsafe("invalid_image", "An embedded JPEG contains data after its end marker.");
                        }

                        hasEndMarker = true;
                    }
                    else
                    {
                        pendingMarker = scanMarker;
                    }

                    break;
                }

                if (hasEndMarker)
                {
                    break;
                }

                if (pendingMarker is null)
                {
                    throw Unsafe("invalid_image", "An embedded JPEG scan is unterminated.");
                }

                continue;
            }

            offset += segmentLength;
        }

        if (width is null or <= 0 || height is null or <= 0 || !sawScan || !hasEndMarker)
        {
            throw Unsafe("invalid_image", "An embedded JPEG is malformed, has no scan, or has no dimensions.");
        }

        return (width.Value, height.Value);
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private SemanticSummary ValidateSemanticLimits(
        IReadOnlyDictionary<string, XDocument> xmlParts,
        int imageCount)
    {
        var word = (XNamespace)WordNamespace;
        var drawing = (XNamespace)DrawingNamespace;
        var vml = (XNamespace)VmlNamespace;
        long blocks = 0;
        long characters = 0;
        long tableCells = 0;
        long explicitBreaks = 0;
        long imageReferences = 0;
        foreach (var (name, document) in xmlParts)
        {
            if (!name.StartsWith("word/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            blocks = CheckedAdd(blocks, document.Descendants(word + "p").LongCount(), "semantic_limit");
            blocks = CheckedAdd(blocks, document.Descendants(word + "tbl").LongCount(), "semantic_limit");
            tableCells = CheckedAdd(tableCells, document.Descendants(word + "tc").LongCount(), "semantic_limit");
            characters = CheckedAdd(
                characters,
                document.Descendants().Where(element => element.Name == word + "t" || element.Name == word + "delText")
                    .Sum(element => (long)element.Value.Length),
                "semantic_limit");
            explicitBreaks = CheckedAdd(
                explicitBreaks,
                document.Descendants(word + "pageBreakBefore").LongCount(),
                "semantic_limit");
            explicitBreaks = CheckedAdd(
                explicitBreaks,
                document.Descendants(word + "br")
                    .LongCount(element => string.Equals(element.Attribute(word + "type")?.Value, "page", StringComparison.Ordinal)),
                "semantic_limit");
            imageReferences = CheckedAdd(
                imageReferences,
                document.Descendants(drawing + "blip").LongCount()
                + document.Descendants(vml + "imagedata").LongCount(),
                "semantic_limit");

            if (blocks > settings.MaxBlocks
                || characters > settings.MaxCharacters
                || tableCells > settings.MaxTableCells
                || explicitBreaks > settings.MaxExplicitPageBreaks
                || Math.Max(imageCount, imageReferences) > settings.MaxImages)
            {
                throw Unsafe("semantic_limit", "The Word document exceeds a semantic safety limit.");
            }
        }

        return new SemanticSummary(
            checked((int)blocks),
            checked((int)characters),
            checked((int)tableCells),
            checked((int)explicitBreaks),
            checked((int)Math.Max(imageCount, imageReferences)));
    }

    private static bool ValidateMainDocumentType(ContentTypeMap contentTypes, string snapshotPath)
    {
        var mainType = contentTypes.Get("word/document.xml");
        var isTemplate = string.Equals(mainType, TemplateContentType, StringComparison.OrdinalIgnoreCase);
        if (!isTemplate && !string.Equals(mainType, DocumentContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw Unsafe("invalid_docx", "The package is not a macro-free DOCX or DOTX document.");
        }

        var extension = Path.GetExtension(snapshotPath);
        if ((isTemplate && !string.Equals(extension, ".dotx", StringComparison.OrdinalIgnoreCase))
            || (!isTemplate && !string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase)))
        {
            throw Unsafe("format_mismatch", "The package content type does not match its file format.");
        }

        return isTemplate;
    }

    private static void ValidateOpenXmlReopen(byte[] packageBytes)
    {
        try
        {
            using var stream = new MemoryStream(packageBytes, writable: false);
            using var document = WordprocessingDocument.Open(stream, isEditable: false);
            if (document.MainDocumentPart?.Document is null)
            {
                throw Unsafe("invalid_docx", "The package has no readable main Word document part.");
            }
        }
        catch (WordMcpException)
        {
            throw;
        }
        catch (Exception exception) when (exception is OpenXmlPackageException or InvalidDataException or IOException or XmlException)
        {
            throw Unsafe("invalid_docx", "The package cannot be opened as a Word document.", exception);
        }
    }

    private static string NormalizeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw Unsafe("unsafe_zip_path", "The Word package contains an empty ZIP path.");
        }

        string normalized;
        try
        {
            normalized = Uri.UnescapeDataString(name.Normalize(NormalizationForm.FormC)).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            throw Unsafe("unsafe_zip_path", "The Word package contains an invalid ZIP path.", exception);
        }

        if (normalized.StartsWith('/')
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw Unsafe("unsafe_zip_path", "The Word package contains an absolute ZIP path.");
        }

        var isDirectory = normalized.EndsWith('/');
        if (isDirectory)
        {
            normalized = normalized.TrimEnd('/');
        }

        var segments = normalized.Split('/');
        if (segments.Length == 0
            || segments.Any(segment => segment.Length == 0
                                       || segment is "." or ".."
                                       || segment.Any(character => char.IsControl(character))
                                       || segment.Contains(':')))
        {
            throw Unsafe("unsafe_zip_path", "The Word package contains a traversing or malformed ZIP path.");
        }

        return isDirectory ? string.Concat(string.Join('/', segments), "/") : string.Join('/', segments);
    }

    private static bool IsXmlPart(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static bool HasCompoundFileSignature(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1];
        return bytes.Length >= signature.Length && bytes[..signature.Length].SequenceEqual(signature);
    }

    private static bool ContainsEncryptedZipEntry(ReadOnlySpan<byte> bytes)
    {
        const uint endOfCentralDirectory = 0x06054b50;
        const uint centralDirectoryEntry = 0x02014b50;
        var minimumOffset = Math.Max(0, bytes.Length - 65_557);
        var endOffset = -1;
        for (var offset = bytes.Length - 22; offset >= minimumOffset; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)) == endOfCentralDirectory)
            {
                endOffset = offset;
                break;
            }
        }

        if (endOffset < 0)
        {
            return false;
        }

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(endOffset + 10, 2));
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(endOffset + 16, 4));
        if (centralOffset > int.MaxValue || centralOffset + 46 > bytes.Length)
        {
            return false;
        }

        var cursor = checked((int)centralOffset);
        for (var index = 0; index < entryCount; index++)
        {
            if (cursor + 46 > bytes.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4)) != centralDirectoryEntry)
            {
                return false;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(cursor + 8, 2));
            if ((flags & 0x0001) != 0)
            {
                return true;
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(cursor + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(cursor + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(cursor + 32, 2));
            cursor = checked(cursor + 46 + nameLength + extraLength + commentLength);
        }

        return false;
    }

    private static long CheckedAdd(long left, long right, string code)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw Unsafe(code, "A package safety counter overflowed.", exception);
        }
    }

    private static long CheckedMultiply(long left, int right)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static void ValidateExpectedHash(string expectedSha256)
    {
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The expected SHA-256 value must contain exactly 64 hexadecimal characters.", nameof(expectedSha256));
        }
    }

    private static void ValidateSnapshotHash(byte[] bytes, string? expectedSha256)
    {
        if (expectedSha256 is null)
        {
            return;
        }

        var expected = Convert.FromHexString(expectedSha256);
        var actual = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw Unsafe("input_changed", "The immutable input snapshot does not match its recorded SHA-256 value.");
        }
    }

    private static WordMcpException Unsafe(string code, string message, Exception? innerException = null)
    {
        var exception = new WordMcpException(
            code,
            "$.source_file_id",
            message,
            "Remove unsafe or unsupported content and upload a macro-free DOCX or DOTX file.",
            unsafeDocument: true);
        if (innerException is not null)
        {
            exception.Data["guard_inner_type"] = innerException.GetType().Name;
        }

        return exception;
    }

    private sealed record ContentTypeMap(
        IReadOnlyDictionary<string, string> Defaults,
        IReadOnlyDictionary<string, string> Overrides)
    {
        public string? Get(string partName)
        {
            if (Overrides.TryGetValue(partName, out var overridden))
            {
                return overridden;
            }

            var extension = Path.GetExtension(partName).TrimStart('.');
            return extension.Length > 0 && Defaults.TryGetValue(extension, out var fallback) ? fallback : null;
        }
    }

    private sealed record ImageSummary(int Count, long Bytes, long Pixels);

    private readonly record struct FieldSummary(int Count, bool HasToc);

    private readonly record struct FieldInstructionToken(string Value, bool Quoted, bool IsSwitch);

    private enum FieldSwitchArgument
    {
        None,
        TocLevels,
        Integer,
        NumberFormat,
        DateTimePicture,
        Picture,
    }

    private sealed record SemanticSummary(
        int Blocks,
        int Characters,
        int TableCells,
        int ExplicitPageBreaks,
        int Images);

}
