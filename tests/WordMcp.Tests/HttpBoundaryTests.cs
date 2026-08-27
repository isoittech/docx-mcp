using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;
using WordMcp.Middleware;
using WordMcp.Security;

namespace WordMcp.Tests;

public sealed class HttpBoundaryTests
{
    [Fact]
    public async Task SharedSecretAllowsOnlyExactBearerAndBypassesHealth()
    {
        using var environment = new TestEnvironment();
        var calls = 0;
        var middleware = new SharedSecretMiddleware(
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            environment.Options);
        var valid = Context("/mcp");
        valid.Request.Headers.Authorization = $"Bearer {environment.Options.Value.SharedSecret}";
        var invalid = Context("/mcp");
        invalid.Request.Headers.Authorization = $"Bearer {environment.Options.Value.SharedSecret}x";
        var health = Context("/health/ready");

        await middleware.InvokeAsync(valid);
        await middleware.InvokeAsync(invalid);
        await middleware.InvokeAsync(health);

        Assert.Equal(2, calls);
        Assert.Equal(StatusCodes.Status200OK, valid.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, invalid.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, health.Response.StatusCode);
    }

    [Theory]
    [InlineData(null, "conversation", "trusted_header_missing")]
    [InlineData("{{LIBRECHAT_USER_ID}}", "conversation", "trusted_header_missing")]
    [InlineData("user\nspoofed", "conversation", "trusted_header_missing")]
    [InlineData("user", "", "trusted_header_missing")]
    public void CallerContextRejectsMissingUnexpandedAndControlHeaders(
        string? user,
        string conversation,
        string expectedCode)
    {
        var context = Context("/mcp");
        if (user is not null)
        {
            context.Request.Headers["X-LibreChat-User-ID"] = user;
        }

        context.Request.Headers["X-LibreChat-Conversation-ID"] = conversation;
        var accessor = new CallerContextAccessor(new HttpContextAccessor { HttpContext = context });

        var exception = Assert.Throws<WordMcpException>(accessor.GetRequired);

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void CallerContextRejectsAmbiguousDuplicateHeader()
    {
        var context = Context("/mcp");
        context.Request.Headers["X-LibreChat-User-ID"] = new[] { "user-a", "user-b" };
        context.Request.Headers["X-LibreChat-Conversation-ID"] = "conversation";
        var accessor = new CallerContextAccessor(new HttpContextAccessor { HttpContext = context });

        var exception = Assert.Throws<WordMcpException>(accessor.GetRequired);

        Assert.Equal("trusted_header_missing", exception.Code);
    }

    [Fact]
    public void CallerContextReadsAndDeduplicatesCurrentAttachmentFileIds()
    {
        var context = Context("/mcp");
        context.Request.Headers["X-LibreChat-User-ID"] = "user";
        context.Request.Headers["X-LibreChat-Conversation-ID"] = "conversation";
        context.Request.Headers["X-LibreChat-Attachment-File-IDs"] = "file-1,file_2,file-1";

        var caller = new CallerContextAccessor(new HttpContextAccessor { HttpContext = context }).GetRequired();

        Assert.NotNull(caller.AttachmentFileIds);
        Assert.Equal(2, caller.AttachmentFileIds.Count);
        Assert.Contains("file-1", caller.AttachmentFileIds);
        Assert.Contains("file_2", caller.AttachmentFileIds);
    }

    [Fact]
    public void CallerContextTreatsDashAsAnEmptyCurrentAttachmentScope()
    {
        var context = Context("/mcp");
        context.Request.Headers["X-LibreChat-User-ID"] = "user";
        context.Request.Headers["X-LibreChat-Conversation-ID"] = "conversation";
        context.Request.Headers["X-LibreChat-Attachment-File-IDs"] = "-";

        var caller = new CallerContextAccessor(new HttpContextAccessor { HttpContext = context }).GetRequired();

        Assert.NotNull(caller.AttachmentFileIds);
        Assert.Empty(caller.AttachmentFileIds);
    }

    [Theory]
    [InlineData("../file")]
    [InlineData("file one")]
    [InlineData(",")]
    public void CallerContextRejectsInvalidCurrentAttachmentFileIds(string value)
    {
        var context = Context("/mcp");
        context.Request.Headers["X-LibreChat-User-ID"] = "user";
        context.Request.Headers["X-LibreChat-Conversation-ID"] = "conversation";
        context.Request.Headers["X-LibreChat-Attachment-File-IDs"] = value;

        var error = Assert.Throws<WordMcpException>(
            new CallerContextAccessor(new HttpContextAccessor { HttpContext = context }).GetRequired);

        Assert.Equal("trusted_header_invalid", error.Code);
    }

    [Fact]
    public async Task OriginValidationRejectsUnknownBrowserOriginButAllowsAbsentOrigin()
    {
        var options = Options.Create(new WordMcpOptions
        {
            LocalDevelopment = false,
            AllowedOrigins = ["https://chat.example.test"],
        });
        var calls = 0;
        var middleware = new OriginValidationMiddleware(
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            options);
        var accepted = Context("/mcp");
        accepted.Request.Headers.Origin = "https://chat.example.test";
        var rejected = Context("/mcp");
        rejected.Request.Headers.Origin = "https://attacker.example";
        var noOrigin = Context("/mcp");

        await middleware.InvokeAsync(accepted);
        await middleware.InvokeAsync(rejected);
        await middleware.InvokeAsync(noOrigin);

        Assert.Equal(2, calls);
        Assert.Equal(StatusCodes.Status403Forbidden, rejected.Response.StatusCode);
    }

    [Theory]
    [InlineData("queue_full", "resource_exhausted")]
    [InlineData("analysis_not_found", "not_found")]
    [InlineData("invalid_target", "invalid_input")]
    public void ToolErrorsMapContractStatusWithoutLeakingDetails(string code, string expectedStatus)
    {
        var error = ToolErrors.From(new WordMcpException(code, "$.id", "safe", "correct"));

        Assert.Equal(expectedStatus, error.Status);
        Assert.Equal(code, error.Code);
    }

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
