using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Albums;
using Vistara.Contracts.Gallery;
using Vistara.Contracts.Pagination;

namespace Vistara.Api.Features.Albums;

public static class AlbumEndpointMapping
{
    public static IEndpointRouteBuilder MapVistaraAlbums(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        Map(endpoints.MapGet("/api/v1/albums", ListAsync));
        Map(endpoints.MapPost("/api/v1/albums", CreateAsync));
        Map(endpoints.MapGet("/api/v1/albums/{id:guid}", GetAsync));
        Map(endpoints.MapPatch("/api/v1/albums/{id:guid}", UpdateAsync));
        Map(endpoints.MapDelete("/api/v1/albums/{id:guid}", DeleteAsync));
        Map(endpoints.MapPost("/api/v1/albums/{id:guid}/items", AddItemsAsync));
        Map(endpoints.MapDelete("/api/v1/albums/{id:guid}/items", RemoveItemsAsync));
        Map(endpoints.MapPatch(
            "/api/v1/albums/{id:guid}/items/order",
            ReorderItemsAsync));
        return endpoints;
    }

    private static void Map(RouteHandlerBuilder endpoint) =>
        endpoint.RequireAuthorization(GalleryCurationEndpointSupport.PolicyName);

    private static Task ListAsync(HttpContext context, CancellationToken cancellationToken) =>
        AlbumEndpoint.ListAsync(
            context,
            Authorization(context),
            Application(context),
            cancellationToken);

    private static Task CreateAsync(HttpContext context, CancellationToken cancellationToken) =>
        AlbumEndpoint.CreateAsync(
            context,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            context.RequestServices.GetRequiredService<IUuid7Generator>(),
            cancellationToken);

    private static Task GetAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        AlbumEndpoint.GetAsync(
            context,
            id,
            Authorization(context),
            Application(context),
            cancellationToken);

    private static Task UpdateAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        AlbumEndpoint.UpdateAsync(
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
        AlbumEndpoint.DeleteAsync(
            context,
            id,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);

    private static Task AddItemsAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        AlbumEndpoint.ChangeItemsAsync(
            context,
            id,
            add: true,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);

    private static Task RemoveItemsAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        AlbumEndpoint.ChangeItemsAsync(
            context,
            id,
            add: false,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);

    private static Task ReorderItemsAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        AlbumEndpoint.ReorderItemsAsync(
            context,
            id,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);

    private static IGalleryCurationAuthorizationPort Authorization(HttpContext context) =>
        context.RequestServices.GetRequiredService<IGalleryCurationAuthorizationPort>();

    private static IAlbumApplication Application(HttpContext context) =>
        context.RequestServices.GetRequiredService<IAlbumApplication>();
}

public static class AlbumEndpoint
{
    public static async Task ListAsync(
        HttpContext context,
        IGalleryCurationAuthorizationPort authorization,
        IAlbumApplication application,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ReadAlbums,
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
                "album_query_invalid",
                "The album query is invalid",
                cancellationToken);
            return;
        }

        CurationResult<IReadOnlyList<AlbumSnapshot>> result =
            await application.ListAsync(actor, limit, cancellationToken);
        if (await GalleryCurationEndpointSupport.WriteFailureAsync(
                context,
                result,
                cancellationToken))
        {
            return;
        }

