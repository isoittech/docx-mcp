using System.IO.Compression;
using System.Xml;

namespace WordMcp.Word;

internal static class OoxmlDigitalSignaturePolicy
{
    private const long MaximumPolicyXmlBytes = 4L * 1024 * 1024;
    private const string SignatureDirectory = "_xmlsignatures/";
    private const string OriginContentType = "application/vnd.openxmlformats-package.digital-signature-origin";
    private const string SignatureContentType = "application/vnd.openxmlformats-package.digital-signature-xmlsignature+xml";
    private const string OriginRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin";
    private const string SignatureRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/signature";

    public static bool IsPresent(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        if (archive.Entries.Any(entry => IsSignaturePartName(entry.FullName)))
        {
            return true;
        }

        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
                && ContainsAttributeValue(entry, "ContentType", IsSignatureContentType))
            {
                return true;
            }

            if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
                && ContainsAttributeValue(entry, "Type", IsSignatureRelationship))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSignaturePartName(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith(SignatureDirectory, StringComparison.OrdinalIgnoreCase)
               && !normalized.EndsWith('/');
    }

    private static bool IsSignatureContentType(string value) =>
        string.Equals(value, OriginContentType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, SignatureContentType, StringComparison.OrdinalIgnoreCase);

    private static bool IsSignatureRelationship(string value) =>
        string.Equals(value, OriginRelationship, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, SignatureRelationship, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAttributeValue(
        ZipArchiveEntry entry,
        string localName,
        Func<string, bool> predicate)
    {
        if (entry.Length < 0 || entry.Length > MaximumPolicyXmlBytes)
        {
            throw new InvalidDataException("An OPC policy XML part exceeds the accepted size.");
        }

        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumPolicyXmlBytes,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || !reader.HasAttributes)
            {
                continue;
            }

            while (reader.MoveToNextAttribute())
            {
                if (string.Equals(reader.LocalName, localName, StringComparison.Ordinal)
                    && predicate(reader.Value))
                {
                    return true;
                }
            }

            reader.MoveToElement();
        }

        return false;
    }
}
