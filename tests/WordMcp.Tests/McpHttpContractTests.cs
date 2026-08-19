using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WordMcp.Tests;

public sealed class McpHttpContractTests
{
    private const string SharedSecret = "http-contract-shared-secret-123456";
    private const string ArtifactSigningKey = "http-contract-artifact-signing-key-123456";
    private const string ScopeHmacKey = "http-contract-scope-hmac-key-123456789";

    [Fact]
    public async Task ToolsListPublishesBoundsAndInvalidCallReturnsStructuredMcpError()
    {
        using var environment = new TestEnvironment();
        var port = GetAvailableLoopbackPort();
        using var process = StartApi(environment, port);
        var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5),
        };

        try
        {
            await WaitUntilLiveAsync(client, process, timeout.Token);

            using var discovery = await SendMcpAsync(
                client,
                "server/discover",
                methodName: null,
                id: 0,
                """
                {"jsonrpc":"2.0","id":0,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"word-mcp-contract-test","version":"1.0.0"},"io.modelcontextprotocol/clientCapabilities":{}}}}
                """,
                timeout.Token);
            var discoveryResult = discovery.RootElement.GetProperty("result");
            Assert.Contains(
                "2026-07-28",
                discoveryResult.GetProperty("supportedVersions")
                    .EnumerateArray()
                    .Select(static version => version.GetString()));
            Assert.Equal("complete", discoveryResult.GetProperty("resultType").GetString());
            Assert.Equal(JsonValueKind.Object, discoveryResult.GetProperty("capabilities").ValueKind);
            var serverInfo = discoveryResult.GetProperty("_meta")
                .GetProperty("io.modelcontextprotocol/serverInfo");
            Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("version").GetString()));

            using var toolsList = await SendMcpAsync(
                client,
                "tools/list",
                methodName: null,
                id: 1,
                """
                {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
                """,
                timeout.Token);
            var tools = toolsList.RootElement.GetProperty("result").GetProperty("tools");
            AssertRange(ToolArgument(tools, "word_get_analysis_chunk", "limit"), 1, 50);
            AssertRange(ToolArgument(tools, "word_wait_for_job", "wait_seconds"), 1, 50);
            AssertItemBounds(ToolArgument(tools, "word_replace_text", "replacements"), 1, 100);
            AssertItemBounds(ToolArgument(tools, "word_apply_edits", "edits"), 1, 50);
            AssertItemBounds(ToolArgument(tools, "word_populate_template", "fields"), 1, 100);
            AssertItemBounds(ToolArgument(tools, "word_add_sections_to_draft", "sections"), 1, 3);
            AssertItemBounds(ToolArgument(tools, "word_get_preview_images", "page_numbers"), 1, 4);

            var insertion = ToolArgument(tools, "word_insert_document_sections", "request")
                .GetProperty("properties");
            AssertItemBounds(insertion.GetProperty("sections"), 1, 3);
            Assert.Equal(
                "start,end,after",
                string.Join(',', insertion.GetProperty("position").GetProperty("enum")
                    .EnumerateArray().Select(value => value.GetString())));

            var definition = ToolArgument(tools, "word_start_document", "definition")
                .GetProperty("properties");
            AssertRange(definition.GetProperty("expected_section_count"), 1, 50);
            Assert.Contains(
                "expected_section_count",
                ToolArgument(tools, "word_start_document", "definition")
                    .GetProperty("required").EnumerateArray().Select(value => value.GetString()));

            using var successfulCall = await SendMcpAsync(
                client,
                "tools/call",
                "word_start_document",
                id: 2,
                """
                {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"word_start_document","arguments":{"definition":{"title":"Contract","purpose":"Verify structured success.","audience":"Automated test","subject":null,"locale":"en-US","expected_section_count":1,"template_source":"none","layout":{},"theme":{},"design":{},"header_footer":{},"sections":[]}},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
                """,
                timeout.Token);
            AssertStructuredSuccess(successfulCall, "draft_id");

            using var invalidCall = await SendMcpAsync(
                client,
                "tools/call",
                "word_wait_for_job",
                id: 3,
                """
                {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"word_wait_for_job","arguments":{"job_id":"latest","wait_seconds":0},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
                """,
                timeout.Token);
            AssertStructuredError(
                invalidCall,
                "wait_seconds_out_of_range",
                "$.wait_seconds");

            using var commonBoundaryFailure = await SendMcpAsync(
                client,
                "tools/call",
                "word_get_analysis_chunk",
                id: 4,
                """
                {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"word_get_analysis_chunk","arguments":{"analysis_id":"missing","kind":"blocks","limit":0},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
                """,
                timeout.Token);
            AssertStructuredError(
                commonBoundaryFailure,
                "analysis_chunk_limit",
                "$.limit");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            _ = await standardOutput;
            _ = await standardError;
        }
    }

    private static void AssertStructuredSuccess(JsonDocument response, string requiredProperty)
    {
        var result = response.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var structured = result.GetProperty("structuredContent");
        Assert.True(structured.TryGetProperty(requiredProperty, out _));

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        using var textJson = JsonDocument.Parse(text);
        Assert.True(JsonElement.DeepEquals(structured, textJson.RootElement));
    }

    private static JsonElement ToolArgument(JsonElement tools, string toolName, string argumentName) =>
        tools.EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == toolName)
            .GetProperty("inputSchema")
            .GetProperty("properties")
            .GetProperty(argumentName);

    private static void AssertRange(JsonElement schema, int minimum, int maximum)
    {
        Assert.Equal(minimum, schema.GetProperty("minimum").GetInt32());
        Assert.Equal(maximum, schema.GetProperty("maximum").GetInt32());
    }

    private static void AssertItemBounds(JsonElement schema, int minimum, int maximum)
    {
        Assert.Equal(minimum, schema.GetProperty("minItems").GetInt32());
        Assert.Equal(maximum, schema.GetProperty("maxItems").GetInt32());
    }

    private static void AssertStructuredError(
        JsonDocument response,
        string expectedCode,
        string expectedFieldPath)
    {
        var result = response.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var structured = result.GetProperty("structuredContent");
        Assert.Equal(5, structured.EnumerateObject().Count());
        Assert.Equal("invalid_input", structured.GetProperty("status").GetString());
        Assert.Equal(expectedCode, structured.GetProperty("code").GetString());
        Assert.Equal(expectedFieldPath, structured.GetProperty("field_path").GetString());
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("correction").GetString()));

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        using var textJson = JsonDocument.Parse(text);
        Assert.True(JsonElement.DeepEquals(structured, textJson.RootElement));
    }

    private static Process StartApi(TestEnvironment environment, int port)
    {
        var options = environment.Options.Value;
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
            throw new InvalidOperationException("The Word MCP contract test server could not be started.");
        }

        return process;
    }

    private static async Task WaitUntilLiveAsync(
        HttpClient client,
        Process process,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("The Word MCP contract test server exited before becoming live.");
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

    private static async Task<JsonDocument> SendMcpAsync(
        HttpClient client,
        string mcpMethod,
        string? methodName,
        int id,
        string payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SharedSecret}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("Mcp-Method", mcpMethod);
        request.Headers.TryAddWithoutValidation("X-LibreChat-User-ID", "contract-user");
        request.Headers.TryAddWithoutValidation("X-LibreChat-Conversation-ID", "contract-conversation");
        if (methodName is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Name", methodName);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dataLine = body.Split('\n', StringSplitOptions.TrimEntries)
            .Single(line => line.StartsWith("data: ", StringComparison.Ordinal));
        var document = JsonDocument.Parse(dataLine["data: ".Length..]);
        Assert.Equal(id, document.RootElement.GetProperty("id").GetInt32());
        return document;
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
