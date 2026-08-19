using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Word;

namespace WordMcp.Rendering;

public sealed record RenderResult(
    string PdfPath,
    IReadOnlyList<string> PreviewPaths,
    string? FinalizedDocumentPath,
    int PageCount,
    int UpdatedIndexCount,
    int IndexUpdatePassCount,
    bool IndexConverged,
    int TocEntryLineCount,
    int TocPageNumberCount,
    int TocMaxPageNumber,
    int ExpectedHeadingCount,
    int MatchedHeadingCount);

public sealed partial class DocumentRenderer(
    ProcessRunner processes,
    IOptions<WordMcpOptions> options)
{
    private readonly WordMcpOptions options = options.Value;

    public async Task<RenderResult> RenderAsync(
        string distributionDocxPath,
        string outputDirectory,
        bool requireIndexUpdate,
        bool finalizeDocumentForDistribution,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var workDirectory = Path.Combine(outputDirectory, "render-work");
        var profileDirectory = Path.Combine(workDirectory, "lo-profile");
        Directory.CreateDirectory(profileDirectory);
        var previewDocx = Path.Combine(workDirectory, "preview.docx");
        var pdfPath = Path.Combine(outputDirectory, "preview.pdf");
        File.Copy(distributionDocxPath, previewDocx, overwrite: false);
        var primaryHash = await HashAsync(distributionDocxPath, cancellationToken).ConfigureAwait(false);

        var port = FindLoopbackPort();
        using var office = processes.StartLongRunning(
            options.LibreOfficePath,
            [
                "--headless",
                "--invisible",
                "--nologo",
                "--nodefault",
                "--nofirststartwizard",
                "--norestore",
                $"-env:UserInstallation={new Uri(profileDirectory.EndsWith(Path.DirectorySeparatorChar) ? profileDirectory : profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri}",
                $"--accept=socket,host=127.0.0.1,port={port};urp;StarOffice.ServiceManager",
            ],
            workDirectory,
            RestrictedEnvironment(profileDirectory));

        ProcessResult uno;
        try
        {
            uno = await processes.RunAsync(
                options.PythonPath,
                [options.UnoScriptPath, port.ToString(CultureInfo.InvariantCulture), previewDocx, pdfPath],
                workDirectory,
                TimeSpan.FromMinutes(options.JobTimeoutMinutes),
                cancellationToken,
                RestrictedEnvironment(profileDirectory)).ConfigureAwait(false);
        }
        finally
        {
            ProcessRunner.TryKillTree(office);
        }

        if (uno.ExitCode != 0)
        {
            throw new WordMcpException(
                "libreoffice_conversion_failed",
                "$render",
                "LibreOffice could not update and render the document.",
                "Simplify unsupported Word features and retry with a safe DOCX.");
        }

        int updatedIndexes;
        int updatePassCount;
        bool indexConverged;
        int tocEntryLines;
        int tocPageNumbers;
        int tocMaxPageNumber;
        int expectedHeadings;
        int matchedHeadings;
        try
        {
            using var status = JsonDocument.Parse(uno.StandardOutput);
            updatedIndexes = status.RootElement.GetProperty("updated_count").GetInt32();
            updatePassCount = status.RootElement.GetProperty("update_pass_count").GetInt32();
            indexConverged = status.RootElement.GetProperty("index_converged").GetBoolean();
            tocEntryLines = status.RootElement.GetProperty("entry_line_count").GetInt32();
            tocPageNumbers = status.RootElement.GetProperty("page_number_count").GetInt32();
            tocMaxPageNumber = status.RootElement.GetProperty("max_page_number").GetInt32();
            expectedHeadings = status.RootElement.GetProperty("expected_heading_count").GetInt32();
            matchedHeadings = status.RootElement.GetProperty("matched_heading_count").GetInt32();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new WordMcpException(
                "uno_status_invalid",
                "$render",
                $"LibreOffice returned an invalid index-update status: {exception.Message}",
                "Retry after verifying the pinned LibreOffice/UNO runtime.");
        }

        var verifyToc = requireIndexUpdate || updatedIndexes > 0;
        if (verifyToc
            && !IsVerifiedTocStatus(
                updatedIndexes,
                updatePassCount,
                indexConverged,
                tocEntryLines,
                tocPageNumbers,
                expectedHeadings,
                matchedHeadings))
        {
            throw new WordMcpException(
                "toc_update_not_verified",
                "$render",
                "The preview document required a TOC update but LibreOffice did not expose updated entries with page numbers.",
                "Do not distribute this preview as updated; inspect the heading styles and generated TOC structure.");
        }

        var currentPrimaryHash = await HashAsync(distributionDocxPath, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(primaryHash, currentPrimaryHash))
        {
            throw new InvalidOperationException("The preview renderer modified the distribution DOCX.");
        }

        var pdf = new FileInfo(pdfPath);
        if (!pdf.Exists || pdf.Length is <= 0 || pdf.Length > options.MaxPdfBytes)
        {
            throw new WordMcpException(
                "pdf_size_out_of_range",
                "$render",
                "The rendered PDF is missing or exceeds the configured size limit.",
                "Reduce the document complexity or image sizes.");
        }

        var info = await processes.RunAsync(
            options.PdfInfoPath,
            [pdfPath],
            outputDirectory,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (info.ExitCode != 0 || !TryParsePdfInfo(info.StandardOutput, out var pageCount, out var width, out var height)
            || pageCount is < 1 || pageCount > options.MaxRenderedPages || width > 2_000 || height > 2_000)
        {
            throw new WordMcpException(
                "pdf_geometry_out_of_range",
                "$render",
                "The PDF page count or page size is invalid or outside the supported bounds.",
                "Keep the document at 50 pages or fewer with a supported paper size.");
        }

        if (!AreTocPageNumbersWithinRange(verifyToc, tocMaxPageNumber, pageCount))
        {
            throw new WordMcpException(
                "toc_page_number_out_of_range",
                "$render",
                "The updated TOC contains a page number outside the rendered document.",
                "Do not distribute this preview; simplify the layout or retry after the TOC pagination converges.");
        }

        var prefix = Path.Combine(outputDirectory, "page");
        var png = await processes.RunAsync(
            options.PdfToPngPath,
            ["-f", "1", "-l", pageCount.ToString(CultureInfo.InvariantCulture), "-r", "144", "-png", pdfPath, prefix],
            outputDirectory,
            TimeSpan.FromMinutes(Math.Min(options.JobTimeoutMinutes, 5)),
            cancellationToken).ConfigureAwait(false);
        if (png.ExitCode != 0)
        {
            throw new WordMcpException(
                "poppler_conversion_failed",
                "$render",
                "Poppler could not render the validated PDF pages.",
                "Reduce the document complexity and retry.");
        }

        var previews = Directory
            .EnumerateFiles(outputDirectory, "page-*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(ParsePageNumber)
            .ToArray();
        var totalPreviewBytes = previews.Sum(path => new FileInfo(path).Length);
        if (previews.Length != pageCount || previews.Any(path => new FileInfo(path).Length == 0)
            || totalPreviewBytes > options.MaxPreviewBytes)
        {
            throw new WordMcpException(
                "preview_output_out_of_range",
                "$render",
                "The PNG output count or total size is invalid.",
                "Reduce the document page count or visual complexity.");
        }

        string? finalizedDocumentPath = null;
        if (finalizeDocumentForDistribution)
        {
            GeneratedDocumentFinalizer.FinalizeForDistribution(previewDocx, cancellationToken);
            finalizedDocumentPath = Path.Combine(outputDirectory, "finalized-document.docx");
            File.Move(previewDocx, finalizedDocumentPath, overwrite: false);
        }

        // The mutable preview copy and LibreOffice profile are job-owned scratch data, not
        // artifacts. Remove them before the worker can publish the immutable result.
        Directory.Delete(workDirectory, recursive: true);

        return new RenderResult(
            pdfPath,
            previews,
            finalizedDocumentPath,
            pageCount,
            updatedIndexes,
            updatePassCount,
            indexConverged,
            tocEntryLines,
            tocPageNumbers,
            tocMaxPageNumber,
            expectedHeadings,
            matchedHeadings);
    }

    internal static bool IsVerifiedTocStatus(
        int updatedIndexes,
        int updatePassCount,
        bool indexConverged,
        int entryLineCount,
        int pageNumberCount,
        int expectedHeadingCount,
        int matchedHeadingCount) =>
        updatedIndexes >= 1
        && updatePassCount is >= 2 and <= 3
        && indexConverged
        && expectedHeadingCount >= 1
        && matchedHeadingCount == expectedHeadingCount
        && entryLineCount >= matchedHeadingCount
        && pageNumberCount >= matchedHeadingCount;

    internal static bool AreTocPageNumbersWithinRange(
        bool verifyToc,
        int maxPageNumber,
        int renderedPageCount) =>
        !verifyToc || maxPageNumber >= 1 && maxPageNumber <= renderedPageCount;

    private static Dictionary<string, string?> RestrictedEnvironment(string profileDirectory) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = profileDirectory,
            ["TMPDIR"] = profileDirectory,
            ["SAL_USE_VCLPLUGIN"] = "gen",
        };

    private static int FindLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParsePdfInfo(string output, out int pages, out double width, out double height)
    {
        pages = 0;
        width = 0;
        height = 0;
        var pageMatch = PagesPattern().Match(output);
        var sizeMatch = PageSizePattern().Match(output);
        return pageMatch.Success
               && sizeMatch.Success
               && int.TryParse(pageMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out pages)
               && double.TryParse(sizeMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out width)
               && double.TryParse(sizeMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out height);
    }

    private static int ParsePageNumber(string path)
    {
        var match = PreviewPagePattern().Match(Path.GetFileName(path));
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : int.MaxValue;
    }

    [GeneratedRegex("(?m)^Pages:\\s+(\\d+)\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PagesPattern();

    [GeneratedRegex("(?m)^Page size:\\s+([0-9.]+) x ([0-9.]+) pts", RegexOptions.CultureInvariant)]
    private static partial Regex PageSizePattern();

    [GeneratedRegex("\\Apage-(\\d+)\\.png\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PreviewPagePattern();
}
