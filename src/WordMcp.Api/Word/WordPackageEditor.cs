using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using WordMcp.Domain;

namespace WordMcp.Word;

public sealed class WordPackageMutationContext
{
    private readonly HashSet<string> changedEntries = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ChangedEntries => changedEntries;

    internal bool ConvertTemplateToDocument { get; private set; }

    public void MarkChanged(OpenXmlPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        MarkChangedEntry(part.Uri.ToString());
    }

    public void MarkChangedEntry(string partUri)
    {
        var normalized = WordPackageEditor.NormalizeEntryName(partUri);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A changed package entry name must not be empty.", nameof(partUri));
        }

        changedEntries.Add(normalized);
    }

    public void RequestTemplateToDocumentConversion()
    {
        ConvertTemplateToDocument = true;
        MarkChangedEntry("[Content_Types].xml");
    }
}

public sealed class WordPackageEditor
{
    public IReadOnlyList<string> Edit(
        string sourcePath,
        string destinationPath,
        Action<WordprocessingDocument, WordPackageMutationContext> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(mutation);

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.Ordinal))
        {
            throw new WordMcpException(
                "source_output_collision",
                "$.document",
                "The source document and output document must be different files.",
                "Use the job-owned output path and keep the immutable source snapshot unchanged.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationFullPath)
            ?? throw new InvalidOperationException("The destination path has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);

        var workPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationFullPath)}.{Guid.NewGuid():N}.sdk.tmp");
        var rewrittenPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationFullPath)}.{Guid.NewGuid():N}.zip.tmp");
        try
        {
            File.Copy(sourceFullPath, workPath, overwrite: false);
            var context = new WordPackageMutationContext();
            using (var document = WordprocessingDocument.Open(workPath, true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                mutation(document, context);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (context.ConvertTemplateToDocument)
            {
                ConvertMainContentTypeToDocument(workPath);
            }

            if (context.ChangedEntries.Count == 0)
            {
                throw new InvalidOperationException("The mutation did not declare any changed package entries.");
            }

            RewriteFromEditedPackage(
                sourceFullPath,
                workPath,
                rewrittenPath,
                context.ChangedEntries,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(rewrittenPath, destinationFullPath, overwrite: true);
            return context.ChangedEntries.Order(StringComparer.Ordinal).ToArray();
        }
        finally
        {
            DeleteIfPresent(workPath);
            DeleteIfPresent(rewrittenPath);
        }
    }

    public void RewriteFromEditedPackage(
        string sourcePath,
        string editedPath,
        string destinationPath,
        IReadOnlyCollection<string> changedEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(editedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(changedEntries);

        var allowed = changedEntries
            .Select(NormalizeEntryName)
            .ToHashSet(StringComparer.Ordinal);
        if (allowed.Count == 0 || allowed.Contains(string.Empty))
        {
            throw new ArgumentException("At least one non-empty changed entry is required.", nameof(changedEntries));
        }

        using var sourceStream = File.OpenRead(sourcePath);
        using var editedStream = File.OpenRead(editedPath);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false);
        using var edited = new ZipArchive(editedStream, ZipArchiveMode.Read, leaveOpen: false);

        var sourceEntries = UniqueEntries(source);
        var editedEntries = UniqueEntries(edited);
        foreach (var entryName in allowed)
        {
            if (!editedEntries.ContainsKey(entryName))
            {
                throw new InvalidDataException($"The edited package does not contain declared entry '{entryName}'.");
            }
        }

        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (destinationDirectory is not null)
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using var destinationStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var destination = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var sourceEntry in source.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = allowed.Contains(sourceEntry.FullName)
                ? editedEntries[sourceEntry.FullName]
                : sourceEntry;
            CopyEntry(selected, destination, sourceEntry, cancellationToken);
        }

        foreach (var addedName in allowed.Where(name => !sourceEntries.ContainsKey(name)).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var editedEntry = editedEntries[addedName];
            CopyEntry(editedEntry, destination, editedEntry, cancellationToken);
        }
    }

    internal static string NormalizeEntryName(string partUri) => partUri.Replace('\\', '/').TrimStart('/');

    private static void ConvertMainContentTypeToDocument(string path)
    {
        const string templateContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml";
        const string documentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("[Content_Types].xml")
                    ?? throw new InvalidDataException("The Word package has no content-types manifest.");
        XDocument manifest;
        using (var input = entry.Open())
        using (var reader = XmlReader.Create(input, new XmlReaderSettings
               {
                   DtdProcessing = DtdProcessing.Prohibit,
                   XmlResolver = null,
                   MaxCharactersInDocument = 4 * 1024 * 1024,
               }))
        {
            manifest = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }

        var contentTypeDeclarations = manifest.Root?.Elements()
            .Where(element => string.Equals((string?)element.Attribute("ContentType"), templateContentType, StringComparison.Ordinal))
            .ToArray() ?? [];
        if (contentTypeDeclarations.Length != 1)
        {
            var observed = manifest.Root?.Elements()
                .Select(element => $"{(string?)element.Attribute("PartName") ?? (string?)element.Attribute("Extension")}={(string?)element.Attribute("ContentType")}")
                .ToArray() ?? [];
            throw new InvalidDataException($"The DOTX package does not contain exactly one template main content type. Observed: {string.Join(", ", observed)}");
        }

        contentTypeDeclarations[0].SetAttributeValue("ContentType", documentContentType);
        var lastWriteTime = entry.LastWriteTime;
        var externalAttributes = entry.ExternalAttributes;
        entry.Delete();
        var replacement = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
        replacement.LastWriteTime = lastWriteTime;
        replacement.ExternalAttributes = externalAttributes;
        using var output = replacement.Open();
        manifest.Save(output, SaveOptions.DisableFormatting);
    }

    private static Dictionary<string, ZipArchiveEntry> UniqueEntries(ZipArchive archive)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!result.TryAdd(entry.FullName, entry))
            {
                throw new InvalidDataException($"The package contains duplicate ZIP entry '{entry.FullName}'.");
            }
        }

        return result;
    }

    private static void CopyEntry(
        ZipArchiveEntry payloadSource,
        ZipArchive destination,
        ZipArchiveEntry metadataSource,
        CancellationToken cancellationToken)
    {
        var destinationEntry = destination.CreateEntry(payloadSource.FullName, CompressionLevel.Optimal);
        destinationEntry.LastWriteTime = metadataSource.LastWriteTime;
        destinationEntry.ExternalAttributes = metadataSource.ExternalAttributes;
        if (payloadSource.Name.Length == 0)
        {
            return;
        }

        using var input = payloadSource.Open();
        using var output = destinationEntry.Open();
        input.CopyTo(output);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
