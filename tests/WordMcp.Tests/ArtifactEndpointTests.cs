using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using WordMcp.Artifacts;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class ArtifactEndpointTests
{
    private const string SharedSecret = "artifact-endpoint-shared-secret-123456";
    private const string ArtifactSigningKey = "artifact-endpoint-signing-key-1234567890";
    private const string ScopeHmacKey = "artifact-endpoint-scope-key-123456789012";
    private const string JobId = "job_AAAAAAAAAAAAAAAAAAAAAAAA";
    private const string RunId = "run_AAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FileName = "document.docx";
    private const string MediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Fact]
    public async Task HeadAndGetServePersistedArtifactWithoutStartingDownloadTimerOnHead()
    {
        using var environment = new TestEnvironment();
        var port = GetAvailableLoopbackPort();
        var seeded = await SeedArtifactAsync(environment, port);
        using var process = StartApi(seeded.Options.Value, port);
        var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = CreateClient(port);

        try
        {
            await WaitUntilLiveAsync(client, process, timeout.Token);

            using (var request = new HttpRequestMessage(HttpMethod.Head, seeded.ValidRelativeUrl))
            using (var response = await client.SendAsync(request, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                AssertArtifactHeaders(response, seeded.Content.Length);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            }

            var afterHead = await ReadArtifactAsync(seeded.Options, timeout.Token);
            Assert.Equal(seeded.ArtifactPath, afterHead.Path);
            Assert.Null(afterHead.FirstDownloadedAt);

            using (var response = await client.GetAsync(seeded.ValidRelativeUrl, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                AssertArtifactHeaders(response, seeded.Content.Length);
                Assert.Equal(seeded.Content, await response.Content.ReadAsByteArrayAsync(timeout.Token));
            }

            var afterGet = await ReadArtifactAsync(seeded.Options, timeout.Token);
            Assert.NotNull(afterGet.FirstDownloadedAt);

            using var disallowedRequest = new HttpRequestMessage(HttpMethod.Post, seeded.ValidRelativeUrl);
            using var disallowedResponse = await client.SendAsync(disallowedRequest, timeout.Token);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, disallowedResponse.StatusCode);
        }
        finally
        {
            await StopApiAsync(process, standardOutput, standardError);
        }
    }

    [Fact]
    public async Task TamperingAndExpiryAreRejectedAndConcurrentGetsRecordFirstDownloadOnce()
    {
        using var environment = new TestEnvironment();
        var port = GetAvailableLoopbackPort();
        var seeded = await SeedArtifactAsync(environment, port);
        using var process = StartApi(seeded.Options.Value, port);
        var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = CreateClient(port);

        try
        {
            await WaitUntilLiveAsync(client, process, timeout.Token);

            await AssertNotFoundAsync(
                client,
                BuildRelativeUrl(JobId, seeded.ArtifactId, "renamed.docx", "attachment", seeded.Token),
                timeout.Token);
            await AssertNotFoundAsync(
                client,
                BuildRelativeUrl(JobId, seeded.ArtifactId, FileName, "inline", seeded.Token),
                timeout.Token);
            await AssertNotFoundAsync(
                client,
                BuildRelativeUrl(JobId, seeded.ArtifactId, FileName, "attachment", TamperToken(seeded.Token)),
                timeout.Token);
            await AssertNotFoundAsync(
                client,
                BuildRelativeUrl(
                    JobId,
                    seeded.ArtifactId,
                    FileName,
                    "attachment",
                    CreateExpiredToken(JobId, seeded.ArtifactId, FileName, "attachment")),
                timeout.Token);
            Assert.Null((await ReadArtifactAsync(seeded.Options, timeout.Token)).FirstDownloadedAt);

            var downloads = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => DownloadAsync(client, seeded.ValidRelativeUrl, timeout.Token)));
            Assert.All(downloads, content => Assert.Equal(seeded.Content, content));

            var firstRecorded = (await ReadArtifactAsync(seeded.Options, timeout.Token)).FirstDownloadedAt;
            Assert.NotNull(firstRecorded);

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            Assert.Equal(seeded.Content, await DownloadAsync(client, seeded.ValidRelativeUrl, timeout.Token));
            var afterLaterGet = (await ReadArtifactAsync(seeded.Options, timeout.Token)).FirstDownloadedAt;
            Assert.Equal(firstRecorded, afterLaterGet);
        }
        finally
        {
            await StopApiAsync(process, standardOutput, standardError);
        }
    }

    [Theory]
    [InlineData("unsafe-basename")]
    [InlineData("size-mismatch")]
    [InlineData("outside-published-run")]
    [InlineData("symbolic-link")]
    [InlineData("legacy-duplicate")]
    public async Task PersistedArtifactHydrationRejectsUnsafeOrAmbiguousCandidates(string scenario)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var options = CreateOptions(environment, GetAvailableLoopbackPort());
        using var jobs = new FileJobRepository(options);
        var jobDirectory = jobs.CreateJobDirectory(JobId);
        var publishedDirectory = Path.Combine(jobDirectory, "runs", RunId);
        Directory.CreateDirectory(publishedDirectory);
        var artifactPath = Path.Combine(publishedDirectory, FileName);
        var artifactFileName = FileName;
        long artifactBytes = 3;
        string? publishedRunId = RunId;

        switch (scenario)
        {
            case "unsafe-basename":
                await File.WriteAllBytesAsync(
                    artifactPath,
                    [1, 2, 3],
                    TestContext.Current.CancellationToken);
                artifactFileName = "../document.docx";
                break;
            case "size-mismatch":
                await File.WriteAllBytesAsync(
                    artifactPath,
                    [1, 2, 3],
                    TestContext.Current.CancellationToken);
                artifactBytes = 4;
                break;
            case "outside-published-run":
                var otherRun = Path.Combine(jobDirectory, "runs", "run_BBBBBBBBBBBBBBBBBBBBBBBB");
                Directory.CreateDirectory(otherRun);
                artifactPath = Path.Combine(otherRun, FileName);
                await File.WriteAllBytesAsync(
                    artifactPath,
                    [1, 2, 3],
                    TestContext.Current.CancellationToken);
                break;
            case "symbolic-link":
                var outsidePath = Path.Combine(environment.Root, "outside-artifact.docx");
                await File.WriteAllBytesAsync(
                    outsidePath,
                    [1, 2, 3],
                    TestContext.Current.CancellationToken);
                File.CreateSymbolicLink(artifactPath, outsidePath);
                break;
            case "legacy-duplicate":
                publishedRunId = null;
                var first = Path.Combine(jobDirectory, "runs", RunId, FileName);
                var second = Path.Combine(
                    jobDirectory,
                    "runs",
                    "run_CCCCCCCCCCCCCCCCCCCCCCCC",
                    FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(second)!);
                await File.WriteAllBytesAsync(first, [1, 2, 3], TestContext.Current.CancellationToken);
                await File.WriteAllBytesAsync(second, [1, 2, 3], TestContext.Current.CancellationToken);
                artifactPath = first;
                break;
            default:
                throw new InvalidOperationException("The test scenario is not supported.");
        }

        var now = TimeProvider.System.GetUtcNow();
        var artifact = new ArtifactRecord(
            "art_AAAAAAAAAAAAAAAAAAAAAAAA",
            "document",
            artifactFileName,
            MediaType,
            artifactPath,
            artifactBytes,
            now,
            now.AddDays(7));
        var job = new WordJob(
            JobId,
            "artifact-endpoint-user",
            "artifact-endpoint-conversation",
            JobKind.FinishDocument,
            JobState.Succeeded,
            JsonSerializer.SerializeToElement(new { operation = scenario }),
            null,
            null,
            JobId,
            null,
            0,
            [],
            now,
            now,
            now.AddDays(7),
            Result: new JobResult(Artifacts: [artifact]),
            PublishedRunId: publishedRunId);
        await jobs.CreateAsync(job, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            jobs.GetAsync(JobId, TestContext.Current.CancellationToken));
    }

    private static async Task<SeededArtifact> SeedArtifactAsync(TestEnvironment environment, int port)
    {
        var options = CreateOptions(environment, port);
        using var jobs = new FileJobRepository(options);
        var runDirectory = Path.Combine(jobs.CreateJobDirectory(JobId), "runs", RunId);
        Directory.CreateDirectory(runDirectory);
        var artifactPath = Path.Combine(runDirectory, FileName);
        var content = "persisted-artifact-endpoint-content"u8.ToArray();
        await File.WriteAllBytesAsync(artifactPath, content, TestContext.Current.CancellationToken);

        var timeProvider = TimeProvider.System;
        var artifacts = new ArtifactService(
            jobs,
            new ArtifactTokenService(options, timeProvider),
            new RetentionPolicy(options),
            options,
            timeProvider);
        var artifact = artifacts.CreateRecord("document", artifactPath, FileName);
        var now = timeProvider.GetUtcNow();
        var job = new WordJob(
            JobId,
            "artifact-endpoint-user",
            "artifact-endpoint-conversation",
            JobKind.FinishDocument,
            JobState.Succeeded,
            JsonSerializer.SerializeToElement(new { operation = "artifact-endpoint-test" }),
            null,
            null,
            JobId,
            null,
            0,
            [],
            now,
            now,
            now.AddDays(options.Value.RetentionDays),
            Result: new JobResult(Artifacts: [artifact]),
            ReservedBytes: artifact.Bytes,
            PublishedRunId: RunId);
        await jobs.CreateAsync(job, TestContext.Current.CancellationToken);

        var link = Assert.Single(artifacts.CreateLinks(job));
        var linkUri = new Uri(link.Url, UriKind.Absolute);
        var token = QueryHelpers.ParseQuery(linkUri.Query)["token"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var persistedPath = Path.Combine(jobs.GetJobDirectory(JobId), "job.json");
        var persistedJson = await File.ReadAllTextAsync(persistedPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(artifactPath, persistedJson, StringComparison.Ordinal);
        using (var persistedDocument = JsonDocument.Parse(persistedJson))
        {
            var persistedArtifact = persistedDocument.RootElement
                .GetProperty("result")
                .GetProperty("artifacts")[0];
            Assert.False(persistedArtifact.TryGetProperty("path", out _));
        }

        var jobViewJson = JsonSerializer.Serialize(new JobView(
            job.Id,
            "finish_document",
            "succeeded",
            job.CreatedAt,
            job.UpdatedAt,
            job.Result,
            null,
            [link],
            null));
        using (var jobViewDocument = JsonDocument.Parse(jobViewJson))
        {
            var wireArtifact = jobViewDocument.RootElement
                .GetProperty("result")
                .GetProperty("artifacts")[0];
            Assert.False(wireArtifact.TryGetProperty("path", out _));
            Assert.False(wireArtifact.TryGetProperty("Path", out _));
        }

        return new SeededArtifact(
            options,
            artifact.ArtifactId,
            token,
            content,
            Path.GetFullPath(artifactPath));
    }

    private static IOptions<WordMcpOptions> CreateOptions(TestEnvironment environment, int port)
    {
        var source = environment.Options.Value;
        return Microsoft.Extensions.Options.Options.Create(new WordMcpOptions
        {
            SharedSecret = SharedSecret,
            ArtifactSigningKey = ArtifactSigningKey,
            ScopeHmacKey = ScopeHmacKey,
            PublicBaseUrl = $"http://127.0.0.1:{port}",
            LocalDevelopment = true,
            StorageRoot = source.StorageRoot,
            LibreChatUploadsRoot = source.LibreChatUploadsRoot,
            TemplatesRoot = source.TemplatesRoot,
            LibreOfficePath = source.LibreOfficePath,
            PythonPath = source.PythonPath,
            UnoScriptPath = source.UnoScriptPath,
            PdfInfoPath = source.PdfInfoPath,
            PdfToPngPath = source.PdfToPngPath,
        });
    }

    private static Process StartApi(WordMcpOptions options, int port)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["WordMcp__SharedSecret"] = SharedSecret;
        startInfo.Environment["WordMcp__ArtifactSigningKey"] = ArtifactSigningKey;
        startInfo.Environment["WordMcp__ScopeHmacKey"] = ScopeHmacKey;
        startInfo.Environment["WordMcp__PublicBaseUrl"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["WordMcp__LocalDevelopment"] = "true";
        startInfo.Environment["WordMcp__StorageRoot"] = options.StorageRoot;
        startInfo.Environment["WordMcp__LibreChatUploadsRoot"] = options.LibreChatUploadsRoot;
        startInfo.Environment["WordMcp__TemplatesRoot"] = options.TemplatesRoot;

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The artifact endpoint test server could not be started.");
        }

        return process;
    }

    private static HttpClient CreateClient(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static async Task WaitUntilLiveAsync(
        HttpClient client,
        Process process,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("The artifact endpoint test server exited before becoming live.");
            }

            try
            {
                using var response = await client.GetAsync("/health/live", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Kestrel has not bound its loopback socket yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task StopApiAsync(
        Process process,
        Task<string> standardOutput,
        Task<string> standardError)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(CancellationToken.None);
        _ = await standardOutput;
        _ = await standardError;
    }

    private static void AssertArtifactHeaders(HttpResponseMessage response, int contentLength)
    {
        Assert.Equal((long)contentLength, response.Content.Headers.ContentLength);
        Assert.Equal(MediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(true, response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var contentDisposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(contentDisposition);
        Assert.Equal("attachment", contentDisposition.DispositionType);
        Assert.Equal(FileName, contentDisposition.FileName);
        Assert.Equal(FileName, contentDisposition.FileNameStar);
    }

    private static async Task AssertNotFoundAsync(
        HttpClient client,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(relativeUrl, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<byte[]> DownloadAsync(
        HttpClient client,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(relativeUrl, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task<ArtifactRecord> ReadArtifactAsync(
        IOptions<WordMcpOptions> options,
        CancellationToken cancellationToken)
    {
        using var jobs = new FileJobRepository(options);
        var job = await jobs.GetAsync(JobId, cancellationToken);
        Assert.NotNull(job);
        var artifacts = job.Result?.Artifacts;
        Assert.NotNull(artifacts);
        return Assert.Single(artifacts);
    }

    private static string BuildRelativeUrl(
        string jobId,
        string artifactId,
        string fileName,
        string disposition,
        string token) => string.Concat(
        "/artifacts/",
        Uri.EscapeDataString(jobId),
        "/",
        Uri.EscapeDataString(artifactId),
        "/",
        Uri.EscapeDataString(fileName),
        "?disposition=",
        Uri.EscapeDataString(disposition),
        "&token=",
        Uri.EscapeDataString(token));

    private static string TamperToken(string token)
    {
        var parts = token.Split('.', StringSplitOptions.None);
        Assert.Equal(3, parts.Length);
        Assert.NotEmpty(parts[2]);
        var signature = parts[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        return string.Join('.', parts[0], parts[1], new string(signature));
    }

    private static string CreateExpiredToken(
        string jobId,
        string artifactId,
        string fileName,
        string disposition)
    {
        const string version = "v1";
        var expires = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var canonical = string.Join('\n', version, jobId, artifactId, fileName, expires, disposition);
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(ArtifactSigningKey),
            Encoding.UTF8.GetBytes(canonical));
        return string.Join('.', version, expires, WebEncoders.Base64UrlEncode(signature));
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record SeededArtifact(
        IOptions<WordMcpOptions> Options,
        string ArtifactId,
        string Token,
        byte[] Content,
        string ArtifactPath)
    {
        public string ValidRelativeUrl =>
            BuildRelativeUrl(JobId, ArtifactId, FileName, "attachment", Token);
    }
}
