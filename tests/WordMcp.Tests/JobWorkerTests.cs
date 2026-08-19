using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using WordMcp.Artifacts;
using WordMcp.Domain;
using WordMcp.Jobs;
using WordMcp.Rendering;
using WordMcp.Security;
using WordMcp.Storage;
using WordMcp.Word;

namespace WordMcp.Tests;

public sealed class JobWorkerTests
{
    [Fact]
    public async Task StartupRecoveryRequeuesMaximumNormalActiveSetWithoutDroppingJobs()
    {
        using var environment = new TestEnvironment();
        using var document = DocxTestPackage.Create();
        using var dependencies = new WorkerDependencies(environment);
        var staleRunDirectory = Path.Combine(
            dependencies.Jobs.GetJobDirectory("job_000000000000000000000000"),
            "runs",
            "run_staleAAAAAAAAAAAAAAAAAAA");
        for (var index = 0; index < 15; index++)
        {
            var jobId = $"job_{index:D24}";
            await CreateAnalyzeJobAsync(
                dependencies.Jobs,
                document.Path,
                jobId,
                JobState.Running,
                environment.Time.GetUtcNow().AddSeconds(index));
            if (index == 0)
            {
                Directory.CreateDirectory(staleRunDirectory);
                await File.WriteAllBytesAsync(
                    Path.Combine(staleRunDirectory, "partial.bin"),
                    [1],
                    TestContext.Current.CancellationToken);
            }
        }

        using var worker = dependencies.CreateWorker(
            new OpenXmlWordDocumentEngine(environment.Options.Value, environment.Time));
        await worker.StartAsync(CancellationToken.None);
        var completed = await WaitForTerminalJobsAsync(dependencies.Jobs, 15, TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.All(completed, job => Assert.Equal(JobState.Succeeded, job.State));
        Assert.Equal(15, (await dependencies.Analyses.ListAsync(CancellationToken.None)).Count);
        Assert.False(Directory.Exists(staleRunDirectory));
    }

    [Fact]
    public async Task UnsafeAnalyzeEndsAsRejectedWithoutReturningAnalysis()
    {
        using var environment = new TestEnvironment();
        using var dependencies = new WorkerDependencies(environment);
        var jobId = "job_unsafe000000000000000000";
        var directory = dependencies.Jobs.CreateJobDirectory(jobId);
        var inputDirectory = Path.Combine(directory, "input");
        Directory.CreateDirectory(inputDirectory);
        var inputPath = Path.Combine(inputDirectory, "source.docx");
        await File.WriteAllBytesAsync(
            inputPath,
            "not-a-zip"u8.ToArray(),
            TestContext.Current.CancellationToken);
        var hash = await HashAsync(inputPath);
        await dependencies.Jobs.CreateAsync(
            CreateAnalyzeJob(jobId, inputPath, hash, JobState.Queued, environment.Time.GetUtcNow()),
            CancellationToken.None);

        using var worker = dependencies.CreateWorker(
            new OpenXmlWordDocumentEngine(environment.Options.Value, environment.Time));
        await worker.StartAsync(CancellationToken.None);
        var completed = await WaitForTerminalJobsAsync(dependencies.Jobs, 1, TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        var rejected = Assert.Single(completed);
        Assert.Equal(JobState.RejectedUnsafeDocument, rejected.State);
        Assert.NotNull(rejected.Error);
        Assert.Null(rejected.Result?.AnalysisId);
    }

    [Fact]
    public async Task UserCancellationInterruptsRunningEngineAndPreservesCanceledTerminalState()
    {
        using var environment = new TestEnvironment();
        using var document = DocxTestPackage.Create();
        using var dependencies = new WorkerDependencies(environment);
        var jobId = "job_cancel000000000000000000";
        await CreateAnalyzeJobAsync(
            dependencies.Jobs,
            document.Path,
            jobId,
            JobState.Queued,
            environment.Time.GetUtcNow());
        using var engine = new BlockingEngine();
        using var worker = dependencies.CreateWorker(engine);
        await worker.StartAsync(CancellationToken.None);
        await engine.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await dependencies.Jobs.UpdateAsync(
            jobId,
            current => current with
            {
                State = JobState.Canceled,
                Error = new JobError(
                    "canceled_by_user",
                    null,
                    "The job was canceled by its owner.",
                    "Submit a new job only if still required."),
                UpdatedAt = environment.Time.GetUtcNow(),
            },
            CancellationToken.None);
        Assert.True(dependencies.Cancellations.Cancel(jobId));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        var canceled = await dependencies.Jobs.GetAsync(jobId, CancellationToken.None);
        Assert.Equal(JobState.Canceled, canceled?.State);
        Assert.Equal("canceled_by_user", canceled?.Error?.Code);
    }

    [Fact]
    public async Task RepeatedAnalyzeInSameScopeUsesPersistentContentCacheWithFreshOpaqueIds()
    {
        using var environment = new TestEnvironment();
        using var document = DocxTestPackage.Create();
        using var dependencies = new WorkerDependencies(environment);
        var engine = new CountingAnalysisEngine(
            new OpenXmlWordDocumentEngine(environment.Options.Value, environment.Time));
        await CreateAnalyzeJobAsync(
            dependencies.Jobs,
            document.Path,
            "job_cacheA000000000000000000",
            JobState.Queued,
            environment.Time.GetUtcNow());
        using (var firstWorker = dependencies.CreateWorker(engine))
        {
            await firstWorker.StartAsync(CancellationToken.None);
            _ = await WaitForTerminalJobsAsync(dependencies.Jobs, 1, TimeSpan.FromSeconds(5));
            await firstWorker.StopAsync(CancellationToken.None);
        }

        environment.Time.Advance(TimeSpan.FromMinutes(1));
        await CreateAnalyzeJobAsync(
            dependencies.Jobs,
            document.Path,
            "job_cacheB000000000000000000",
            JobState.Queued,
            environment.Time.GetUtcNow());
        using (var secondWorker = dependencies.CreateWorker(engine))
        {
            await secondWorker.StartAsync(CancellationToken.None);
            _ = await WaitForTerminalJobsAsync(dependencies.Jobs, 2, TimeSpan.FromSeconds(5));
            await secondWorker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, engine.AnalysisCount);
        var snapshots = await dependencies.Analyses.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(2, snapshots.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(snapshots[0].Targets.Keys.Intersect(snapshots[1].Targets.Keys, StringComparer.Ordinal));
        Assert.Single(await dependencies.Analyses.ListCacheAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkerNeverRunsMoreThanConfiguredMaximumThreeJobsConcurrently()
    {
        using var environment = new TestEnvironment();
        using var document = DocxTestPackage.Create();
        using var dependencies = new WorkerDependencies(environment);
        using var release = new ManualResetEventSlim();
        var engine = new ConcurrencyTrackingEngine(
            new OpenXmlWordDocumentEngine(environment.Options.Value, environment.Time),
            release);
        for (var index = 0; index < 6; index++)
        {
            await CreateAnalyzeJobAsync(
                dependencies.Jobs,
                document.Path,
                $"job_parallel{index:D16}",
                JobState.Queued,
                environment.Time.GetUtcNow().AddSeconds(index));
        }

        using var worker = dependencies.CreateWorker(engine);
        await worker.StartAsync(CancellationToken.None);
        await engine.MaximumWorkersStarted.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(3, engine.MaximumObserved);
        release.Set();
        var completed = await WaitForTerminalJobsAsync(dependencies.Jobs, 6, TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.All(completed, job => Assert.Equal(JobState.Succeeded, job.State));
        Assert.Equal(3, engine.MaximumObserved);
    }

    [Fact]
    public async Task BoundedEngineTimeoutEndsAsTimedOutWithStructuredError()
    {
        using var environment = new TestEnvironment();
        using var document = DocxTestPackage.Create();
        using var dependencies = new WorkerDependencies(environment);
        var jobId = "job_timeout00000000000000000";
        await CreateAnalyzeJobAsync(
            dependencies.Jobs,
            document.Path,
            jobId,
            JobState.Queued,
            environment.Time.GetUtcNow());

        using var worker = dependencies.CreateWorker(new ImmediateTimeoutEngine());
        await worker.StartAsync(CancellationToken.None);
        var completed = await WaitForTerminalJobsAsync(dependencies.Jobs, 1, TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        var timedOut = Assert.Single(completed);
        Assert.Equal(JobState.TimedOut, timedOut.State);
        Assert.Equal("job_timeout", timedOut.Error?.Code);
        Assert.Null(timedOut.Result);
    }

    private static async Task CreateAnalyzeJobAsync(
        FileJobRepository jobs,
        string sourcePath,
        string jobId,
        JobState state,
        DateTimeOffset createdAt)
    {
        var directory = jobs.CreateJobDirectory(jobId);
        var inputDirectory = Path.Combine(directory, "input");
        Directory.CreateDirectory(inputDirectory);
        var inputPath = Path.Combine(inputDirectory, "source.docx");
        File.Copy(sourcePath, inputPath);
        await jobs.CreateAsync(
            CreateAnalyzeJob(jobId, inputPath, await HashAsync(inputPath), state, createdAt),
            CancellationToken.None);
    }

    private static WordJob CreateAnalyzeJob(
        string jobId,
        string inputPath,
        string sha256,
        JobState state,
        DateTimeOffset createdAt) => new(
        jobId,
        "user-scope",
        "conversation-scope",
        JobKind.Analyze,
        state,
        System.Text.Json.JsonSerializer.SerializeToElement(new AnalyzePayload("source")),
        inputPath,
        sha256,
        null,
        null,
        0,
        [],
        createdAt,
        createdAt,
        createdAt.AddDays(7));

    private static async Task<WordJob[]> WaitForTerminalJobsAsync(
        FileJobRepository jobs,
        int expectedCount,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var current = await jobs.ListAsync(CancellationToken.None);
            if (current.Count == expectedCount && current.All(job => job.State is not (JobState.Queued or JobState.Running)))
            {
                return current.ToArray();
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The test jobs did not reach terminal states.");
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private sealed class WorkerDependencies : IDisposable
    {
        private readonly TestEnvironment environment;
        private readonly JobChannel queue;
        private readonly DocxPackageGuard guard;
        private readonly ArtifactService artifactService;
        private readonly StorageQuotaService quota;
        private readonly DraftRepository drafts;

        public WorkerDependencies(TestEnvironment environment)
        {
            this.environment = environment;
            Jobs = new FileJobRepository(environment.Options);
            Analyses = new AnalysisRepository(environment.Options);
            drafts = new DraftRepository(environment.Options);
            quota = new StorageQuotaService(
                Jobs,
                drafts,
                Analyses,
                environment.Options,
                environment.Time);
            queue = new JobChannel(environment.Options);
            guard = new DocxPackageGuard(environment.Options);
            Cancellations = new JobCancellationRegistry();
            var retention = new RetentionPolicy(environment.Options);
            artifactService = new ArtifactService(
                Jobs,
                new ArtifactTokenService(environment.Options, environment.Time),
                retention,
                environment.Options,
                environment.Time);
        }

        public FileJobRepository Jobs { get; }

        public AnalysisRepository Analyses { get; }

        public JobCancellationRegistry Cancellations { get; }

        public JobWorker CreateWorker(IWordDocumentEngine engine) => new(
            queue,
            Jobs,
            engine,
            Analyses,
            quota,
            new DocumentRenderer(new ProcessRunner(), environment.Options),
            guard,
            artifactService,
            Cancellations,
            environment.Options,
            environment.Time,
            NullLogger<JobWorker>.Instance);

        public void Dispose()
        {
            quota.Dispose();
            drafts.Dispose();
            Analyses.Dispose();
            Jobs.Dispose();
        }
    }

    private sealed class BlockingEngine : IWordDocumentEngine, IDisposable
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public AnalysisSnapshot Analyze(WordAnalysisRequest request, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable test path.");
        }

        public WordMutationResult ReplaceText(
            WordMutationRequest request,
            IReadOnlyList<TextReplacementRequest> replacements,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordMutationResult ApplyEdits(
            WordMutationRequest request,
            IReadOnlyList<AtomicEditRequest> edits,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordMutationResult PopulateTemplate(
            WordTemplatePopulationRequest request,
            IReadOnlyList<TemplateFieldRequest> fields,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordGenerationResult Generate(
            WordGenerationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public OpenXmlValidationReport ValidateExistingEdit(
            string sourcePath,
            string candidatePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public OpenXmlValidationReport ValidateNewDocument(
            string candidatePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
            started.TrySetCanceled();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class CountingAnalysisEngine(IWordDocumentEngine inner) : IWordDocumentEngine
    {
        private int analysisCount;

        public int AnalysisCount => Volatile.Read(ref analysisCount);

        public AnalysisSnapshot Analyze(WordAnalysisRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref analysisCount);
            return inner.Analyze(request, cancellationToken);
        }

        public WordMutationResult ReplaceText(
            WordMutationRequest request,
            IReadOnlyList<TextReplacementRequest> replacements,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordMutationResult ApplyEdits(
            WordMutationRequest request,
            IReadOnlyList<AtomicEditRequest> edits,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordMutationResult PopulateTemplate(
            WordTemplatePopulationRequest request,
            IReadOnlyList<TemplateFieldRequest> fields,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordGenerationResult Generate(
            WordGenerationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public OpenXmlValidationReport ValidateExistingEdit(
            string sourcePath,
            string candidatePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public OpenXmlValidationReport ValidateNewDocument(
            string candidatePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ConcurrencyTrackingEngine(
        IWordDocumentEngine inner,
        ManualResetEventSlim release) : IWordDocumentEngine
    {
        private readonly TaskCompletionSource maximumWorkersStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        private int maximumObserved;

        public Task MaximumWorkersStarted => maximumWorkersStarted.Task;

        public int MaximumObserved => Volatile.Read(ref maximumObserved);

        public AnalysisSnapshot Analyze(WordAnalysisRequest request, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(current);
            if (current >= 3)
            {
                maximumWorkersStarted.TrySetResult();
            }

            try
            {
                release.Wait(cancellationToken);
                return inner.Analyze(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        public WordMutationResult ReplaceText(
            WordMutationRequest request,
            IReadOnlyList<TextReplacementRequest> replacements,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordMutationResult ApplyEdits(
            WordMutationRequest request,
            IReadOnlyList<AtomicEditRequest> edits,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordMutationResult PopulateTemplate(
            WordTemplatePopulationRequest request,
            IReadOnlyList<TemplateFieldRequest> fields,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordGenerationResult Generate(
            WordGenerationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public OpenXmlValidationReport ValidateExistingEdit(
            string sourcePath,
            string candidatePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public OpenXmlValidationReport ValidateNewDocument(
            string candidatePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximumObserved);
                if (candidate <= current
                    || Interlocked.CompareExchange(ref maximumObserved, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ImmediateTimeoutEngine : IWordDocumentEngine
    {
        public AnalysisSnapshot Analyze(WordAnalysisRequest request, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("Synthetic bounded subprocess timeout.");

        public WordMutationResult ReplaceText(
            WordMutationRequest request,
            IReadOnlyList<TextReplacementRequest> replacements,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordMutationResult ApplyEdits(
            WordMutationRequest request,
            IReadOnlyList<AtomicEditRequest> edits,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordMutationResult PopulateTemplate(
            WordTemplatePopulationRequest request,
            IReadOnlyList<TemplateFieldRequest> fields,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WordGenerationResult Generate(
            WordGenerationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public OpenXmlValidationReport ValidateExistingEdit(
            string sourcePath,
            string candidatePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public OpenXmlValidationReport ValidateNewDocument(
            string candidatePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
