using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Storage;

public sealed class FileJobRepository : IDisposable
{
    private readonly string storageRoot;
    private readonly string jobsRoot;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileJobRepository(IOptions<WordMcpOptions> options)
    {
        storageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.StorageRoot));
        jobsRoot = Path.Combine(storageRoot, "jobs");
        Directory.CreateDirectory(jobsRoot);
    }

    public string CreateJobDirectory(string jobId)
    {
        ValidateId(jobId);
        var path = Path.Combine(jobsRoot, jobId);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task CreateAsync(WordJob job, CancellationToken cancellationToken)
    {
        ValidateId(job.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = JobPath(job.Id);
            if (File.Exists(path))
            {
                throw new InvalidOperationException("The generated job identifier already exists.");
            }

            await JsonFileStore.WriteAtomicAsync(path, job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WordJob?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!Identifier.IsValid(jobId, "job_"))
        {
            return null;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadJobAsync(JobPath(jobId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WordJob> UpdateAsync(
        string jobId,
        Func<WordJob, WordJob> update,
        CancellationToken cancellationToken)
    {
        ValidateId(jobId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = JobPath(jobId);
            var current = await ReadJobAsync(path, cancellationToken).ConfigureAwait(false)
                ?? throw new WordMcpException(
                    "job_not_found",
                    "$.job_id",
                    "The job was not found.",
                    "Use a job_id returned in this conversation.");
            var replacement = update(current);
            await JsonFileStore.WriteAtomicAsync(path, replacement, cancellationToken).ConfigureAwait(false);
            return replacement;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Lists validated job metadata without resolving artifact paths. Broad consumers such as
    /// recovery, quota accounting, queue admission, and latest-job selection must not become
    /// unavailable because an unrelated completed job has lost one artifact.
    /// </summary>
    public Task<IReadOnlyList<WordJob>> ListAsync(CancellationToken cancellationToken) =>
        ListCoreAsync(hydrateArtifactPaths: false, cancellationToken);

    internal Task<IReadOnlyList<WordJob>> ListForRetentionAsync(CancellationToken cancellationToken) =>
        ListAsync(cancellationToken);

    private async Task<IReadOnlyList<WordJob>> ListCoreAsync(
        bool hydrateArtifactPaths,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = new List<WordJob>();
            foreach (var path in Directory.EnumerateFiles(jobsRoot, "job.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = await ReadJobAsync(path, hydrateArtifactPaths, cancellationToken).ConfigureAwait(false);
                if (job is not null)
                {
                    jobs.Add(job);
                }
            }

            return jobs;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WordJob?> LatestAsync(
        CallerScope scope,
        bool successfulDeclarativeOnly,
        CancellationToken cancellationToken)
    {
        var jobs = await ListAsync(cancellationToken).ConfigureAwait(false);
        return jobs
            .Where(job => job.UserScope == scope.UserScope && job.ConversationScope == scope.ConversationScope)
            .Where(job => !successfulDeclarativeOnly
                          || (job.State == JobState.Succeeded && job.DocumentDefinition is not null))
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public async Task<(WordJob Job, ArtifactRecord Artifact)?> FindArtifactAsync(
        CallerScope scope,
        string artifactId,
        CancellationToken cancellationToken)
    {
        if (!Identifier.IsValid(artifactId, "art_"))
        {
            return null;
        }

        var jobs = await ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var metadata in jobs.Where(item => item.UserScope == scope.UserScope
                                                    && item.ConversationScope == scope.ConversationScope))
        {
            if (metadata.Result?.Artifacts?.Any(item => item.ArtifactId == artifactId) != true)
            {
                continue;
            }

            // Hydrate only the selected owner job. Missing or unsafe files still fail closed for
            // the artifact being imported, without poisoning unrelated list consumers.
            var job = await GetAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
            var artifact = job?.Result?.Artifacts?.FirstOrDefault(item => item.ArtifactId == artifactId);
            if (job is not null && artifact is not null)
            {
                return (job, artifact);
            }
        }

        return null;
    }

    public string GetJobDirectory(string jobId)
    {
        ValidateId(jobId);
        return Path.Combine(jobsRoot, jobId);
    }

    public async Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        ValidateId(jobId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = GetJobDirectory(jobId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> CleanupUnpublishedRunsAsync(string jobId, CancellationToken cancellationToken)
    {
        ValidateId(jobId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await ReadJobAsync(
                    JobPath(jobId),
                    hydrateArtifactPaths: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (job is null)
            {
                return 0;
            }

            string? publishedRunId = null;
            if (job.PublishedRunId is not null)
            {
                if (!Identifier.IsValid(job.PublishedRunId, "run_"))
                {
                    throw new InvalidDataException("The persisted published run identifier is invalid.");
                }

                publishedRunId = job.PublishedRunId;
            }
            else if (job.Result?.Artifacts is { Count: > 0 })
            {
                // Pre-reservation records did not persist a run identifier. Preserve them
                // conservatively rather than risking deletion of a published artifact.
                return 0;
            }

            var canonicalJobsRoot = Path.GetFullPath(jobsRoot);
            var jobRoot = Path.GetFullPath(GetJobDirectory(jobId));
            var runsRoot = Path.GetFullPath(Path.Combine(jobRoot, "runs"));
            if (Path.GetDirectoryName(jobRoot) != canonicalJobsRoot
                || Path.GetDirectoryName(runsRoot) != jobRoot
                || !Directory.Exists(runsRoot))
            {
                return 0;
            }

            if ((File.GetAttributes(canonicalJobsRoot) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(jobRoot) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(runsRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Job storage cleanup does not follow reparse points.");
            }

            if (publishedRunId is not null)
            {
                ValidatePublishedRun(job, runsRoot, publishedRunId);
            }

            var deleted = 0;
            foreach (var candidate in Directory.EnumerateDirectories(runsRoot, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(candidate);
                var runId = Path.GetFileName(fullPath);
                if (Path.GetDirectoryName(fullPath) != runsRoot || !Identifier.IsValid(runId, "run_"))
                {
                    throw new InvalidDataException("The job runs directory contains an unexpected child directory.");
                }

                if (runId == publishedRunId)
                {
                    continue;
                }

                DeleteValidatedRunDirectory(runsRoot, fullPath, runId);
                deleted++;
            }

            return deleted;
        }
        finally
        {
            gate.Release();
        }
    }

    private string JobPath(string jobId) => Path.Combine(GetJobDirectory(jobId), "job.json");

    private async Task<WordJob?> ReadJobAsync(string path, CancellationToken cancellationToken)
    {
        return await ReadJobAsync(path, hydrateArtifactPaths: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WordJob?> ReadJobAsync(
        string path,
        bool hydrateArtifactPaths,
        CancellationToken cancellationToken)
    {
        var job = await JsonFileStore.ReadAsync<WordJob>(path, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        ValidatePersistedJobLocation(job, path);
        return hydrateArtifactPaths ? HydrateArtifactPaths(job) : job;
    }

    private void ValidatePersistedJobLocation(WordJob job, string persistedPath)
    {
        if (!Identifier.IsValid(job.Id, "job_"))
        {
            throw new InvalidDataException("The persisted job identifier is invalid.");
        }

        var expectedJobPath = Path.GetFullPath(JobPath(job.Id));
        if (!string.Equals(Path.GetFullPath(persistedPath), expectedJobPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The persisted job is outside its owning directory.");
        }

        try
        {
            LinuxFileIdentity.EnsureDirectoryUnderRoot(storageRoot, ["jobs", job.Id]);
            _ = LinuxFileIdentity.InspectUnderRoot(storageRoot, ["jobs", job.Id, "job.json"]);
        }
        catch (SafeFileOpenException exception)
        {
            throw new InvalidDataException("The persisted job file is missing or unsafe.", exception);
        }
    }

    private WordJob HydrateArtifactPaths(WordJob job)
    {
        if (job.Result?.Artifacts is not { Count: > 0 } artifacts)
        {
            return job;
        }

        var jobRoot = Path.GetFullPath(GetJobDirectory(job.Id));
        var searchRoot = jobRoot;
        IReadOnlyList<string> searchRootSegments = ["jobs", job.Id];
        if (job.PublishedRunId is not null)
        {
            if (!Identifier.IsValid(job.PublishedRunId, "run_"))
            {
                throw new InvalidDataException("The persisted published run identifier is invalid.");
            }

            searchRoot = Path.GetFullPath(Path.Combine(jobRoot, "runs", job.PublishedRunId));
            searchRootSegments = ["jobs", job.Id, "runs", job.PublishedRunId];
        }

        try
        {
            LinuxFileIdentity.EnsureDirectoryUnderRoot(storageRoot, searchRootSegments);
        }
        catch (SafeFileOpenException exception)
        {
            throw new InvalidDataException("The persisted artifact directory is missing or unsafe.", exception);
        }

        var hydrated = artifacts
            .Select(artifact => artifact with
            {
                Path = ResolvePersistedArtifactPath(job, artifact, searchRoot),
            })
            .ToArray();
        return job with { Result = job.Result with { Artifacts = hydrated } };
    }

    private string ResolvePersistedArtifactPath(WordJob job, ArtifactRecord artifact, string searchRoot)
    {
        if (!Identifier.IsValid(artifact.ArtifactId, "art_")
            || !IsSafeFileName(artifact.FileName)
            || artifact.Bytes <= 0)
        {
            throw new InvalidDataException("Persisted artifact metadata is invalid.");
        }

        var matches = Directory.EnumerateFiles(
                searchRoot,
                artifact.FileName,
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseSensitive,
                })
            .Take(2)
            .Select(Path.GetFullPath)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("A persisted artifact is missing or is not unique within its owning run.");
        }

        var candidate = matches[0];
        IReadOnlyList<string> segments;
        LinuxFileIdentity.Identity identity;
        try
        {
            segments = LinuxFileIdentity.RelativeSegments(storageRoot, candidate);
            var belongsToJob = segments.Count >= 3
                               && segments[0] == "jobs"
                               && segments[1] == job.Id;
            var belongsToPublishedRun = job.PublishedRunId is null
                || (segments.Count >= 5
                    && segments[2] == "runs"
                    && segments[3] == job.PublishedRunId);
            if (!belongsToJob || !belongsToPublishedRun)
            {
                throw new SafeFileOpenException("The artifact path is outside its owning job run.");
            }

            identity = LinuxFileIdentity.InspectUnderRoot(storageRoot, segments);
        }
        catch (SafeFileOpenException exception)
        {
            throw new InvalidDataException("A persisted artifact path is unsafe.", exception);
        }

        if (identity.Size != checked((ulong)artifact.Bytes))
        {
            throw new InvalidDataException("A persisted artifact size does not match its metadata.");
        }

        return candidate;
    }

    private static bool IsSafeFileName(string? fileName) =>
        fileName is { Length: >= 1 and <= 128 }
        && char.IsAsciiLetterOrDigit(fileName[0])
        && fileName.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
        && Path.GetFileName(fileName) == fileName;

    private static void DeleteValidatedRunDirectory(string runsRoot, string candidate, string runId)
    {
        if (Path.GetDirectoryName(candidate) != runsRoot
            || !Identifier.IsValid(runId, "run_")
            || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Only a validated non-linked job run directory may be removed.");
        }

        EnsureTreeHasNoReparsePoints(candidate);
        Directory.Delete(candidate, recursive: true);
    }

    private static void ValidatePublishedRun(WordJob job, string runsRoot, string publishedRunId)
    {
        if (job.State != JobState.Succeeded || job.Result?.Artifacts is not { Count: > 0 } artifacts)
        {
            throw new InvalidDataException("Only a successful job with artifacts may publish a run directory.");
        }

        var publishedDirectory = Path.GetFullPath(Path.Combine(runsRoot, publishedRunId));
        if (Path.GetDirectoryName(publishedDirectory) != runsRoot
            || !Directory.Exists(publishedDirectory)
            || (File.GetAttributes(publishedDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The persisted published run directory is missing or unsafe.");
        }

        EnsureTreeHasNoReparsePoints(publishedDirectory);
        foreach (var artifact in artifacts)
        {
            if (!Identifier.IsValid(artifact.ArtifactId, "art_")
                || !IsSafeFileName(artifact.FileName)
                || artifact.Bytes <= 0)
            {
                throw new InvalidDataException("Published artifact metadata is invalid.");
            }
        }
    }

    private static void EnsureTreeHasNoReparsePoints(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("A job run containing a reparse point cannot be removed automatically.");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static void ValidateId(string jobId)
    {
        if (!Identifier.IsValid(jobId, "job_"))
        {
            throw new WordMcpException(
                "invalid_job_id",
                "$.job_id",
                "The job identifier is invalid.",
                "Use the opaque job_id exactly as returned by word-mcp.");
        }
    }

    public void Dispose() => gate.Dispose();
}
