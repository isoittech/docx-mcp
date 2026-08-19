using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Artifacts;

public sealed partial class ArtifactService(
    FileJobRepository jobs,
    ArtifactTokenService tokens,
    RetentionPolicy retention,
    IOptions<WordMcpOptions> options,
    TimeProvider timeProvider)
{
    private readonly string publicBaseUrl = options.Value.PublicBaseUrl.TrimEnd('/');

    public ArtifactRecord CreateRecord(string kind, string path, string fileName)
    {
        if (!SafeFileName().IsMatch(fileName))
        {
            throw new InvalidOperationException("Artifact file names must be normalized safe names.");
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
        {
            throw new InvalidOperationException("An artifact cannot be registered before it exists.");
        }

        var now = timeProvider.GetUtcNow();
        return new ArtifactRecord(
            Identifier.New("art_"),
            kind,
            fileName,
            MediaType(fileName),
            info.FullName,
            info.Length,
            now,
            now.AddDays(options.Value.RetentionDays));
    }

    public IReadOnlyList<ArtifactLink> CreateLinks(WordJob job)
    {
        if (job.Result?.Artifacts is not { Count: > 0 } artifacts
            || !IsRetained(job))
        {
            return [];
        }

        return artifacts
            .Where(artifact => artifact.Kind != "preview")
            .Select(artifact =>
            {
                const string disposition = "attachment";
                var (token, expiresAt) = tokens.Create(job.Id, artifact.ArtifactId, artifact.FileName, disposition);
                var url = string.Concat(
                    publicBaseUrl,
                    "/artifacts/",
                    Uri.EscapeDataString(job.Id),
                    "/",
                    Uri.EscapeDataString(artifact.ArtifactId),
                    "/",
                    Uri.EscapeDataString(artifact.FileName),
                    "?disposition=",
                    disposition,
                    "&token=",
                    Uri.EscapeDataString(token));
                return new ArtifactLink(artifact.ArtifactId, artifact.Kind, artifact.FileName, url, expiresAt);
            })
            .ToArray();
    }

    public async Task<(WordJob Job, ArtifactRecord Artifact)?> AuthorizeDownloadAsync(
        string jobId,
        string artifactId,
        string fileName,
        string disposition,
        string token,
        CancellationToken cancellationToken)
    {
        if (!Identifier.IsValid(jobId, "job_") || !Identifier.IsValid(artifactId, "art_")
            || !SafeFileName().IsMatch(fileName)
            || disposition is not ("attachment" or "inline")
            || !tokens.Validate(jobId, artifactId, fileName, disposition, token))
        {
            return null;
        }

        var job = await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        var artifact = job?.Result?.Artifacts?.FirstOrDefault(item => item.ArtifactId == artifactId
                                                                       && item.FileName == fileName);
        if (job is null || artifact is null || !IsRetained(job)
            || !File.Exists(artifact.Path))
        {
            return null;
        }

        return (job, artifact);
    }

    public bool IsRetained(WordJob job) =>
        retention.EffectiveJobExpiry(job) > timeProvider.GetUtcNow();

    public async Task MarkDocumentDownloadedAsync(
        string jobId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await jobs.UpdateAsync(
            jobId,
            current =>
            {
                if (current.Result?.Artifacts is null)
                {
                    return current;
                }

                var changed = false;
                var artifacts = current.Result.Artifacts.Select(artifact =>
                {
                    if (artifact.ArtifactId == artifactId
                        && artifact.Kind == "document"
                        && artifact.FirstDownloadedAt is null)
                    {
                        changed = true;
                        return artifact with { FirstDownloadedAt = now };
                    }

                    return artifact;
                }).ToArray();
                return changed
                    ? current with
                    {
                        Result = current.Result with { Artifacts = artifacts },
                        UpdatedAt = now,
                    }
                    : current;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string MediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFileName();
}
