using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vistara.Contracts.Identity;
using Vistara.Domain.Common;

namespace Vistara.Api.Features.Account;

/// <summary>
/// Account preference document for <c>GET</c> and <c>PATCH</c>
/// <c>/api/v1/me/preferences</c>. The document is account level, so it is not
/// tenant scoped, and every mutation is guarded by the document version.
/// </summary>
public static class UserPreferencesEndpoint
{
    private const string CodePrefix = "preferences";

    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task GetAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IUserPreferencesPort preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(preferences);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ReadSelf,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        UserPreferencesView view =
            await preferences.GetAsync(actor.UserId, cancellationToken);
        await WriteAsync(context, StatusCodes.Status200OK, view, cancellationToken);
    }

    public static async Task PatchAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IUserPreferencesPort preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(preferences);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ReadSelf,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        IfMatchCondition condition = ApiConcurrency.ReadIfMatch(context.Request);
        if (!await ApiConcurrency.RequirePreconditionAsync(
                context,
                condition,
                CodePrefix,
                cancellationToken))
        {
            return;
        }

        UserPreferencesPatch patch;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
            if (!TryReadPatch(document.RootElement, out patch, out string? field))
            {
                await WriteValidationAsync(context, field!, cancellationToken);
                return;
            }
        }
        catch (JsonException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"{CodePrefix}.malformed_request",
                "The preference patch could not be parsed.",
                cancellationToken);
            return;
        }

        long expected = condition.Kind == IfMatchKind.Wildcard
            ? (await preferences.GetAsync(actor.UserId, cancellationToken)).Version
            : condition.Version;
        Result<UserPreferencesView> updated = await preferences.UpdateAsync(
            actor.UserId,
            patch,
            expected,
            cancellationToken);
        if (!updated.TryGetValue(out UserPreferencesView? view))
        {
            if (updated.Error!.Code == "preferences.version_conflict")
            {
                await ApiConcurrency.WriteStaleAsync(
                    context,
                    CodePrefix,
                    cancellationToken);
                return;
            }

            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                updated.Error,
                cancellationToken);
            return;
        }

        await WriteAsync(context, StatusCodes.Status200OK, view, cancellationToken);
    }

    internal static bool TryReadPatch(
        JsonElement root,
        out UserPreferencesPatch patch,
        out string? invalidField)
    {
        patch = new UserPreferencesPatch(
            null,
            null,
            null,
            PatchValue.Absent<string?>(),
            PatchValue.Absent<string?>());
        invalidField = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            invalidField = "body";
            return false;
        }

        string? density = null;
        bool? reducedMotion = null;
        bool? pagedMode = null;
        PatchValue<string?> locale = PatchValue.Absent<string?>();
        PatchValue<string?> timeZone = PatchValue.Absent<string?>();

        if (root.TryGetProperty("density", out JsonElement densityValue))
        {
            if (densityValue.ValueKind != JsonValueKind.String)
            {
                invalidField = "density";
                return false;
            }

            density = densityValue.GetString();
        }

        if (root.TryGetProperty("reducedMotion", out JsonElement reduced))
        {
            if (reduced.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                invalidField = "reducedMotion";
                return false;
            }

            reducedMotion = reduced.GetBoolean();
        }

        if (root.TryGetProperty("screenReaderPagedMode", out JsonElement paged))
        {
            if (paged.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                invalidField = "screenReaderPagedMode";
                return false;
            }

            pagedMode = paged.GetBoolean();
        }

        if (root.TryGetProperty("locale", out JsonElement localeValue))
        {
            if (!TryReadNullableString(localeValue, out string? value))
            {
                invalidField = "locale";
                return false;
            }

            locale = PatchValue.Of<string?>(value);
        }

        if (root.TryGetProperty("timeZone", out JsonElement zoneValue))
        {
            if (!TryReadNullableString(zoneValue, out string? value))
            {
                invalidField = "timeZone";
                return false;
            }

            timeZone = PatchValue.Of<string?>(value);
        }

        patch = new UserPreferencesPatch(
            density,
            reducedMotion,
            pagedMode,
            locale,
            timeZone);
        return true;
    }

    private static bool TryReadNullableString(JsonElement element, out string? value)
    {
        value = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            default:
                return false;
        }
    }

    private static Task DenyAsync(
        HttpContext context,
        AccountAccessStatus status,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            status == AccountAccessStatus.Unauthenticated
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden,
            status == AccountAccessStatus.Unauthenticated
                ? $"{CodePrefix}.unauthenticated"
                : $"{CodePrefix}.forbidden",
            "The current principal could not be resolved.",
            cancellationToken);

    private static Task WriteValidationAsync(
        HttpContext context,
        string field,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            $"{CodePrefix}.invalid_request",
            "The preference patch is invalid.",
            cancellationToken,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [field] = ["The value is not supported for this field."],
            });

    private static async Task WriteAsync(
        HttpContext context,
        int status,
        UserPreferencesView view,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new UserPreferencesResponse(
                view.Density,
                view.ReducedMotion,
                view.ScreenReaderPagedMode,
                view.Locale,
                view.TimeZone,
                view.Version),
            ResponseJsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ETag = ApiConcurrency.ToETag(view.Version);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }
}
