using WordMcp.Domain;

namespace WordMcp.Word;

public sealed partial class OpenXmlWordDocumentEngine
{
    public WordGenerationResult Generate(
        WordGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if (!Path.IsPathFullyQualified(request.DestinationPath))
        {
            throw new ArgumentException("The worker-owned destination path must be absolute.", nameof(request));
        }

        if (request.TemplatePath is not null
            && (!Path.IsPathFullyQualified(request.TemplatePath) || !File.Exists(request.TemplatePath)))
        {
            throw InvalidInput(
                "template_snapshot_not_found",
                "$.template_source",
                "The resolved immutable template snapshot was not found.");
        }

        var destination = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The destination path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.generation.tmp");
        try
        {
            var generator = new OpenXmlDocumentGenerator(options);
            generator.Generate(
                temporary,
                request.Definition,
                request.TemplatePath,
                request.Images ?? new Dictionary<string, WordImageAsset>(StringComparer.Ordinal),
                cancellationToken);
            if (OoxmlDigitalSignaturePolicy.IsPresent(temporary))
            {
                throw new WordMcpException(
                    "generation_sanitization_failed",
                    "$.template_source",
                    "The generated document retained digital-signature package structures.",
                    "Remove digital-signature structures from the generation template and retry.");
            }

            var validation = validationGate.ValidateNewDocument(temporary, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
            var outputSha256 = ComputeSha256(destination);
            var outputAnalysis = Analyze(
                new WordAnalysisRequest(
                    destination,
                    EnsureDocxFileName(request.Definition.Title),
                    outputSha256,
                    request.UserScope,
                    request.ConversationScope),
                cancellationToken);
            return new WordGenerationResult(outputSha256, outputAnalysis, validation);
        }
        catch
        {
            DeleteFailedOutput(temporary);
            DeleteFailedOutput(destination);
            throw;
        }
    }

    private static string EnsureDocxFileName(string title)
    {
        var safe = new string(title
            .Where(character => !Path.GetInvalidFileNameChars().Contains(character) && !char.IsControl(character))
            .Take(80)
            .ToArray())
            .Trim();
        return string.Concat(string.IsNullOrEmpty(safe) ? "document" : safe, ".docx");
    }
}
