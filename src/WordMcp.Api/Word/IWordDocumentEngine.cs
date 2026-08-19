using WordMcp.Domain;

namespace WordMcp.Word;

public interface IWordDocumentEngine
{
    AnalysisSnapshot Analyze(WordAnalysisRequest request, CancellationToken cancellationToken = default);

    WordMutationResult ReplaceText(
        WordMutationRequest request,
        IReadOnlyList<TextReplacementRequest> replacements,
        CancellationToken cancellationToken = default);

    WordMutationResult ApplyEdits(
        WordMutationRequest request,
        IReadOnlyList<AtomicEditRequest> edits,
        CancellationToken cancellationToken = default);

    WordMutationResult PopulateTemplate(
        WordTemplatePopulationRequest request,
        IReadOnlyList<TemplateFieldRequest> fields,
        CancellationToken cancellationToken = default);

    WordGenerationResult Generate(
        WordGenerationRequest request,
        CancellationToken cancellationToken = default);

    OpenXmlValidationReport ValidateExistingEdit(
        string sourcePath,
        string candidatePath,
        CancellationToken cancellationToken = default);

    OpenXmlValidationReport ValidateNewDocument(
        string candidatePath,
        CancellationToken cancellationToken = default);
}
