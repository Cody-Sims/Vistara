using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Contracts.Admin;
using Vistara.Domain.Common;
using Vistara.Persistence.Administration;

namespace Vistara.Api.Features.Admin;

/// <summary>
/// Operator screens for storage consumption, tenant policy, and the audit
/// trail. These routes live under <c>/api/v1/admin</c> because they describe
/// the tenant deployment rather than a gallery resource.
/// </summary>
public static class AdminEndpoint
{
    private const string StoragePrefix = "storage";

    private const string PolicyPrefix = "policies";

    private const string AuditPrefix = "audit";

    /// <summary>
    /// Audit outcomes and actor kinds are published in lower camel, matching
    /// the job state vocabulary, while the stored representation stays the
    /// domain enum name. The legacy spelling remains accepted on the filter.
    /// </summary>
    private static readonly Dictionary<string, string> AuditOutcomes =
        new(StringComparer.Ordinal)
        {
            ["succeeded"] = "Succeeded",
            ["rejected"] = "Rejected",
            ["failed"] = "Failed",
            ["Succeeded"] = "Succeeded",
            ["Rejected"] = "Rejected",
            ["Failed"] = "Failed",
        };

    private static readonly Dictionary<string, string> AuditActorKinds =
        new(StringComparer.Ordinal)
        {
            ["User"] = "user",
            ["ApiKey"] = "apiKey",
            ["System"] = "system",
        };

    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task GetStorageAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IAdminPort admin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(admin);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ManageQuotas,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, StoragePrefix, cancellationToken);
            return;
        }

        StorageSummaryView summary =
            await admin.GetStorageAsync(actor.TenantId, cancellationToken);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new StorageSummaryResponse(
                summary.Buckets
                    .Select(bucket => new StorageBucketResponse(
                        bucket.Id,
                        bucket.Kind,
                        bucket.Status,
                        bucket.UsedBytes,
                        bucket.QuotaBytes,
                        bucket.ObjectCount,
                        bucket.LastCheckedAt,
                        bucket.Message))
                    .ToArray(),
                summary.OriginalBytes,
                summary.DerivativeBytes,
                summary.StagingBytes,
                summary.QuotaBytes,
                summary.PendingUploadBytes),
            cancellationToken);
    }

    public static async Task GetPolicyAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IAdminPort admin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(admin);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ManageQuotas,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, PolicyPrefix, cancellationToken);
            return;
        }

        Result<TenantPolicyView> policy =
            await admin.GetPolicyAsync(actor.TenantId, cancellationToken);
        if (!policy.TryGetValue(out TenantPolicyView? view))
        {
            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                policy.Error!,
                cancellationToken);
            return;
        }

        await WritePolicyAsync(context, view, cancellationToken);
    }

    public static async Task PatchPolicyAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IAdminPort admin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(admin);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ManageQuotas,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, PolicyPrefix, cancellationToken);
            return;
        }

        IfMatchCondition condition = ApiConcurrency.ReadIfMatch(context.Request);
        if (!await ApiConcurrency.RequirePreconditionAsync(
                context,
                condition,
                PolicyPrefix,
                cancellationToken))
        {
            return;
        }

        UpdateTenantPolicyRequest? request;
        JsonElement root;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
            root = document.RootElement.Clone();
            request = root.Deserialize<UpdateTenantPolicyRequest>(ResponseJsonOptions);
        }
        catch (JsonException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"{PolicyPrefix}.malformed_request",
                "The policy patch could not be parsed.",
                cancellationToken);
            return;
        }

        if (request is null)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"{PolicyPrefix}.malformed_request",
                "The policy patch is required.",
                cancellationToken);
            return;
        }

        long expected = condition.Kind == IfMatchKind.Wildcard
            ? await CurrentVersionAsync(admin, actor.TenantId, cancellationToken)
            : condition.Version;
        Result<TenantPolicyView> updated = await admin.UpdatePolicyAsync(
            actor.TenantId,
            actor.UserId,
            new TenantPolicyPatch(
                request.Retention?.TrashRetentionDays,
                request.Retention?.PurgeGraceDays,
                request.Sharing?.PublicLinksEnabled,
                request.Sharing?.MaxLinkLifetimeDays,
                request.Sharing?.RequirePasswordForPublicLinks,
                ReadQuota(root, "storageBytes", request.Quotas?.StorageBytes),
                ReadQuota(
                    root,
                    "dailyTransformPixels",
                    request.Quotas?.DailyTransformPixels),
                ReadQuota(
                    root,
                    "concurrentUploads",
                    request.Quotas?.ConcurrentUploads)),
            expected,
            cancellationToken);
        if (!updated.TryGetValue(out TenantPolicyView? view))
        {
            if (updated.Error!.Code == "policies.version_conflict")
            {
                await ApiConcurrency.WriteStaleAsync(
                    context,
                    PolicyPrefix,
                    cancellationToken);
                return;
            }

            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                updated.Error,
                cancellationToken);
            return;
        }

        await WritePolicyAsync(context, view, cancellationToken);
    }

    public static async Task ReadAuditAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IAdminPort admin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(admin);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ReadAudit,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, AuditPrefix, cancellationToken);
            return;
        }

        IQueryCollection query = context.Request.Query;
        string? action = query["action"].FirstOrDefault();
        if (action is not null && (action.Length == 0 || action.Length > 128))
        {
            await WriteValidationAsync(context, AuditPrefix, "action", cancellationToken);
            return;
        }

        string? requestedOutcome = query["outcome"].FirstOrDefault();
        if (requestedOutcome is not null &&
            !AuditOutcomes.ContainsKey(requestedOutcome))
        {
            await WriteValidationAsync(context, AuditPrefix, "outcome", cancellationToken);
            return;
        }

        string? outcome = requestedOutcome is null
            ? null
            : AuditOutcomes[requestedOutcome];

        if (!AdminPaging.TryReadLimit(query["limit"].FirstOrDefault(), out int limit))
        {
            await WriteValidationAsync(context, AuditPrefix, "limit", cancellationToken);
            return;
        }

        string fingerprint = AdminCursor.Fingerprint("audit", action, outcome);
        DateTimeOffset? afterOccurredAt = null;
        Guid? afterId = null;
        string? rawCursor = query["cursor"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(rawCursor))
        {
            if (!AdminCursor.TryDecode(
                    rawCursor,
                    actor.TenantId,
                    fingerprint,
                    out AdminCursor cursor))
            {
                await ApiProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    $"{AuditPrefix}.cursor_mismatch",
                    "The cursor belongs to a different tenant or query.",
                    cancellationToken);
                return;
            }

            afterOccurredAt = new DateTimeOffset(cursor.Ticks, TimeSpan.Zero);
            afterId = cursor.Id;
        }

        AuditPage page = await admin.ReadAuditAsync(
            new AuditQuery(
                actor.TenantId,
                action,
                outcome,
                limit,
                afterOccurredAt,
                afterId),
            cancellationToken);
        string? nextCursor =
            page.NextOccurredAtUtc is { } next && page.NextId is { } nextId
                ? new AdminCursor(actor.TenantId, fingerprint, next.UtcTicks, nextId).Encode()
                : null;
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new AuditCollectionResponse(
                page.Items
                    .Select(item => new AuditEventResponse(
                        item.Id,
                        item.OccurredAt,
                        new AuditActorResponse(
                            DescribeActorKind(item.ActorKind),
                            item.ActorId,
                            null),
                        item.Action,
                        DescribeOutcome(item.Outcome),
                        item.ResourceType,
                        item.ResourceId))
                    .ToArray(),
                nextCursor),
            cancellationToken);
    }

    /// <summary>
    /// Distinguishes an absent quota member from an explicit null, so a patch
    /// that never mentions a quota cannot clear or zero it.
    /// </summary>
    internal static PatchValue<long?> ReadQuota(
        JsonElement root,
        string name,
        long? parsed)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("quotas", out JsonElement quotas) ||
            quotas.ValueKind != JsonValueKind.Object ||
            !quotas.TryGetProperty(name, out JsonElement member))
        {
            return PatchValue.Absent<long?>();
        }

        return member.ValueKind == JsonValueKind.Null
            ? PatchValue.Of<long?>(null)
            : PatchValue.Of(parsed);
    }

    internal static string DescribeActorKind(string stored) =>
        AuditActorKinds.TryGetValue(stored, out string? published)
            ? published
            : stored;

    internal static string DescribeOutcome(string stored) =>
        stored.Length == 0
            ? stored
            : string.Concat(char.ToLowerInvariant(stored[0]), stored[1..]);

    private static async ValueTask<long> CurrentVersionAsync(
        IAdminPort admin,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        Result<TenantPolicyView> policy =
            await admin.GetPolicyAsync(tenantId, cancellationToken);
        return policy.TryGetValue(out TenantPolicyView? view) ? view.Version : -1;
    }

    private static async Task WritePolicyAsync(
        HttpContext context,
        TenantPolicyView view,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.ETag = ApiConcurrency.ToETag(view.Version);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new TenantPolicyResponse(
                new RetentionPolicyResponse(
                    view.TrashRetentionDays,
                    view.PurgeGraceDays),
                new SharingPolicyResponse(
                    view.PublicLinksEnabled,
                    view.MaxLinkLifetimeDays,
                    view.RequirePasswordForPublicLinks),
                new QuotaPolicyResponse(
                    view.StorageBytes,
                    view.DailyTransformPixels,
                    view.ConcurrentUploads),
                view.Version),
            cancellationToken);
    }

    private static Task DenyAsync(
        HttpContext context,
        AccountAccessStatus status,
        string prefix,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            status == AccountAccessStatus.Unauthenticated
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden,
            status == AccountAccessStatus.Unauthenticated
                ? $"{prefix}.unauthenticated"
                : $"{prefix}.forbidden",
            "The caller is not permitted to administer this tenant.",
            cancellationToken);

    private static Task WriteValidationAsync(
        HttpContext context,
        string prefix,
        string field,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            $"{prefix}.invalid_query",
            "The administrative query is invalid.",
            cancellationToken,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [field] = ["The value is not supported for this parameter."],
            });

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        int status,
        T payload,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ResponseJsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }
}

