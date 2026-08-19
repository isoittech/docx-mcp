using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Middleware;

namespace WordMcp.Tests;

public sealed class RequestLimitMiddlewareTests
{
    [Fact]
    public async Task InvokeAsyncAcceptsBoundedJsonAndRewindsForEndpoint()
    {
        var reached = false;
        var middleware = CreateMiddleware(
            async context =>
            {
                reached = true;
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                Assert.Equal("{\"jsonrpc\":\"2.0\"}", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
            });
        var context = Context("{\"jsonrpc\":\"2.0\"}", contentLength: null);

        await middleware.InvokeAsync(context);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsyncRejectsChunkedBodyAboveLimitWithoutInvokingEndpoint()
    {
        var reached = false;
        var middleware = CreateMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, maxBytes: 32);
        var context = Context($"{{\"value\":\"{new string('x', 64)}\"}}", contentLength: null);

        await middleware.InvokeAsync(context);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Contains("request_body_too_large", await ResponseTextAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsyncRejectsJsonAboveDepthLimit()
    {
        var middleware = CreateMiddleware(
            _ => throw new InvalidOperationException("The endpoint must not run."),
            maxDepth: 3);
        var context = Context("{\"a\":{\"b\":{\"c\":{\"d\":1}}}}", contentLength: null);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("invalid_json", await ResponseTextAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsyncRejectsOversizedSingleString()
    {
        var middleware = CreateMiddleware(
            _ => throw new InvalidOperationException("The endpoint must not run."),
            maxStringCharacters: 8,
            maxTotalStringCharacters: 32);
        var context = Context("{\"value\":\"123456789\"}", contentLength: null);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Contains("json_string_too_long", await ResponseTextAsync(context), StringComparison.Ordinal);
    }

    private static RequestLimitMiddleware CreateMiddleware(
        RequestDelegate next,
        long maxBytes = 2_048,
        int maxDepth = 32,
        int maxStringCharacters = 128,
        int maxTotalStringCharacters = 512) =>
        new(
            next,
            Options.Create(new WordMcpOptions
            {
                MaxRequestBodyBytes = maxBytes,
                MaxJsonDepth = maxDepth,
                MaxJsonStringCharacters = maxStringCharacters,
                MaxJsonTotalStringCharacters = maxTotalStringCharacters,
            }));

    private static DefaultHttpContext Context(string json, long? contentLength)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var services = new ServiceCollection()
            .AddOptions()
            .Configure<JsonOptions>(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = contentLength;
        context.Request.Body = new MemoryStream(bytes);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ResponseTextAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
