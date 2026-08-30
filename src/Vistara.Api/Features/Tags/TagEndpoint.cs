using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Albums;
using Vistara.Application.Common;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Tags;
using Vistara.Contracts.Gallery;
using Vistara.Contracts.Pagination;

namespace Vistara.Api.Features.Tags;

public static class TagEndpointMapping
{
    public static IEndpointRouteBuilder MapVistaraTags(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Map(endpoints.MapGet("/api/v1/tags", ListAsync));
        Map(endpoints.MapPost("/api/v1/tags", CreateAsync));
        Map(endpoints.MapPatch("/api/v1/tags/{id:guid}", UpdateAsync));
        Map(endpoints.MapDelete("/api/v1/tags/{id:guid}", DeleteAsync));
        Map(endpoints.MapPut("/api/v1/assets/{id:guid}/tags/{tagId:guid}", AddAssetTagAsync));
        Map(endpoints.MapDelete(
            "/api/v1/assets/{id:guid}/tags/{tagId:guid}",
            RemoveAssetTagAsync));
        return endpoints;
    }

    private static void Map(RouteHandlerBuilder endpoint) =>
        endpoint.RequireAuthorization(GalleryCurationEndpointSupport.PolicyName);

    private static IGalleryCurationAuthorizationPort Authorization(HttpContext context) =>
        context.RequestServices.GetRequiredService<IGalleryCurationAuthorizationPort>();

    private static ITagApplication Application(HttpContext context) =>
        context.RequestServices.GetRequiredService<ITagApplication>();

    private static Task ListAsync(HttpContext context, CancellationToken cancellationToken) =>
        TagEndpoint.ListAsync(context, Authorization(context), Application(context), cancellationToken);

    private static Task CreateAsync(HttpContext context, CancellationToken cancellationToken) =>
        TagEndpoint.CreateAsync(
            context,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            context.RequestServices.GetRequiredService<IUuid7Generator>(),
            cancellationToken);

    private static Task UpdateAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TagEndpoint.UpdateAsync(
            context,
            id,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);

    private static Task DeleteAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TagEndpoint.DeleteAsync(
            context,
            id,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);

    private static Task AddAssetTagAsync(
        Guid id,
        Guid tagId,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TagEndpoint.SetAssetTagAsync(
            context,
            id,
            tagId,
            tagged: true,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);

    private static Task RemoveAssetTagAsync(
        Guid id,
        Guid tagId,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TagEndpoint.SetAssetTagAsync(
            context,
            id,
            tagId,
            tagged: false,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);
}

public static class TagEndpoint
{
    public static async Task ListAsync(
        HttpContext context,
        IGalleryCurationAuthorizationPort authorization,
        ITagApplication application,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ReadTags,
            null,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        int limit = CursorPageRequest.DefaultLimit;
        if (context.Request.Query.TryGetValue("limit", out var value) &&
            (!int.TryParse(value, out limit) || limit is < 1 or > CursorPageRequest.MaximumLimit))
        {
            await GalleryCurationEndpointSupport.WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "tag_query_invalid",
                "The tag query is invalid",
                cancellationToken);
            return;
        }

        CurationResult<IReadOnlyList<TagSnapshot>> result = await application.ListAsync(
            actor,
            limit,
            context.Request.Query["search"].FirstOrDefault(),
            cancellationToken);
        if (await GalleryCurationEndpointSupport.WriteFailureAsync(
                context,
                result,
                cancellationToken))
        {
            return;
        }

        var response = new CursorPage<TagResponse>(
            result.Value!.Select(GalleryCurationEndpointSupport.ToContract).ToArray());
        await GalleryCurationEndpointSupport.WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            response,
            cancellationToken);
    }