public static class AdminServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraAdministration(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IAccountAuthorizationPort, ClaimsAccountAuthorizationPort>();
        services.TryAddScoped<RelationalAdminStore>();
        services.TryAddScoped<IAdminPort, PlatformAdminAdapter>();
        services.TryAddSingleton<IPlatformRateLimitHook, PermitAllPlatformRateLimitHook>();
        services.AddHttpClient(
            PlatformStorageValidationProbe.HttpClientName,
            client => client.Timeout = StorageValidationEndpoint.ProbeTimeout);
        services.TryAddScoped<
            IStorageValidationProbe,
            PlatformStorageValidationProbe>();
        services.TryAddScoped<
            IStorageValidationPort,
            PlatformStorageValidationAdapter>();
        return services;
    }
}

public static class AdminEndpointMapping
{
    public const string PolicyName = "Vistara.Admin";

    public static IEndpointRouteBuilder MapVistaraAdministration(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        Map(endpoints.MapGet(
            "/api/v1/admin/storage",
            (HttpContext context, CancellationToken cancellationToken) =>
                AdminEndpoint.GetStorageAsync(
                    context,
                    Authorization(context),
                    Admin(context),
                    cancellationToken)));
        Map(endpoints.MapGet(
            "/api/v1/admin/policies",
            (HttpContext context, CancellationToken cancellationToken) =>
                AdminEndpoint.GetPolicyAsync(
                    context,
                    Authorization(context),
                    Admin(context),
                    cancellationToken)));
        Map(endpoints.MapPatch(
            "/api/v1/admin/policies",
            (HttpContext context, CancellationToken cancellationToken) =>
                AdminEndpoint.PatchPolicyAsync(
                    context,
                    Authorization(context),
                    Admin(context),
                    cancellationToken)));
        Map(endpoints.MapPost(
            "/api/v1/admin/storage/validate",
            (HttpContext context, CancellationToken cancellationToken) =>
                StorageValidationEndpoint.ValidateAsync(
                    context,
                    Authorization(context),
                    context.RequestServices
                        .GetRequiredService<IStorageValidationPort>(),
                    context.RequestServices
                        .GetRequiredService<IPlatformRateLimitHook>(),
                    cancellationToken)));
        Map(endpoints.MapGet(
            "/api/v1/admin/audit",
            (HttpContext context, CancellationToken cancellationToken) =>
                AdminEndpoint.ReadAuditAsync(
                    context,
                    Authorization(context),
                    Admin(context),
                    cancellationToken)));
        return endpoints;
    }

    private static void Map(RouteHandlerBuilder endpoint) =>
        endpoint.RequireAuthorization(PolicyName);

    private static IAccountAuthorizationPort Authorization(HttpContext context) =>
        context.RequestServices.GetRequiredService<IAccountAuthorizationPort>();

    private static IAdminPort Admin(HttpContext context) =>
        context.RequestServices.GetRequiredService<IAdminPort>();
}
