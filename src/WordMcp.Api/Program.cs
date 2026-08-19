using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using WordMcp.Analysis;
using WordMcp.Artifacts;
using WordMcp.Configuration;
using WordMcp.Drafts;
using WordMcp.Jobs;
using WordMcp.Middleware;
using WordMcp.Rendering;
using WordMcp.Security;
using WordMcp.Storage;
using WordMcp.Tools;
using WordMcp.Word;

var builder = WebApplication.CreateBuilder(args);
var configuredOptions = builder.Configuration
    .GetSection(WordMcpOptions.SectionName)
    .Get<WordMcpOptions>() ?? new WordMcpOptions();
configuredOptions.Validate(requireSecrets: true);

builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = configuredOptions.MaxRequestBodyBytes;
    server.AddServerHeader = false;
});
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.MaxDepth = configuredOptions.MaxJsonDepth;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});
builder.Services.AddSingleton<IOptions<WordMcpOptions>>(Options.Create(configuredOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<CallerContextAccessor>();
builder.Services.AddSingleton<ScopeIdService>();
builder.Services.AddSingleton<ArtifactTokenService>();
builder.Services.AddSingleton<CursorTokenService>();
builder.Services.AddSingleton<RetentionPolicy>();
builder.Services.AddSingleton<ArtifactService>();

builder.Services.AddSingleton<FileJobRepository>();
builder.Services.AddSingleton<DraftRepository>();
builder.Services.AddSingleton<AnalysisRepository>();
builder.Services.AddSingleton<StorageQuotaService>();
builder.Services.AddSingleton<DocxPackageGuard>();
builder.Services.AddSingleton<InputFileResolver>();
builder.Services.AddSingleton<TemplateRegistry>();

builder.Services.AddSingleton<DocumentSpecValidator>();
builder.Services.AddSingleton<DraftService>();
builder.Services.AddSingleton<AnalysisQueryService>();
builder.Services.AddSingleton<IWordDocumentEngine, OpenXmlWordDocumentEngine>();
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<DocumentRenderer>();

builder.Services.AddSingleton<JobChannel>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService<DefaultTemplateWarmupService>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<RetentionWorker>();

var mcpJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    MaxDepth = configuredOptions.MaxJsonDepth,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
};
mcpJsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
builder.Services.AddMcpServer(options =>
    options.ServerInstructions = WordServerInstructions.Build(configuredOptions))
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly(typeof(WordTools).Assembly, mcpJsonOptions);

var app = builder.Build();
app.UseMiddleware<RequestLimitMiddleware>();
app.UseMiddleware<OriginValidationMiddleware>();
app.UseMiddleware<SharedSecretMiddleware>();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", (TemplateRegistry templates) =>
    templates.Readiness.IsReady
        ? Results.Ok(new { status = "ready" })
        : Results.Json(
            new { status = "not_ready", reason = templates.Readiness.FailureCode },
            statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapArtifactEndpoints();
app.MapMcp("/mcp");
app.Run();

public partial class Program;