    public static async Task CreateAsync(
        HttpContext context,
        IGalleryCurationAuthorizationPort authorization,
        ITagApplication application,
        IClock clock,
        IUuid7Generator ids,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageTags,
            null,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        string? key = await GalleryCurationEndpointSupport.ReadIdempotencyKeyAsync(
            context,
            cancellationToken);
        CreateTagRequest? request =
            await GalleryCurationEndpointSupport.ReadRequestAsync<CreateTagRequest>(
                context,
                cancellationToken);
        if (key is null || request is null)
        {
            return;
        }

        CurationResult<TagSnapshot> result = await application.CreateAsync(
            actor,
            ids.NewId(),
            request.Name,
            request.Color,
            key,
            clock.UtcNow,
            cancellationToken);
        if (await GalleryCurationEndpointSupport.WriteFailureAsync(
                context,
                result,
                cancellationToken))
        {
            return;
        }

        context.Response.Headers.Location = $"/api/v1/tags/{result.Value!.Id:D}";
        await GalleryCurationEndpointSupport.WriteTagAsync(
            context,
            StatusCodes.Status201Created,
            result.Value,
            cancellationToken);
    }

    public static async Task UpdateAsync(
        HttpContext context,
        Guid tagId,
        IGalleryCurationAuthorizationPort authorization,
        ITagApplication application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageTags,
            tagId,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        long? version = await GalleryCurationEndpointSupport.ReadExpectedVersionAsync(
            context,
            cancellationToken);
        string? key = await GalleryCurationEndpointSupport.ReadIdempotencyKeyAsync(
            context,
            cancellationToken);
        using JsonDocument? document =
            await GalleryCurationEndpointSupport.ReadDocumentAsync(
                context,
                cancellationToken);
        if (version is null || key is null || document is null ||
            document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        JsonElement root = document.RootElement;
        var update = new TagUpdate(
            ReadOptional(root, "name"),
            ReadOptional(root, "color"));
        CurationResult<TagSnapshot> result = await application.UpdateAsync(
            actor,
            tagId,
            version.Value,
            update,
            key,
            clock.UtcNow,
            cancellationToken);
        if (await GalleryCurationEndpointSupport.WriteFailureAsync(
                context,
                result,
                cancellationToken))
        {
            return;
        }

        await GalleryCurationEndpointSupport.WriteTagAsync(
            context,
            StatusCodes.Status200OK,
            result.Value!,
            cancellationToken);
    }

    public static async Task DeleteAsync(
        HttpContext context,
        Guid tagId,
        IGalleryCurationAuthorizationPort authorization,
        ITagApplication application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageTags,
            tagId,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        long? version = await GalleryCurationEndpointSupport.ReadExpectedVersionAsync(
            context,
            cancellationToken);
        string? key = await GalleryCurationEndpointSupport.ReadIdempotencyKeyAsync(
            context,
            cancellationToken);
        if (version is null || key is null)
        {
            return;
        }

        CurationResult<bool> result = await application.DeleteAsync(
            actor,
            tagId,
            version.Value,
            key,
            clock.UtcNow,
            cancellationToken);
        if (await GalleryCurationEndpointSupport.WriteFailureAsync(
                context,
                result,
                cancellationToken))
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers.CacheControl = "no-store";
    }

    public static async Task SetAssetTagAsync(
        HttpContext context,
        Guid assetId,
        Guid tagId,
        bool tagged,
        IGalleryCurationAuthorizationPort authorization,
        ITagApplication application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageAssetTags,
            assetId,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        long? version = await GalleryCurationEndpointSupport.ReadExpectedVersionAsync(
            context,
            cancellationToken);
        string? key = await GalleryCurationEndpointSupport.ReadIdempotencyKeyAsync(
            context,
            cancellationToken);
        if (version is null || key is null)
        {
            return;
        }

        CurationResult<CuratedAssetSnapshot> result = await application.SetAssetTagAsync(
            actor,
            assetId,
            tagId,
            version.Value,
            tagged,
            key,
            clock.UtcNow,
            cancellationToken);
        if (await GalleryCurationEndpointSupport.WriteFailureAsync(
                context,
                result,
                cancellationToken))
        {
            return;
        }

        await GalleryCurationEndpointSupport.WriteAssetAsync(
            context,
            result.Value!,
            cancellationToken);
    }

    private static OptionalField<string> ReadOptional(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return OptionalField.Unspecified<string>();
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => OptionalField.Specified<string>(null),
            JsonValueKind.String => OptionalField.Specified(value.GetString()),
            _ => OptionalField.Specified(string.Empty),
        };
    }
}
