using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WordMcp.Analysis;
using WordMcp.Configuration;
using WordMcp.Drafts;
using WordMcp.Jobs;
using WordMcp.Security;
using WordMcp.Storage;
using WordMcp.Tools;

namespace WordMcp.Tests;

public sealed partial class WordToolsContractTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 32,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly IServiceProvider ToolServices = new ToolSchemaServiceProvider();

    static WordToolsContractTests() =>
        SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

    private static readonly string[] ExpectedNames =
    [
        "word_get_capabilities",
        "word_analyze",
        "word_get_analysis_chunk",
        "word_render_preview",
        "word_replace_text",
        "word_apply_edits",
        "word_populate_template",
        "word_start_document",
        "word_add_sections_to_draft",
        "word_finish_document",
        "word_insert_document_sections",
        "word_refine_document_section",
        "word_get_job",
        "word_wait_for_job",
        "word_get_preview_images",
        "word_cancel_job",
    ];

    private static readonly string[] InfrastructureArgumentNames =
    [
        "caller_context",
        "jobs",
        "analyses",
        "drafts",
        "options",
        "templates",
        "cancellation_token",
    ];

    private static readonly string[] AtomicEditOperations =
    [
        "replace_block",
        "insert_before",
        "insert_after",
        "delete_block",
        "replace_cell",
        "append_table_row",
    ];

    [Fact]
    public void ExposesExactlyTheContractedToolSet()
    {
        var names = ToolMethods()
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedNames.Order(StringComparer.Ordinal), names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(nameof(WordTools.GetAnalysisChunkAsync), "analysis_id")]
    [InlineData(nameof(WordTools.GetAnalysisChunkAsync), "kind")]
    [InlineData(nameof(WordTools.ReplaceTextAsync), "analysis_id")]
    [InlineData(nameof(WordTools.ReplaceTextAsync), "replacements")]
    [InlineData(nameof(WordTools.ApplyEditsAsync), "analysis_id")]
    [InlineData(nameof(WordTools.ApplyEditsAsync), "edits")]
    [InlineData(nameof(WordTools.PopulateTemplateAsync), "fields")]
    [InlineData(nameof(WordTools.StartDocumentAsync), "definition")]
    [InlineData(nameof(WordTools.AddSectionsToDraftAsync), "draft_id")]
    [InlineData(nameof(WordTools.AddSectionsToDraftAsync), "sections")]
    [InlineData(nameof(WordTools.FinishDocumentAsync), "draft_id")]
    [InlineData(nameof(WordTools.InsertDocumentSectionsAsync), "request")]
    [InlineData(nameof(WordTools.RefineDocumentSectionAsync), "section")]
    [InlineData(nameof(WordTools.GetPreviewImagesAsync), "job_id")]
    [InlineData(nameof(WordTools.GetPreviewImagesAsync), "page_numbers")]
    [InlineData(nameof(WordTools.CancelJobAsync), "job_id")]
    public void BehaviorallyRequiredArgumentsAreRequiredInPublishedSchema(
        string methodName,
        string argumentName)
    {
        var tool = Create(methodName);
        var required = RequiredArguments(tool.ProtocolTool.InputSchema);

        Assert.Contains(argumentName, required);
    }

    [Theory]
    [InlineData(nameof(WordTools.AnalyzeAsync), "source_file_id")]
    [InlineData(nameof(WordTools.GetAnalysisChunkAsync), "cursor")]
    [InlineData(nameof(WordTools.GetAnalysisChunkAsync), "limit")]
    [InlineData(nameof(WordTools.RenderPreviewAsync), "source_file_id")]
    [InlineData(nameof(WordTools.PopulateTemplateAsync), "source_file_id")]
    [InlineData(nameof(WordTools.StartDocumentAsync), "user_requested_new_workflow")]
    [InlineData(nameof(WordTools.AddSectionsToDraftAsync), "start_section_index")]
    [InlineData(nameof(WordTools.InsertDocumentSectionsAsync), "job_id")]
    [InlineData(nameof(WordTools.RefineDocumentSectionAsync), "job_id")]
    [InlineData(nameof(WordTools.RefineDocumentSectionAsync), "user_requested_edit")]
    [InlineData(nameof(WordTools.GetJobAsync), "job_id")]
    [InlineData(nameof(WordTools.WaitForJobAsync), "job_id")]
    [InlineData(nameof(WordTools.WaitForJobAsync), "wait_seconds")]
    public void DefaultedArgumentsRemainOptionalInPublishedSchema(
        string methodName,
        string argumentName)
    {
        var tool = Create(methodName);
        var required = RequiredArguments(tool.ProtocolTool.InputSchema);
        var properties = tool.ProtocolTool.InputSchema
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(argumentName, properties);
        Assert.DoesNotContain(argumentName, required);
    }

    [Fact]
    public void PublishedTopLevelArgumentsAreSnakeCaseAndExcludeInfrastructure()
    {
        foreach (var method in ToolMethods())
        {
            var schema = Create(method).ProtocolTool.InputSchema;
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
            {
                Assert.Matches(SnakeCaseName(), property.Name);
                Assert.DoesNotContain(
                    property.Name,
                    InfrastructureArgumentNames);
            }
        }
    }

    [Fact]
    public void PublishedNestedSchemaPropertiesRemainSnakeCase()
    {
        foreach (var method in ToolMethods())
        {
            AssertSchemaPropertyNames(Create(method).ProtocolTool.InputSchema);
        }
    }

    [Theory]
    [InlineData(nameof(WordTools.GetAnalysisChunkAsync), "limit", 1, 50)]
    [InlineData(nameof(WordTools.WaitForJobAsync), "wait_seconds", 1, 50)]
    public void PublishedIntegerArgumentsExposeHardRanges(
        string methodName,
        string argumentName,
        int minimum,
        int maximum)
    {
        var schema = ArgumentSchema(methodName, argumentName);

        Assert.Equal(minimum, schema.GetProperty("minimum").GetInt32());
        Assert.Equal(maximum, schema.GetProperty("maximum").GetInt32());
    }

    [Theory]
    [InlineData(nameof(WordTools.ReplaceTextAsync), "replacements", 1, 100)]
    [InlineData(nameof(WordTools.ApplyEditsAsync), "edits", 1, 50)]
    [InlineData(nameof(WordTools.PopulateTemplateAsync), "fields", 1, 100)]
    [InlineData(nameof(WordTools.AddSectionsToDraftAsync), "sections", 1, 3)]
    [InlineData(nameof(WordTools.GetPreviewImagesAsync), "page_numbers", 1, 4)]
    public void PublishedCollectionArgumentsExposeHardItemBounds(
        string methodName,
        string argumentName,
        int minimum,
        int maximum)
    {
        var schema = ArgumentSchema(methodName, argumentName);

        Assert.Equal(minimum, schema.GetProperty("minItems").GetInt32());
        Assert.Equal(maximum, schema.GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public void PublishedNestedModelsExposeInsertionBoundsEnumsRangesAndRequiredMembers()
    {
        var insertion = ArgumentSchema(nameof(WordTools.InsertDocumentSectionsAsync), "request");
        var insertionProperties = insertion.GetProperty("properties");
        var insertionSections = insertionProperties.GetProperty("sections");
        Assert.Equal(1, insertionSections.GetProperty("minItems").GetInt32());
        Assert.Equal(3, insertionSections.GetProperty("maxItems").GetInt32());
        Assert.Equal(
            ["start", "end", "after"],
            EnumValues(insertionProperties.GetProperty("position")));
        Assert.Contains("sections", RequiredArguments(insertion));

        var section = insertionSections.GetProperty("items");
        Assert.Equal(
            ["section_key", "title", "blocks"],
            RequiredArguments(section));
        Assert.Equal(60, section.GetProperty("properties").GetProperty("blocks")
            .GetProperty("maxItems").GetInt32());

        var edit = ArgumentSchema(nameof(WordTools.ApplyEditsAsync), "edits").GetProperty("items");
        Assert.Equal(AtomicEditOperations, EnumValues(edit.GetProperty("properties").GetProperty("operation")));
        Assert.Equal(["operation", "target_id"], RequiredArguments(edit));

        var definition = ArgumentSchema(nameof(WordTools.StartDocumentAsync), "definition");
        var definitionProperties = definition.GetProperty("properties");
        var expectedSectionCount = definitionProperties.GetProperty("expected_section_count");
        Assert.Equal(1, expectedSectionCount.GetProperty("minimum").GetInt32());
        Assert.Equal(50, expectedSectionCount.GetProperty("maximum").GetInt32());
        Assert.Equal(
            ["a4", "letter"],
            EnumValues(definitionProperties.GetProperty("layout").GetProperty("properties")
                .GetProperty("page_size")));
        Assert.Equal(
            ["professional", "minimal", "report", "academic"],
            EnumValues(definitionProperties.GetProperty("theme").GetProperty("properties")
                .GetProperty("preset")));
        Assert.Contains("expected_section_count", RequiredArguments(definition));
    }

    [Fact]
    public void EveryToolReturnsAnExplicitProtocolResult()
    {
        foreach (var method in ToolMethods())
        {
            var expectedType = method.Name == nameof(WordTools.GetCapabilities)
                ? typeof(CallToolResult)
                : typeof(Task<CallToolResult>);
            Assert.Equal(expectedType, method.ReturnType);
        }
    }

    [Fact]
    public async Task InvalidWaitReturnsMcpErrorWithMatchingFiveFieldTextAndStructuredContent()
    {
        var result = await WordTools.WaitForJobAsync(
            callerContext: null!,
            jobs: null!,
            cancellationToken: CancellationToken.None,
            waitSeconds: 0);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.NotNull(result.StructuredContent);
        var structured = result.StructuredContent.Value;
        Assert.Equal(5, structured.EnumerateObject().Count());
        Assert.Equal("invalid_input", structured.GetProperty("status").GetString());
        Assert.Equal("wait_seconds_out_of_range", structured.GetProperty("code").GetString());
        Assert.Equal("$.wait_seconds", structured.GetProperty("field_path").GetString());
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("correction").GetString()));
        Assert.Equal(structured.GetRawText(), text.Text);
    }

    [Fact]
    public void ToolDescriptionsPreserveWorkflowAndVisualReviewSafetyLanguage()
    {
        var descriptions = ToolMethods()
            .Select(method => method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty)
            .ToArray();

        Assert.Contains(descriptions, value => value.Contains("word_wait_for_job", StringComparison.Ordinal));
        Assert.Contains(descriptions, value => value.Contains("result.section_keys", StringComparison.Ordinal));
        var preview = descriptions.Single(value => value.Contains("MCP image block", StringComparison.Ordinal));
        Assert.Contains("全ページ", preview, StringComparison.Ordinal);
        Assert.Contains("1〜4", preview, StringComparison.Ordinal);
        Assert.Contains("確認済み", preview, StringComparison.Ordinal);
    }

    private static McpServerTool Create(string methodName)
    {
        var method = typeof(WordTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return Create(method);
    }

    private static McpServerTool Create(MethodInfo method) =>
        McpServerTool.Create(
            method,
            target: null,
            options: new McpServerToolCreateOptions
            {
                SerializerOptions = SerializerOptions,
                Services = ToolServices,
            });

    private static IEnumerable<MethodInfo> ToolMethods() => typeof(WordTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static string[] RequiredArguments(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray()
                .Select(element => element.GetString())
                .OfType<string>()
                .ToArray()
            : [];

    private static JsonElement ArgumentSchema(string methodName, string argumentName) =>
        Create(methodName).ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty(argumentName);

    private static string[] EnumValues(JsonElement schema) => schema.GetProperty("enum")
        .EnumerateArray()
        .Select(element => element.GetString())
        .OfType<string>()
        .ToArray();

    private static void AssertSchemaPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in schema.EnumerateArray())
            {
                AssertSchemaPropertyNames(element);
            }

            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (property.NameEquals("properties"))
            {
                foreach (var modelProperty in property.Value.EnumerateObject())
                {
                    Assert.Matches(SnakeCaseName(), modelProperty.Name);
                    AssertSchemaPropertyNames(modelProperty.Value);
                }
            }
            else
            {
                AssertSchemaPropertyNames(property.Value);
            }
        }
    }

    [GeneratedRegex("\\A[a-z][a-z0-9]*(?:_[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SnakeCaseName();

    private sealed class ToolSchemaServiceProvider : IServiceProvider, IServiceProviderIsService
    {
        private static readonly HashSet<Type> ServiceTypes =
        [
            typeof(IOptions<WordMcpOptions>),
            typeof(TemplateRegistry),
            typeof(CallerContextAccessor),
            typeof(JobService),
            typeof(AnalysisQueryService),
            typeof(DraftService),
        ];

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? this : null;

        public bool IsService(Type serviceType) => ServiceTypes.Contains(serviceType);
    }
}
