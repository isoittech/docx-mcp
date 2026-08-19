using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using WordMcp.Domain;

namespace WordMcp.Word;

public sealed class OpenXmlValidationGate
{
    private const int MaximumErrors = 1_000;

    public OpenXmlValidationReport ValidateExistingEdit(
        string sourcePath,
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        var baseline = Validate(sourcePath, "baseline document", cancellationToken);
        var candidate = Validate(candidatePath, "candidate document", cancellationToken);
        var added = ExceptAsMultiset(candidate, baseline);
        var report = new OpenXmlValidationReport(baseline, candidate, added);
        if (!report.IsAccepted)
        {
            throw ValidationFailure(added, generated: false);
        }

        return report;
    }

    public OpenXmlValidationReport ValidateNewDocument(
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        var candidate = Validate(candidatePath, "generated document", cancellationToken);
        var report = new OpenXmlValidationReport([], candidate, candidate);
        if (!report.IsAccepted)
        {
            throw ValidationFailure(candidate, generated: true);
        }

        return report;
    }

    public IReadOnlyList<OpenXmlValidationFingerprint> Validate(
        string path,
        CancellationToken cancellationToken = default) =>
        Validate(path, "document", cancellationToken);

    private static OpenXmlValidationFingerprint[] Validate(
        string path,
        string documentRole,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var document = WordprocessingDocument.Open(path, false);
            var validator = new OpenXmlValidator(FileFormatVersions.Office2019)
            {
                // Probe one item past the accepted comparison bound. Capping at exactly
                // MaximumErrors can make a newly appended error indistinguishable from a
                // baseline whose first MaximumErrors fingerprints happen to be identical.
                MaxNumberOfErrors = MaximumErrors + 1,
            };

            var validationErrors = validator.Validate(document, cancellationToken)
                .Take(MaximumErrors + 1)
                .ToArray();
            if (validationErrors.Length > MaximumErrors)
            {
                throw ValidationLimitFailure(documentRole);
            }

            return validationErrors
                .Select(ToFingerprint)
                .OrderBy(error => error.PartUri, StringComparer.Ordinal)
                .ThenBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Id, StringComparer.Ordinal)
                .ThenBy(error => error.Description, StringComparer.Ordinal)
                .ToArray();
        }
        catch (WordMcpException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or OpenXmlPackageException)
        {
            throw new WordMcpException(
                "invalid_openxml_package",
                "$.source_file_id",
                "The Word package could not be reopened for Open XML validation.",
                "Provide an unencrypted macro-free DOCX or DOTX that opens without repair warnings.",
                unsafeDocument: true);
        }
    }

    private static OpenXmlValidationFingerprint ToFingerprint(ValidationErrorInfo error) => new(
        error.ErrorType.ToString(),
        error.Id ?? string.Empty,
        error.Part?.Uri.ToString() ?? string.Empty,
        error.Path?.XPath ?? string.Empty,
        error.Description ?? string.Empty);

    private static List<OpenXmlValidationFingerprint> ExceptAsMultiset(
        IReadOnlyList<OpenXmlValidationFingerprint> candidate,
        IReadOnlyList<OpenXmlValidationFingerprint> baseline)
    {
        var counts = baseline
            .GroupBy(Identity)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var added = new List<OpenXmlValidationFingerprint>();
        foreach (var error in candidate)
        {
            var key = Identity(error);
            if (counts.TryGetValue(key, out var remaining) && remaining > 0)
            {
                counts[key] = remaining - 1;
            }
            else
            {
                added.Add(error);
            }
        }

        return added;
    }

    private static string Identity(OpenXmlValidationFingerprint error) => string.Join(
        '\u001f',
        error.ErrorType,
        error.Id,
        error.PartUri,
        error.Path,
        error.Description);

    private static WordMcpException ValidationFailure(
        IReadOnlyList<OpenXmlValidationFingerprint> errors,
        bool generated)
    {
        var first = errors[0];
        var diagnostics = string.Join(", ", errors.Take(12).Select(error => $"{error.Id}@{error.Path}"));
        return new WordMcpException(
            generated ? "generated_openxml_invalid" : "openxml_regression",
            "$.document",
            $"Open XML validation found {errors.Count} {(generated ? "error(s)" : "new error(s)")}; first: {first.Id} in {first.PartUri} at {first.Path}. Diagnostics: {diagnostics}",
            generated
                ? "Use only the supported declarative blocks and verify the generated package relationships."
                : "Re-analyze the source and choose an editable target that does not cross an unsupported structure.");
    }

    private static WordMcpException ValidationLimitFailure(string documentRole) => new(
        "openxml_validation_error_limit",
        "$.document",
        $"The {documentRole} contains more than {MaximumErrors} Open XML validation errors, so a complete regression comparison is not possible.",
        "Repair the source or regenerate the document until the complete Open XML validation result is within the supported bound.");
}
