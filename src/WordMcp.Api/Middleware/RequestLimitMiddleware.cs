using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;

namespace WordMcp.Middleware;

public sealed class RequestLimitMiddleware(RequestDelegate next, IOptions<WordMcpOptions> options)
{
    private readonly long maxBytes = options.Value.MaxRequestBodyBytes;
    private readonly int maxDepth = options.Value.MaxJsonDepth;
    private readonly int maxStringCharacters = options.Value.MaxJsonStringCharacters;
    private readonly int maxTotalStringCharacters = options.Value.MaxJsonTotalStringCharacters;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "request_body_too_large",
                "The MCP request body exceeds the configured limit.",
                "Split the document into staged draft calls and keep each request below 2 MiB.")
                .ConfigureAwait(false);
            return;
        }

        var sizeFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        await using var buffered = new MemoryStream(
            context.Request.ContentLength is > 0 and <= int.MaxValue
                ? (int)context.Request.ContentLength.Value
                : 0);
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(rented, context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffered.Length + read > maxBytes)
                {
                    await WriteErrorAsync(
                        context,
                        StatusCodes.Status413PayloadTooLarge,
                        "request_body_too_large",
                        "The MCP request body exceeds the configured limit.",
                        "Split the document into staged draft calls and keep each request below 2 MiB.")
                        .ConfigureAwait(false);
                    return;
                }

                await buffered.WriteAsync(rented.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        buffered.Position = 0;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                buffered,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = maxDepth,
                },
                context.RequestAborted).ConfigureAwait(false);
            ValidateStringLimits(document.RootElement);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_json",
                "The MCP request is malformed or exceeds the configured JSON depth.",
                "Send valid JSON with nesting depth 32 or less; do not embed document data as base64.")
                .ConfigureAwait(false);
            return;
        }
        catch (JsonPayloadLimitException exception)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                exception.Code,
                exception.Message,
                "Split content across staged section calls and keep individual strings within the published limits.")
                .ConfigureAwait(false);
            return;
        }

        buffered.Position = 0;
        context.Request.Body = buffered;
        context.Request.ContentLength = buffered.Length;
        await next(context).ConfigureAwait(false);
    }

    private void ValidateStringLimits(JsonElement root)
    {
        long total = 0;
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject())
                    {
                        total += property.Name.Length;
                        pending.Push(property.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray())
                    {
                        pending.Push(item);
                    }

                    break;
                case JsonValueKind.String:
                    var length = current.GetString()?.Length ?? 0;
                    if (length > maxStringCharacters)
                    {
                        throw new JsonPayloadLimitException(
                            "json_string_too_long",
                            "One MCP string exceeds the configured character limit.");
                    }

                    total += length;
                    break;
            }

            if (total > maxTotalStringCharacters)
            {
                throw new JsonPayloadLimitException(
                    "json_string_budget_exceeded",
                    "The MCP request exceeds the configured total string character budget.");
            }
        }
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        string correction)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(
            new
            {
                status = "invalid_input",
                code,
                field_path = "$",
                message,
                correction,
            },
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    private sealed class JsonPayloadLimitException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
