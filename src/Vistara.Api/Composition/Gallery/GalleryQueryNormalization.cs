using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;

namespace Vistara.Api.Composition.Gallery;

internal sealed class GalleryQueryNormalizationStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(
        Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, pipeline) =>
            {
                if (IsAssetQueryPath(context.Request.Path) &&
                    context.Request.Query.TryGetValue(
                        "statuses",
                        out StringValues statuses))
                {
                    context.Request.QueryString = QueryString.Create(
                        context.Request.Query.Select(pair =>
                            new KeyValuePair<string, StringValues>(
                                pair.Key,
                                string.Equals(
                                    pair.Key,
                                    "statuses",
                                    StringComparison.Ordinal)
                                    ? NormalizeStatuses(statuses)
                                    : pair.Value)));
                }

                await pipeline(context);
            });
            next(app);
        };

    private static bool IsAssetQueryPath(PathString path) =>
        path.Equals("/api/v1/assets") ||
        path.Equals("/api/v1/timeline") ||
        path.Equals("/api/v1/search/facets");

    private static StringValues NormalizeStatuses(StringValues values) =>
        new(values.Select(value => string.Join(
                ',',
                (value ?? string.Empty)
                    .Split(
                        ',',
                        StringSplitOptions.TrimEntries |
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeStatus)))
            .ToArray());

    private static string NormalizeStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "processing" => "Processing",
            "ready" => "Ready",
            "failed" => "Failed",
            "trashed" => "Trashed",
            "purged" => "Purged",
            _ => value,
        };
}

internal static class GalleryQueryNormalizationServiceCollectionExtensions
{
    internal static IServiceCollection AddGalleryQueryNormalization(
        this IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<
                IStartupFilter,
                GalleryQueryNormalizationStartupFilter>());
        return services;
    }
}
