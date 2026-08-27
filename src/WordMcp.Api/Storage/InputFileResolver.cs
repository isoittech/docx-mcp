using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Storage;

public enum ResolvedInputFormat
{
    Docx,
    Dotx,
    Png,
    Jpeg,
}

public sealed record ResolvedInputSnapshot(
    string SourceId,
    string Path,
    string Sha256,
    long Bytes,
    ResolvedInputFormat Format);

/// <summary>
/// Resolves opaque upload/artifact identifiers and makes a job-owned immutable snapshot.
/// Package validation deliberately remains a worker operation after the job receipt exists.
/// </summary>
public sealed class InputFileResolver(
    IOptions<WordMcpOptions> options,
    FileJobRepository jobs)
{
    private readonly WordMcpOptions settings = options.Value;

    public Task<ResolvedInputSnapshot> ResolveDocumentAsync(
        CallerContext caller,
        CallerScope scope,
        string? sourceFileId,
        string snapshotDirectory,
        CancellationToken cancellationToken) =>
        SnapshotDocumentAsync(caller, scope, sourceFileId, snapshotDirectory, cancellationToken);

    public async Task<ResolvedInputSnapshot> SnapshotDocumentAsync(
        CallerContext caller,
        CallerScope scope,
        string? sourceFileId,
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        EnsureSnapshotDirectory(snapshotDirectory);
        var effectiveId = sourceFileId ?? "latest";
        if (Identifier.IsValid(effectiveId, "art_"))
        {
            return await SnapshotArtifactAsync(scope, effectiveId, snapshotDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        ValidateOpaqueId(effectiveId, "$.source_file_id", allowLatest: true);
        var candidate = SelectUpload(
            caller,
            effectiveId,
            documentOnly: true,
            "$.source_file_id");
        var destination = Path.Combine(snapshotDirectory, $"source{Extension(candidate.Format)}");
        return await CopyCandidateAsync(
            settings.LibreChatUploadsRoot,
            candidate,
            destination,
            settings.MaxFileBytes,
            "$.source_file_id",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ResolvedInputSnapshot> ResolveImageAsync(
        CallerContext caller,
        string imageFileId,
        string snapshotDirectory,
        CancellationToken cancellationToken) =>
        SnapshotImageAsync(caller, imageFileId, snapshotDirectory, cancellationToken);

    public async Task<ResolvedInputSnapshot> SnapshotImageAsync(
        CallerContext caller,
        string imageFileId,
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        EnsureSnapshotDirectory(snapshotDirectory);
        ValidateOpaqueId(imageFileId, "$.image_file_id", allowLatest: false);
        var candidate = SelectUpload(
            caller,
            imageFileId,
            documentOnly: false,
            "$.image_file_id");
        if (candidate.Format is not (ResolvedInputFormat.Png or ResolvedInputFormat.Jpeg))
        {
            throw Invalid(
                "unsupported_image_format",
                "$.image_file_id",
                "The image identifier does not resolve to a PNG or JPEG upload.",
                "Upload a PNG or JPEG and pass its opaque image_file_id.");
        }

        var destination = Path.Combine(
            snapshotDirectory,
            $"image-{Guid.NewGuid():N}{Extension(candidate.Format)}");
        return await CopyCandidateAsync(
            settings.LibreChatUploadsRoot,
            candidate,
            destination,
            Math.Min(settings.MaxFileBytes, settings.MaxImageBytes),
            "$.image_file_id",
            cancellationToken).ConfigureAwait(false);
    }

    private UploadCandidate SelectUpload(
        CallerContext caller,
        string requestedId,
        bool documentOnly,
        string fieldPath)
    {
        ValidateOpaqueId(caller.UserId, fieldPath, allowLatest: false);
        try
        {
            LinuxFileIdentity.EnsureDirectoryUnderRoot(settings.LibreChatUploadsRoot, [caller.UserId]);
        }
        catch (SafeFileOpenException exception)
        {
            throw Invalid(
                "input_file_not_found",
                fieldPath,
                "No matching upload is available in this user boundary.",
                "Upload the file again and use its opaque file ID.",
                exception);
        }

        var userDirectory = Path.Combine(settings.LibreChatUploadsRoot, caller.UserId);
        if (requestedId != "latest"
            && caller.AttachmentFileIds is not null
            && !caller.AttachmentFileIds.Contains(requestedId))
        {
            throw Invalid(
                "input_file_not_found",
                fieldPath,
                "The upload is not available in the current message attachment boundary.",
                "Attach the file to the current message and use its opaque file ID.");
        }

        string[] fileNames;
        try
        {
            fileNames = Directory.EnumerateFiles(userDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Invalid(
                "input_file_not_found",
                fieldPath,
                "No matching upload is available in this user boundary.",
                "Upload the file again and use its opaque file ID.",
                exception);
        }

        var candidates = new List<UploadCandidate>();
        foreach (var fileName in fileNames)
        {
            var separator = fileName.IndexOf("__", StringComparison.Ordinal);
            if (separator is <= 0 or > 128)
            {
                continue;
            }

            var candidateId = fileName[..separator];
            if (!IsSafeOpaqueId(candidateId)
                || (requestedId != "latest" && !string.Equals(candidateId, requestedId, StringComparison.Ordinal))
                || (caller.AttachmentFileIds is not null && !caller.AttachmentFileIds.Contains(candidateId))
                || !TryFormat(fileName, out var format)
                || (documentOnly && format is not (ResolvedInputFormat.Docx or ResolvedInputFormat.Dotx)))
            {
                continue;
            }

            LinuxFileIdentity.Identity identity;
            try
            {
                identity = LinuxFileIdentity.InspectUnderRoot(
                    settings.LibreChatUploadsRoot,
                    [caller.UserId, fileName]);
            }
            catch (SafeFileOpenException exception)
            {
                throw Invalid(
                    "unsafe_input_file",
                    fieldPath,
                    "A matching upload is not a stable, singly-linked regular file.",
                    "Upload the file again; symlinks and hardlinks are not accepted.",
                    exception);
            }

            candidates.Add(new UploadCandidate(candidateId, [caller.UserId, fileName], format, identity, fileName));
        }

        if (candidates.Count == 0)
        {
            throw Invalid(
                "input_file_not_found",
                fieldPath,
                "No matching upload is available in this user boundary.",
                "Upload a supported file and use its opaque file ID.");
        }

        if (requestedId == "latest"
            && caller.AttachmentFileIds is not null
            && candidates.Count != 1)
        {
            throw Invalid(
                "ambiguous_file_id",
                fieldPath,
                "More than one supported document is attached to the current message.",
                "Attach only the intended document, or pass its opaque file ID explicitly.");
        }

        if (requestedId != "latest" && candidates.Count != 1)
        {
            throw Invalid(
                "ambiguous_file_id",
                fieldPath,
                "The opaque file ID resolves to more than one supported upload.",
                "Upload the file again so the ID resolves uniquely.");
        }

        return candidates
            .OrderByDescending(candidate => candidate.Identity.ModifiedSeconds)
            .ThenByDescending(candidate => candidate.Identity.ModifiedNanoseconds)
            .ThenByDescending(candidate => candidate.FileName, StringComparer.Ordinal)
            .First();
    }

    private async Task<ResolvedInputSnapshot> SnapshotArtifactAsync(
        CallerScope scope,
        string artifactId,
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        var found = await jobs.FindArtifactAsync(scope, artifactId, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            throw Invalid(
                "input_file_not_found",
                "$.source_file_id",
                "The document artifact was not found in this conversation.",
                "Use a document artifact_id returned in this conversation.");
        }

        var (job, artifact) = found.Value;
        if (artifact.Kind != "document"
            || !TryFormat(artifact.FileName, out var format)
            || format is not (ResolvedInputFormat.Docx or ResolvedInputFormat.Dotx))
        {
            throw Invalid(
                "unsupported_document_format",
                "$.source_file_id",
                "The artifact is not a DOCX or DOTX document.",
                "Use a document artifact_id returned in this conversation.");
        }

        var artifactPath = ResolveArtifactPath(job, artifact);
        IReadOnlyList<string> segments;
        LinuxFileIdentity.Identity identity;
        try
        {
            segments = LinuxFileIdentity.RelativeSegments(settings.StorageRoot, artifactPath);
            if (segments.Count < 3
                || segments[0] != "jobs"
                || segments[1] != job.Id)
            {
                throw new SafeFileOpenException("The artifact path is outside its owning job.");
            }

            identity = LinuxFileIdentity.InspectUnderRoot(settings.StorageRoot, segments);
        }
        catch (SafeFileOpenException exception)
        {
            throw Invalid(
                "unsafe_input_file",
                "$.source_file_id",
                "The document artifact is not a stable, singly-linked regular file.",
                "Regenerate the document artifact and use the new artifact_id.",
                exception);
        }

        var candidate = new UploadCandidate(artifactId, segments, format, identity, artifact.FileName);
        return await CopyCandidateAsync(
            settings.StorageRoot,
            candidate,
            Path.Combine(snapshotDirectory, $"source{Extension(format)}"),
            settings.MaxFileBytes,
            "$.source_file_id",
            cancellationToken).ConfigureAwait(false);
    }

    private string ResolveArtifactPath(WordJob job, ArtifactRecord artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Path) && Path.IsPathFullyQualified(artifact.Path))
        {
            return artifact.Path;
        }

        if (Path.GetFileName(artifact.FileName) != artifact.FileName)
        {
            throw Invalid(
                "unsafe_input_file",
                "$.source_file_id",
                "The artifact metadata contains an unsafe file name.",
                "Regenerate the document artifact.");
        }

        var jobDirectory = jobs.GetJobDirectory(job.Id);
        string[] matches;
        try
        {
            matches = Directory.EnumerateFiles(
                    jobDirectory,
                    artifact.FileName,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        MatchCasing = MatchCasing.CaseSensitive,
                    })
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Invalid(
                "input_file_not_found",
                "$.source_file_id",
                "The document artifact is no longer available.",
                "Regenerate the document artifact.",
                exception);
        }

        if (matches.Length != 1)
        {
            throw Invalid(
                "input_file_not_found",
                "$.source_file_id",
                "The document artifact is no longer uniquely available.",
                "Regenerate the document artifact.");
        }

        return matches[0];
    }

    private static async Task<ResolvedInputSnapshot> CopyCandidateAsync(
        string root,
        UploadCandidate candidate,
        string destination,
        long maximumBytes,
        string fieldPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await LinuxFileIdentity.CopySnapshotAsync(
                root,
                candidate.Segments,
                destination,
                maximumBytes,
                candidate.Identity,
                cancellationToken).ConfigureAwait(false);
            return new ResolvedInputSnapshot(
                candidate.SourceId,
                snapshot.Path,
                snapshot.Sha256,
                snapshot.Bytes,
                candidate.Format);
        }
        catch (SafeFileOpenException exception)
        {
            throw Invalid(
                "unsafe_input_file",
                fieldPath,
                "The selected input changed or is not a stable, singly-linked regular file.",
                "Upload the file again and retry with the new opaque file ID.",
                exception);
        }
    }

    private static void EnsureSnapshotDirectory(string snapshotDirectory)
    {
        if (!Path.IsPathFullyQualified(snapshotDirectory))
        {
            throw new ArgumentException("The snapshot directory must be absolute.", nameof(snapshotDirectory));
        }

        Directory.CreateDirectory(snapshotDirectory);
    }

    private static void ValidateOpaqueId(string value, string fieldPath, bool allowLatest)
    {
        if (value == "latest")
        {
            if (allowLatest)
            {
                return;
            }
        }
        else if (IsSafeOpaqueId(value))
        {
            return;
        }

        throw Invalid(
            "invalid_file_id",
            fieldPath,
            "The file identifier is invalid.",
            "Use the opaque file ID exactly as returned; do not send a path, URL, or file name.");
    }

    private static bool IsSafeOpaqueId(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool TryFormat(string fileName, out ResolvedInputFormat format)
    {
        format = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".docx" => ResolvedInputFormat.Docx,
            ".dotx" => ResolvedInputFormat.Dotx,
            ".png" => ResolvedInputFormat.Png,
            ".jpg" or ".jpeg" => ResolvedInputFormat.Jpeg,
            _ => (ResolvedInputFormat)(-1),
        };
        return (int)format >= 0;
    }

    private static string Extension(ResolvedInputFormat format) => format switch
    {
        ResolvedInputFormat.Docx => ".docx",
        ResolvedInputFormat.Dotx => ".dotx",
        ResolvedInputFormat.Png => ".png",
        ResolvedInputFormat.Jpeg => ".jpg",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static WordMcpException Invalid(
        string code,
        string fieldPath,
        string message,
        string correction,
        Exception? innerException = null)
    {
        var exception = new WordMcpException(code, fieldPath, message, correction);
        if (innerException is not null)
        {
            exception.Data["resolver_inner_type"] = innerException.GetType().Name;
        }

        return exception;
    }

    private sealed record UploadCandidate(
        string SourceId,
        IReadOnlyList<string> Segments,
        ResolvedInputFormat Format,
        LinuxFileIdentity.Identity Identity,
        string FileName);
}
