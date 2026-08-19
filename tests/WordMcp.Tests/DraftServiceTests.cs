using WordMcp.Domain;
using WordMcp.Drafts;
using WordMcp.Security;
using WordMcp.Storage;

namespace WordMcp.Tests;

public sealed class DraftServiceTests
{
    [Fact]
    public async Task DraftPersistsOrderedSectionsAndRejectsPrematureFinish()
    {
        using var environment = new TestEnvironment();
        var repository = new DraftRepository(environment.Options);
        var jobs = new FileJobRepository(environment.Options);
        var scopes = new ScopeIdService(environment.Options);
        var validator = new DocumentSpecValidator(environment.Options);
        var analyses = new AnalysisRepository(environment.Options);
        using var quota = new StorageQuotaService(jobs, repository, analyses, environment.Options, environment.Time);
        var service = new DraftService(repository, jobs, scopes, validator, quota, environment.Options, environment.Time);
        var caller = new CallerContext("user-a", "conversation-a", null);
        var definition = Definition(2);

        var view = await service.StartAsync(caller, definition, false, CancellationToken.None);
        var first = Section("first", "第1章");
        var updated = await service.AddSectionsAsync(caller, view.DraftId, 1, [first], CancellationToken.None);

        Assert.Equal(2, updated.NextSectionIndex);
        Assert.Equal(1, updated.RemainingSectionCount);
        await Assert.ThrowsAsync<WordMcpException>(() =>
            service.AcquireCompletedAsync(caller, view.DraftId, CancellationToken.None));

        await service.AddSectionsAsync(caller, view.DraftId, null, [Section("second", "第2章")], CancellationToken.None);
        var complete = await service.AcquireCompletedAsync(caller, view.DraftId, CancellationToken.None);
        Assert.Equal(["first", "second"], complete.Definition.Sections.Select(section => section.SectionKey));
    }

    [Fact]
    public async Task DraftIsSeparatedByConversationAndExpires()
    {
        using var environment = new TestEnvironment();
        var repository = new DraftRepository(environment.Options);
        var jobs = new FileJobRepository(environment.Options);
        var scopes = new ScopeIdService(environment.Options);
        var validator = new DocumentSpecValidator(environment.Options);
        var analyses = new AnalysisRepository(environment.Options);
        using var quota = new StorageQuotaService(jobs, repository, analyses, environment.Options, environment.Time);
        var service = new DraftService(repository, jobs, scopes, validator, quota, environment.Options, environment.Time);
        var owner = new CallerContext("user-a", "conversation-a", null);
        var other = new CallerContext("user-a", "conversation-b", null);
        var view = await service.StartAsync(owner, Definition(1), false, CancellationToken.None);

        await Assert.ThrowsAsync<WordMcpException>(() =>
            service.AddSectionsAsync(other, view.DraftId, null, [Section("first", "第1章")], CancellationToken.None));

        environment.Time.Advance(TimeSpan.FromMinutes(61));
        var exception = await Assert.ThrowsAsync<WordMcpException>(() =>
            service.AddSectionsAsync(owner, view.DraftId, null, [Section("first", "第1章")], CancellationToken.None));
        Assert.Equal("draft_expired", exception.Code);
    }

    [Fact]
    public async Task RepeatedStartInOneTrustedMessageConvergesOnTheOriginalDraftAndJob()
    {
        using var environment = new TestEnvironment();
        var repository = new DraftRepository(environment.Options);
        var jobs = new FileJobRepository(environment.Options);
        var scopes = new ScopeIdService(environment.Options);
        var validator = new DocumentSpecValidator(environment.Options);
        var analyses = new AnalysisRepository(environment.Options);
        using var quota = new StorageQuotaService(jobs, repository, analyses, environment.Options, environment.Time);
        var service = new DraftService(repository, jobs, scopes, validator, quota, environment.Options, environment.Time);
        var caller = new CallerContext("user-a", "conversation-a", "message-a");
        var definition = Definition(1);

        var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            service.StartAsync(caller, definition, true, CancellationToken.None)));
        var draftId = Assert.Single(starts.Select(view => view.DraftId).Distinct(StringComparer.Ordinal));
        Assert.Single(await repository.ListAsync(CancellationToken.None));

        await service.AddSectionsAsync(
            caller,
            draftId,
            1,
            [Section("first", "第1章")],
            CancellationToken.None);
        const string jobId = "job_AAAAAAAAAAAAAAAAAAAAAAAA";
        await service.MarkSubmittedAsync(caller, draftId, jobId, CancellationToken.None);

        var submittedReplay = await service.StartAsync(caller, Definition(2), true, CancellationToken.None);
        Assert.Equal(draftId, submittedReplay.DraftId);
        Assert.Equal(0, submittedReplay.RemainingSectionCount);
        Assert.Equal(jobId, submittedReplay.SubmittedJobId);
        Assert.Equal("word_wait_for_job", submittedReplay.NextTool);

        var laterMessage = await service.StartAsync(
            caller with { MessageId = "message-b" },
            Definition(2),
            true,
            CancellationToken.None);
        Assert.NotEqual(draftId, laterMessage.DraftId);
        Assert.Equal(2, (await repository.ListAsync(CancellationToken.None)).Count);
    }

    private static DocumentDefinition Definition(int count) => new(
        "業務報告書",
        "状況を共有する",
        "関係者",
        "週次報告",
        "ja-JP",
        count,
        "none",
        new DocumentLayoutSpec(),
        new DocumentThemeSpec(),
        new DocumentDesignSpec(),
        new HeaderFooterPolicy(),
        []);

    private static LogicalSectionSpec Section(string key, string title) => new(
        key,
        title,
        [new DocumentBlock(DocumentBlockKind.Paragraph, Text: "本文です。")]);
}
