using Microsoft.AspNetCore.Builder;

namespace Vistara.Api.OpenApi.Gallery;

public sealed record GalleryOpenApiPayload(int StatusCode, Type? SchemaType);

public sealed record GalleryOpenApiSchema(
    string SchemaId,
    Type ClrType,
    string Description);

public sealed record GalleryOpenApiOperation(
    string OperationId,
    string Method,
    string Path,
    string Summary,
    Type? QuerySchema,
    IReadOnlyList<Type> RequestSchemas,
    IReadOnlyList<GalleryOpenApiPayload> Responses,
    IReadOnlyList<int> ProblemStatusCodes,
    IReadOnlyDictionary<string, Type> RequiredHeaders,
    bool RequiresAuthentication,
    bool RequiresIfMatch,
    bool RequiresIdempotencyKey);

public static class GalleryOpenApiMetadataExtensions
{
    public static RouteHandlerBuilder WithGalleryOpenApi(
        this RouteHandlerBuilder builder,
        string operationId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GalleryOpenApiOperation operation = GalleryOpenApiCatalog.Get(operationId);
        return builder
            .WithName(operation.OperationId)
            .WithSummary(operation.Summary)
            .WithMetadata(operation);
    }
}
