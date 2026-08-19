namespace WordMcp.Artifacts;

public static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(
            "/artifacts/{jobId}/{artifactId}/{fileName}",
            [HttpMethods.Get, HttpMethods.Head],
            HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        ArtifactService artifacts,
        string jobId,
        string artifactId,
        string fileName,
        string? token,
        string disposition = "attachment",
        CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (string.IsNullOrEmpty(token))
        {
            return Results.NotFound();
        }

        var authorized = await artifacts.AuthorizeDownloadAsync(
            jobId,
            artifactId,
            fileName,
            disposition,
            token,
            cancellationToken).ConfigureAwait(false);
        if (authorized is null)
        {
            return Results.NotFound();
        }

        var (_, artifact) = authorized.Value;
        context.Response.Headers.ContentDisposition =
            $"{disposition}; filename=\"{artifact.FileName}\"; filename*=UTF-8''{Uri.EscapeDataString(artifact.FileName)}";

        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentType = artifact.MediaType;
            context.Response.ContentLength = artifact.Bytes;
            return Results.Empty;
        }

        if (artifact.Kind == "document")
        {
            await artifacts.MarkDocumentDownloadedAsync(jobId, artifactId, cancellationToken).ConfigureAwait(false);
        }

        return Results.File(
            artifact.Path,
            artifact.MediaType,
            enableRangeProcessing: false);
    }
}
