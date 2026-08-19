using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Word;

namespace WordMcp.Storage;

public enum TemplateReadinessState
{
    NotConfigured,
    Pending,
    Ready,
    Failed,
}

public sealed record TemplateRegistryReadiness(
    TemplateReadinessState State,
    string? FailureCode)
{
    public bool IsReady => State is TemplateReadinessState.NotConfigured or TemplateReadinessState.Ready;
}

/// <summary>Resolves administrator-scoped deployment templates without accepting paths.</summary>
public sealed class TemplateRegistry(
    IOptions<WordMcpOptions> options,
    DocxPackageGuard packageGuard) : IDisposable
{
    private readonly WordMcpOptions settings = options.Value;
    private readonly OpenXmlValidationGate openXmlValidationGate = new();
    private readonly SemaphoreSlim validationGate = new(1, 1);
    private TemplateRegistryReadiness readiness = string.IsNullOrEmpty(options.Value.DefaultTemplateId)
        ? new(TemplateReadinessState.NotConfigured, null)
        : new(TemplateReadinessState.Pending, null);
    private DocxPackageInspection? defaultInspection;
    private string? defaultInspectionSha256;

    public bool HasDefault => !string.IsNullOrEmpty(settings.DefaultTemplateId);

    public TemplateRegistryReadiness Readiness => Volatile.Read(ref readiness);

    public DocxPackageInspection? DefaultInspection => Volatile.Read(ref defaultInspection);

    public Task<ResolvedInputSnapshot> ResolveDefaultAsync(
        string snapshotDirectory,
        CancellationToken cancellationToken) =>
        SnapshotDefaultAsync(snapshotDirectory, cancellationToken);

    /// <summary>Snapshots the configured default. The worker still validates this job-owned copy.</summary>
    public Task<ResolvedInputSnapshot> SnapshotDefaultAsync(
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        if (!HasDefault)
        {
            throw Invalid(
                "default_template_not_configured",
                "$.template_source",
                "No default deployment template is configured.",
                "Use template_source=none or ask an administrator to configure a default template.");
        }

        return SnapshotByIdAsync(settings.DefaultTemplateId, snapshotDirectory, cancellationToken);
    }

    /// <summary>Resolves an administrator-selected opaque deployment template identifier.</summary>
    public async Task<ResolvedInputSnapshot> SnapshotByIdAsync(
        string deploymentTemplateId,
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        ValidateTemplateId(deploymentTemplateId);
        if (!Path.IsPathFullyQualified(snapshotDirectory))
        {
            throw new ArgumentException("The snapshot directory must be absolute.", nameof(snapshotDirectory));
        }

        Directory.CreateDirectory(snapshotDirectory);
        try
        {
            LinuxFileIdentity.EnsureDirectoryUnderRoot(settings.TemplatesRoot, []);
        }
        catch (SafeFileOpenException exception)
        {
            throw Invalid(
                "template_not_found",
                "$.template_source",
                "The deployment template store is unavailable.",
                "Ask an administrator to restore the configured template mount.",
                exception);
        }

        string[] names;
        try
        {
            names = Directory.EnumerateFiles(settings.TemplatesRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null
                               && (string.Equals(name, $"{deploymentTemplateId}.docx", StringComparison.Ordinal)
                                   || string.Equals(name, $"{deploymentTemplateId}.dotx", StringComparison.Ordinal)))
                .Cast<string>()
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Invalid(
                "template_not_found",
                "$.template_source",
                "The deployment template store is unavailable.",
                "Ask an administrator to restore the configured template mount.",
                exception);
        }

        if (names.Length == 0)
        {
            throw Invalid(
                "template_not_found",
                "$.template_source",
                "The deployment template was not found.",
                "Use a configured deployment template ID.");
        }

        if (names.Length != 1)
        {
            throw Invalid(
                "ambiguous_template_id",
                "$.template_source",
                "The deployment template ID resolves to more than one file.",
                "Ask an administrator to keep exactly one DOCX or DOTX for this template ID.");
        }

        var name = names[0];
        var format = name.EndsWith(".dotx", StringComparison.Ordinal)
            ? ResolvedInputFormat.Dotx
            : ResolvedInputFormat.Docx;
        LinuxFileIdentity.Identity identity;
        try
        {
            identity = LinuxFileIdentity.InspectUnderRoot(settings.TemplatesRoot, [name]);
        }
        catch (SafeFileOpenException exception)
        {
            throw Invalid(
                "unsafe_template_file",
                "$.template_source",
                "The deployment template is not a stable, singly-linked regular file.",
                "Ask an administrator to replace symlinks or hardlinks with a regular template file.",
                exception);
        }

        var extension = format == ResolvedInputFormat.Dotx ? ".dotx" : ".docx";
        try
        {
            var snapshot = await LinuxFileIdentity.CopySnapshotAsync(
                settings.TemplatesRoot,
                [name],
                Path.Combine(snapshotDirectory, $"template{extension}"),
                settings.MaxFileBytes,
                identity,
                cancellationToken).ConfigureAwait(false);
            return new ResolvedInputSnapshot(
                deploymentTemplateId,
                snapshot.Path,
                snapshot.Sha256,
                snapshot.Bytes,
                format);
        }
        catch (SafeFileOpenException exception)
        {
            throw Invalid(
                "unsafe_template_file",
                "$.template_source",
                "The deployment template changed or is not a stable regular file.",
                "Ask an administrator to replace the template and retry.",
                exception);
        }
    }

    /// <summary>
    /// Startup/readiness validation. Job-time template snapshots remain separately guarded by the worker.
    /// </summary>
    public async Task<DocxPackageInspection?> ValidateConfiguredDefaultAsync(CancellationToken cancellationToken)
    {
        await validationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!HasDefault)
            {
                Volatile.Write(
                    ref readiness,
                    new TemplateRegistryReadiness(TemplateReadinessState.NotConfigured, null));
                Volatile.Write(ref defaultInspection, null);
                Volatile.Write(ref defaultInspectionSha256, null);
                return null;
            }

            Volatile.Write(
                ref readiness,
                new TemplateRegistryReadiness(TemplateReadinessState.Pending, null));
            var validationRoot = Path.Combine(settings.StorageRoot, "template-validation");
            Directory.CreateDirectory(validationRoot);
            var validationDirectory = Path.Combine(validationRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(validationDirectory);
            try
            {
                var snapshot = await SnapshotDefaultAsync(validationDirectory, cancellationToken)
                    .ConfigureAwait(false);
                var cachedInspection = Volatile.Read(ref defaultInspection);
                var cachedSha256 = Volatile.Read(ref defaultInspectionSha256);
                DocxPackageInspection inspection;
                if (cachedInspection is not null
                    && string.Equals(cachedSha256, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    inspection = cachedInspection;
                }
                else
                {
                    inspection = await packageGuard.ValidateSnapshotAsync(
                            snapshot.Path,
                            snapshot.Sha256,
                            cancellationToken)
                        .ConfigureAwait(false);
                    _ = openXmlValidationGate.ValidateNewDocument(snapshot.Path, cancellationToken);
                }
                Volatile.Write(ref defaultInspection, inspection);
                Volatile.Write(ref defaultInspectionSha256, snapshot.Sha256);
                Volatile.Write(
                    ref readiness,
                    new TemplateRegistryReadiness(TemplateReadinessState.Ready, null));
                return inspection;
            }
            finally
            {
                Directory.Delete(validationDirectory, recursive: true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WordMcpException exception)
        {
            Volatile.Write(ref defaultInspection, null);
            Volatile.Write(ref defaultInspectionSha256, null);
            Volatile.Write(
                ref readiness,
                new TemplateRegistryReadiness(TemplateReadinessState.Failed, exception.Code));
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Volatile.Write(ref defaultInspection, null);
            Volatile.Write(ref defaultInspectionSha256, null);
            Volatile.Write(
                ref readiness,
                new TemplateRegistryReadiness(TemplateReadinessState.Failed, "template_validation_failed"));
            throw Invalid(
                "template_validation_failed",
                "$.template_source",
                "The configured default template could not be validated.",
                "Ask an administrator to repair the template configuration.",
                exception);
        }
        finally
        {
            validationGate.Release();
        }
    }

    public Task<DocxPackageInspection?> ValidateDefaultAsync(CancellationToken cancellationToken) =>
        ValidateConfiguredDefaultAsync(cancellationToken);

    public void Dispose()
    {
        validationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ValidateTemplateId(string value)
    {
        if (value is { Length: >= 1 and <= 128 }
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return;
        }

        throw Invalid(
            "invalid_template_id",
            "$.template_source",
            "The deployment template identifier is invalid.",
            "Use a configured opaque deployment template ID, not a path or file name.");
    }

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
            exception.Data["template_inner_type"] = innerException.GetType().Name;
        }

        return exception;
    }
}

/// <summary>Runs default-template validation once without making an unsafe service look ready.</summary>
public sealed partial class DefaultTemplateWarmupService(
    TemplateRegistry templates,
    ILogger<DefaultTemplateWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await templates.ValidateConfiguredDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WordMcpException)
        {
            LogValidationFailure(logger, templates.Readiness.FailureCode);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Default template readiness validation failed with code {FailureCode}.")]
    private static partial void LogValidationFailure(ILogger logger, string? failureCode);
}
