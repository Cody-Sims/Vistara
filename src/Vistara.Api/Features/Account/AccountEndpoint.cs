using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Vistara.Auth.Cookies;
using Vistara.Contracts.Identity;
using Vistara.Domain.Common;

namespace Vistara.Api.Features.Account;

/// <summary>
/// Browser session and current-principal endpoints for <c>/api/v1/auth</c>,
/// <c>/api/v1/me</c>, and one-time first-owner provisioning.
/// </summary>
public static class AccountEndpoint
{
    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task LoginAsync(
        HttpContext context,
        IBrowserSessionPort sessions,
        CookieAuthOptions cookies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(cookies);

        LoginRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<LoginRequest>(
                context.Request.Body,
                ResponseJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "auth.malformed_request",
                "The login request body could not be parsed.",
                cancellationToken);
            return;
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.Login) ||
            string.IsNullOrEmpty(request.Password))
        {
            await WriteInvalidCredentialsAsync(context, cancellationToken);
            return;
        }

        Result<BrowserSessionResult> issued = await sessions.LoginAsync(
            new BrowserLoginCommand(
                request.Login,
                request.Password,
                request.TenantId,
                ReadSessionToken(context, cookies)),
            cancellationToken);
        if (!issued.TryGetValue(out BrowserSessionResult? session))
        {
            if (issued.Error!.Category is ErrorCategory.Unauthorized)
            {
                await WriteInvalidCredentialsAsync(context, cancellationToken);
                return;
            }

            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                issued.Error,
                cancellationToken);
            return;
        }

        context.Response.Headers.Append(HeaderNames.SetCookie, session.SetCookieHeader);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new LoginResponse(
                Map(session.User, cookies),
                session.AntiforgeryToken),
            cancellationToken);
    }

    public static async Task LogoutAsync(
        HttpContext context,
        IBrowserSessionPort sessions,
        CookieAuthOptions cookies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(cookies);

        string setCookie = await sessions.LogoutAsync(
            ReadSessionToken(context, cookies),
            cancellationToken);
        context.Response.Headers.Append(HeaderNames.SetCookie, setCookie);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    public static async Task GetCurrentUserAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IBrowserSessionPort sessions,
        CookieAuthOptions cookies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(cookies);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ReadSelf,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                access.Status == AccountAccessStatus.Unauthenticated
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status403Forbidden,
                access.Status == AccountAccessStatus.Unauthenticated
                    ? "auth.unauthenticated"
                    : "auth.forbidden",
                "The current principal could not be resolved.",
                cancellationToken);
            return;
        }

        Result<CurrentUserView> described = await sessions.DescribeAsync(
            actor.TenantId,
            actor.UserId,
            cancellationToken);
        if (!described.TryGetValue(out CurrentUserView? user))
        {
            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                described.Error!,
                cancellationToken);
            return;
        }

        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            Map(user, cookies),
            cancellationToken);
    }

    public static async Task ProvisionFirstOwnerAsync(
        HttpContext context,
        IFirstOwnerProvisioningPort provisioning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(provisioning);

        ProvisionFirstOwnerRequest? request;
        try
        {
            request = await JsonSerializer
                .DeserializeAsync<ProvisionFirstOwnerRequest>(
                    context.Request.Body,
                    ResponseJsonOptions,
                    cancellationToken);
        }
        catch (JsonException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "setup.malformed_request",
                "The provisioning request body could not be parsed.",
                cancellationToken);
            return;
        }

        if (request is null)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "setup.malformed_request",
                "The provisioning request body is required.",
                cancellationToken);
            return;
        }

        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.TenantSlug))
        {
            errors["tenantSlug"] = ["A tenant slug is required."];
        }

        if (string.IsNullOrWhiteSpace(request.TenantName))
        {
            errors["tenantName"] = ["A tenant name is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            errors["email"] = ["An owner email address is required."];
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["An owner display name is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            errors["password"] = ["An owner password is required."];
        }

        if (errors.Count > 0)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "setup.invalid_request",
                "The provisioning request is invalid.",
                cancellationToken,
                errors);
            return;
        }

        Result<ProvisionedOwnerView> provisioned = await provisioning.ProvisionAsync(
            new FirstOwnerProvisioningCommand(
                request.TenantSlug!,
                request.TenantName!,
                request.Email!,
                request.DisplayName!,
                request.Password!),
            cancellationToken);
        if (!provisioned.TryGetValue(out ProvisionedOwnerView? owner))
        {
            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                provisioned.Error!,
                cancellationToken);
            return;
        }

        context.Response.Headers.Location = "/api/v1/me";
        await WriteJsonAsync(
            context,
            StatusCodes.Status201Created,
            new ProvisionFirstOwnerResponse(
                owner.TenantId,
                owner.TenantSlug,
                owner.TenantName,
                owner.UserId,
                owner.Email,
                owner.DisplayName,
                owner.Role),
            cancellationToken);
    }

    internal static string? ReadSessionToken(
        HttpContext context,
        CookieAuthOptions cookies) =>
        context.Request.Cookies.TryGetValue(cookies.CookieName, out string? value) &&
            !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static CurrentUserResponse Map(
        CurrentUserView user,
        CookieAuthOptions cookies) =>
        new(
            user.UserId,
            user.Email,
            user.DisplayName,
            user.TenantId,
            user.Role,
            user.Tenants
                .Select(tenant => new CurrentUserTenantResponse(
                    tenant.TenantId,
                    tenant.Slug,
                    tenant.Name,
                    tenant.Role,
                    tenant.MembershipStatus))
                .ToArray(),
            cookies.AntiforgeryHeaderName);

    private static Task WriteInvalidCredentialsAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status401Unauthorized,
            "auth.invalid_credentials",
            "The login or password is incorrect.",
            cancellationToken);

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
