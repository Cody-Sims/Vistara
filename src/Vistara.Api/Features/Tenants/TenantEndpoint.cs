using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vistara.Api.Features.Account;
using Vistara.Contracts.Identity;
using Vistara.Domain.Common;
using Vistara.Domain.Tenancy;

namespace Vistara.Api.Features.Tenants;

/// <summary>
/// Tenant discovery and member administration for <c>/api/v1/tenants</c>.
/// </summary>
public static class TenantEndpoint
{
    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly string[] AssignableRoles =
    [
        nameof(TenantRole.Viewer),
        nameof(TenantRole.Member),
        nameof(TenantRole.TenantAdmin),
        nameof(TenantRole.TenantOwner),
    ];

    public static async Task ListTenantsAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        ITenantDirectoryPort directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(directory);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ReadTenants,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        IReadOnlyList<TenantMembershipView> tenants =
            await directory.ListTenantsForUserAsync(
                actor.UserId,
                actor.MayEnumerateOtherTenants ? null : actor.TenantId,
                cancellationToken);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new TenantCollectionResponse(
                tenants
                    .Select(tenant => new TenantSummaryResponse(
                        tenant.TenantId,
                        tenant.Slug,
                        tenant.Name,
                        tenant.TenantStatus,
                        tenant.Role,
                        tenant.MembershipStatus,
                        tenant.JoinedAt))
                    .ToArray()),
            cancellationToken);
    }

    public static async Task ListMembersAsync(
        HttpContext context,
        Guid tenantId,
        IAccountAuthorizationPort authorization,
        ITenantDirectoryPort directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(directory);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ReadMembers,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        if (actor.TenantId != tenantId)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        IReadOnlyList<TenantMemberView> members =
            await directory.ListMembersAsync(tenantId, cancellationToken);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new TenantMemberCollectionResponse(members.Select(Map).ToArray()),
            cancellationToken);
    }

    public static async Task InviteMemberAsync(
        HttpContext context,
        Guid tenantId,
        IAccountAuthorizationPort authorization,
        ITenantDirectoryPort directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(directory);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ManageMembers,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        if (actor.TenantId != tenantId)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        InviteTenantMemberRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<InviteTenantMemberRequest>(
                context.Request.Body,
                ResponseJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "tenants.malformed_request",
                "The membership request body could not be parsed.",
                cancellationToken);
            return;
        }

        if (request is null)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "tenants.malformed_request",
                "The membership request body is required.",
                cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            await WriteValidationAsync(
                context,
                "email",
                "An email address is required.",
                cancellationToken);
            return;
        }

        if (request.Role is null ||
            !AssignableRoles.Contains(request.Role, StringComparer.Ordinal))
        {
            await WriteValidationAsync(
                context,
                "role",
                "The role must be one of Viewer, Member, TenantAdmin, or TenantOwner.",
                cancellationToken);
            return;
        }

        if (string.Equals(request.Role, nameof(TenantRole.TenantOwner), StringComparison.Ordinal) &&
            actor.Role != TenantRole.TenantOwner)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "tenants.owner_role_requires_owner",
                "Only a tenant owner may grant the tenant owner role.",
                cancellationToken);
            return;
        }

        Result<TenantMemberView> invited = await directory.InviteMemberAsync(
            new TenantMemberInvitation(
                tenantId,
                actor.UserId,
                request.Email,
                request.Role),
            cancellationToken);
        if (!invited.TryGetValue(out TenantMemberView? member))
        {
            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                invited.Error!,
                cancellationToken);
            return;
        }

        context.Response.Headers.Location =
            $"/api/v1/tenants/{tenantId:D}/members";
        await WriteJsonAsync(
            context,
            StatusCodes.Status201Created,
            Map(member),
            cancellationToken);
    }

    public static async Task UpdateMemberAsync(
        HttpContext context,
        Guid tenantId,
        Guid memberUserId,
        IAccountAuthorizationPort authorization,
        ITenantDirectoryPort directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(directory);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ManageMembers,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        if (actor.TenantId != tenantId ||
            memberUserId == Guid.Empty ||
            memberUserId.Version != 7)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        IfMatchCondition condition = ApiConcurrency.ReadIfMatch(context.Request);
        if (!await ApiConcurrency.RequirePreconditionAsync(
                context,
                condition,
                "tenants",
                cancellationToken))
        {
            return;
        }

        UpdateTenantMemberRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<UpdateTenantMemberRequest>(
                context.Request.Body,
                ResponseJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "tenants.malformed_request",
                "The membership request body could not be parsed.",
                cancellationToken);
            return;
        }

        if (request is null)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "tenants.malformed_request",
                "The membership request body is required.",
                cancellationToken);
            return;
        }

        if (string.Equals(request.Role, nameof(TenantRole.TenantOwner), StringComparison.Ordinal) &&
            actor.Role != TenantRole.TenantOwner)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "tenants.owner_role_requires_owner",
                "Only a tenant owner may grant the tenant owner role.",
                cancellationToken);
            return;
        }

        long expected = condition.Kind == IfMatchKind.Wildcard
            ? await CurrentVersionAsync(directory, tenantId, memberUserId, cancellationToken)
            : condition.Version;
        Result<TenantMemberView> updated = await directory.UpdateMemberAsync(
            new TenantMemberUpdate(
                tenantId,
                actor.UserId,
                memberUserId,
                request.Role,
                request.Status),
            expected,
            cancellationToken);
        if (!updated.TryGetValue(out TenantMemberView? member))
        {
            if (updated.Error!.Code == "tenants.member_version_conflict")
            {
                await ApiConcurrency.WriteStaleAsync(
                    context,
                    "tenants",
                    cancellationToken);
                return;
            }

            if (updated.Error.Code == "tenants.member_not_found")
            {
                await WriteNotFoundAsync(context, cancellationToken);
                return;
            }

            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                updated.Error,
                cancellationToken);
            return;
        }

        context.Response.Headers.ETag = ApiConcurrency.ToETag(member.Version);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            Map(member),
            cancellationToken);
    }

    private static async ValueTask<long> CurrentVersionAsync(
        ITenantDirectoryPort directory,
        Guid tenantId,
        Guid memberUserId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantMemberView> members =
            await directory.ListMembersAsync(tenantId, cancellationToken);
        return members
            .Where(member => member.UserId == memberUserId)
            .Select(member => member.Version)
            .DefaultIfEmpty(-1)
            .First();
    }

    private static TenantMemberResponse Map(TenantMemberView member) =>
        new(
            member.UserId,
            member.Email,
            member.DisplayName,
            member.Role,
            member.Status,
            member.InvitedAt,
            member.JoinedAt,
            member.Version);

    private static Task DenyAsync(
        HttpContext context,
        AccountAccessStatus status,
        CancellationToken cancellationToken) =>
        status == AccountAccessStatus.Unauthenticated
            ? ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "tenants.unauthenticated",
                "Authentication is required to read tenant membership.",
                cancellationToken)
            : ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "tenants.forbidden",
                "The caller is not permitted to administer tenant membership.",
                cancellationToken);

    private static Task WriteNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status404NotFound,
            "tenants.not_found",
            "The requested tenant was not found.",
            cancellationToken);

    private static Task WriteValidationAsync(
        HttpContext context,
        string field,
        string message,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "tenants.invalid_request",
            "The membership request is invalid.",
            cancellationToken,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [field] = [message],
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
