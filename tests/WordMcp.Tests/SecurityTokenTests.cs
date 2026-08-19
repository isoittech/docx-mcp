using WordMcp.Domain;
using WordMcp.Security;

namespace WordMcp.Tests;

public sealed class SecurityTokenTests
{
    [Fact]
    public void ScopeIdsAreKeyedDomainSeparatedAndStable()
    {
        using var environment = new TestEnvironment();
        var service = new ScopeIdService(environment.Options);
        var caller = new CallerContext("user-1", "conversation-1", "message-1");

        var first = service.Create(caller);
        var second = service.Create(caller);

        Assert.Equal(first, second);
        Assert.NotEqual(first.UserScope, first.ConversationScope);
        Assert.DoesNotContain(caller.UserId, first.UserScope, StringComparison.Ordinal);
        Assert.Equal(64, first.UserScope.Length);
        var messageScope = service.CreateMessageScope(caller);
        Assert.NotNull(messageScope);
        Assert.Equal(64, messageScope.Length);
        Assert.DoesNotContain(caller.MessageId!, messageScope, StringComparison.Ordinal);
        Assert.NotEqual(messageScope, service.CreateMessageScope(caller with { MessageId = "message-2" }));
        Assert.Null(service.CreateMessageScope(caller with { MessageId = null }));
    }

    [Fact]
    public void ArtifactTokenBindsEveryCapabilityComponentAndExpires()
    {
        using var environment = new TestEnvironment();
        var service = new ArtifactTokenService(environment.Options, environment.Time);
        var (token, _) = service.Create("job_abcdefghijklmnop", "art_abcdefghijklmnop", "report.docx", "attachment");

        Assert.True(service.Validate("job_abcdefghijklmnop", "art_abcdefghijklmnop", "report.docx", "attachment", token));
        Assert.False(service.Validate("job_changedxxxxxxxx", "art_abcdefghijklmnop", "report.docx", "attachment", token));
        Assert.False(service.Validate("job_abcdefghijklmnop", "art_changedxxxxxxxx", "report.docx", "attachment", token));
        Assert.False(service.Validate("job_abcdefghijklmnop", "art_abcdefghijklmnop", "other.docx", "attachment", token));
        Assert.False(service.Validate("job_abcdefghijklmnop", "art_abcdefghijklmnop", "report.docx", "inline", token));

        environment.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.False(service.Validate("job_abcdefghijklmnop", "art_abcdefghijklmnop", "report.docx", "attachment", token));
    }

    [Fact]
    public void CursorTokenRejectsAnotherConversation()
    {
        using var environment = new TestEnvironment();
        var service = new CursorTokenService(environment.Options);
        var first = new CallerScope("user", "conversation-a");
        var second = new CallerScope("user", "conversation-b");
        var cursor = service.Create("ana_abcdefghijklmnop", "paragraphs", 50, first);

        Assert.Equal(50, service.Parse(cursor, "ana_abcdefghijklmnop", "paragraphs", first));
        Assert.Throws<WordMcpException>(() => service.Parse(cursor, "ana_abcdefghijklmnop", "paragraphs", second));
    }
}