        var response = new CursorPage<AlbumSummaryResponse>(
            result.Value!.Select(album =>
                GalleryCurationEndpointSupport.ToContract(album).Album).ToArray());
        await GalleryCurationEndpointSupport.WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            response,
            cancellationToken);
    }

    public static async Task CreateAsync(
        HttpContext context,
        IGalleryCurationAuthorizationPort authorization,
        IAlbumApplication application,
        IClock clock,
        IUuid7Generator ids,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.CreateAlbum,
            null,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        string? key = await GalleryCurationEndpointSupport.ReadIdempotencyKeyAsync(
            context,
            cancellationToken);
        CreateAlbumRequest? request =
            await GalleryCurationEndpointSupport.ReadRequestAsync<CreateAlbumRequest>(
                context,
                cancellationToken);
        if (key is null || request is null)
        {
            return;
        }

        CurationResult<AlbumSnapshot> result = await application.CreateAsync(
            actor,
            ids.NewId(),
            request.Name,
            request.Description,
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

        context.Response.Headers.Location = $"/api/v1/albums/{result.Value!.Id:D}";
        await GalleryCurationEndpointSupport.WriteAlbumAsync(
            context,
            StatusCodes.Status201Created,
            result.Value,
            cancellationToken);
    }

    public static async Task GetAsync(
        HttpContext context,
        Guid albumId,
        IGalleryCurationAuthorizationPort authorization,
        IAlbumApplication application,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ReadAlbums,
            albumId,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        CurationResult<AlbumSnapshot> result =
            await application.GetAsync(actor, albumId, cancellationToken);
        if (await GalleryCurationEndpointSupport.WriteFailureAsync(
                context,
                result,
                cancellationToken))
        {
            return;
        }

        await GalleryCurationEndpointSupport.WriteAlbumAsync(
            context,
            StatusCodes.Status200OK,
            result.Value!,
            cancellationToken);
    }

    public static async Task UpdateAsync(
        HttpContext context,
        Guid albumId,
        IGalleryCurationAuthorizationPort authorization,
        IAlbumApplication application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageAlbum,
            albumId,
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
        if (version is null || key is null || document is null)
        {
            return;
        }

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            await InvalidBodyAsync(context, cancellationToken);
            return;
        }

        var update = new AlbumUpdate(
            ReadOptionalString(root, "name"),
            ReadOptionalString(root, "description"),
            ReadOptionalGuid(root, "coverAssetId"));
        CurationResult<AlbumSnapshot> result = await application.UpdateAsync(
            actor,
            albumId,
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

        await GalleryCurationEndpointSupport.WriteAlbumAsync(
            context,
            StatusCodes.Status200OK,
            result.Value!,
            cancellationToken);
    }

    public static async Task DeleteAsync(
        HttpContext context,
        Guid albumId,
        IGalleryCurationAuthorizationPort authorization,
        IAlbumApplication application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageAlbum,
            albumId,
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
            albumId,
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

    public static async Task ChangeItemsAsync(
        HttpContext context,
        Guid albumId,
        bool add,
        IGalleryCurationAuthorizationPort authorization,
        IAlbumApplication application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageAlbum,
            albumId,
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
        IReadOnlyList<Vistara.Contracts.Assets.VersionedAssetReference>? items;
        if (add)
        {
            AddAlbumItemsRequest? request =
                await GalleryCurationEndpointSupport.ReadRequestAsync<AddAlbumItemsRequest>(
                    context,
                    cancellationToken);
            items = request?.Items;
        }
        else
        {
            RemoveAlbumItemsRequest? request =
                await GalleryCurationEndpointSupport.ReadRequestAsync<RemoveAlbumItemsRequest>(
                    context,
                    cancellationToken);
            items = request?.Items;
        }

        if (version is null || key is null || items is null)
        {
            return;
        }

        VersionedAssetTarget[] targets = items.Select(item =>
            new VersionedAssetTarget(item.Id, item.Version.Value)).ToArray();
        CurationResult<AlbumSnapshot> result = add
            ? await application.AddItemsAsync(
                actor,
                albumId,
                version.Value,
                targets,
                key,
                clock.UtcNow,
                cancellationToken)
            : await application.RemoveItemsAsync(
                actor,
                albumId,
                version.Value,
                targets,
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

        await GalleryCurationEndpointSupport.WriteAlbumAsync(
            context,
            StatusCodes.Status200OK,
            result.Value!,
            cancellationToken);
    }

    public static async Task ReorderItemsAsync(
        HttpContext context,
        Guid albumId,
        IGalleryCurationAuthorizationPort authorization,
        IAlbumApplication application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageAlbum,
            albumId,
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
        ReorderAlbumItemsRequest? request =
            await GalleryCurationEndpointSupport.ReadRequestAsync<ReorderAlbumItemsRequest>(
                context,
                cancellationToken);
        if (version is null || key is null || request is null)
        {
            return;
        }

        AlbumItemPosition[] order = request.Items.Select(item =>
            new AlbumItemPosition(item.AssetId, item.Position)).ToArray();
        CurationResult<AlbumSnapshot> result = await application.ReorderItemsAsync(
            actor,
            albumId,
            version.Value,
            order,
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

        await GalleryCurationEndpointSupport.WriteAlbumAsync(
            context,
            StatusCodes.Status200OK,
            result.Value!,
            cancellationToken);
    }

    private static OptionalField<string> ReadOptionalString(
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

    private static OptionalField<Guid?> ReadOptionalGuid(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return OptionalField.Unspecified<Guid?>();
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return OptionalField.Specified<Guid?>(null);
        }

        return value.ValueKind == JsonValueKind.String &&
            Guid.TryParse(value.GetString(), out Guid id)
            ? OptionalField.Specified<Guid?>(id)
            : OptionalField.Specified<Guid?>(Guid.Empty);
    }

    private static Task InvalidBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        GalleryCurationEndpointSupport.WriteProblemAsync(
            context,
            StatusCodes.Status400BadRequest,
            "request_invalid",
            "The request body is invalid",
            cancellationToken);
}
