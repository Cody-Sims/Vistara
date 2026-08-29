using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Vistara.Application.Common;
using Vistara.Contracts.Errors;
using Vistara.Contracts.Idempotency;
using Vistara.Contracts.Uploads;

namespace Vistara.Api.Features.Uploads;

public static class UploadEndpoint
{
    private const int MaximumJsonRequestBytes = 32 * 1_024;
    private const int MaximumPartPlanCount = 1_000;
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinimumPlanLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumPlanLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.Ordinal)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
        };
    private static readonly HashSet<string> ActiveStates =
        new(StringComparer.Ordinal)
        {
            "pending",
            "uploadIssued",
        };
    private static readonly HashSet<string> SafeStates =
        new(StringComparer.Ordinal)
        {
            "pending",
            "uploadIssued",
            "committing",
            "commitRequested",
            "verifying",
            "promoting",
            "accepted",
            "aborting",
            "aborted",
            "expired",
            "rejected",
            "outcomeUnknown",
            "reconciling",
        };

    public static async Task CreateAsync(
        HttpContext context,
        IUploadAuthorizationPort authorization,
        IUploadApplicationPort application,
        IClock clock,
        IUuid7Generator idGenerator,
        CancellationToken cancellationToken)
    {
        UploadAccess? access = await AuthorizeAsync(
            context,
            () => authorization.AuthorizeCreateAsync(context, cancellationToken),
            cancellationToken);
        if (access is null)
        {
            return;
        }

        if (!TryReadIdempotencyKey(
                context.Request.Headers["Idempotency-Key"],
                out IdempotencyKey idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_idempotency_key",
                "A valid Idempotency-Key header is required",
                cancellationToken);
            return;
        }

        CreateUploadRequest? request = await ReadJsonAsync<CreateUploadRequest>(
            context,
            required: true,
            cancellationToken);
        if (request is null)
        {
            return;
        }

        if (!TryValidateCreateRequest(
                request,
                out ValidatedCreateRequest validated,
                out string validationCode))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                validationCode,
                "The upload declaration is invalid",
                cancellationToken);
            return;
        }

        try
        {
            UploadProviderPolicy policy = await application.GetProviderPolicyAsync(
                access.TenantId!.Value,
                cancellationToken);
            long maximumBytes = Math.Min(
                policy.MaximumUploadBytes,
                policy.Capabilities.Limits.MaxObjectBytes);
            if (validated.SizeBytes > maximumBytes)
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status413PayloadTooLarge,
                    "upload_too_large",
                    "The upload exceeds the configured size limit",
                    cancellationToken);
                return;
            }

            string strategy = SelectStrategy(policy, validated.SizeBytes);
            DateTimeOffset now = EnsureUtc(clock.UtcNow);
            Guid uploadId = idGenerator.NewId();
            EnsureUuid7(uploadId, nameof(idGenerator));
            string stagingKey = CreateStagingKey(access.TenantId.Value, uploadId);
            string requestHash = ComputeRequestHash(validated);
            UploadReserveResult reservation = await application.ReserveAsync(
                new ReserveUploadRequest(
                    access.TenantId.Value,
                    access.ActorId!.Value,
                    uploadId,
                    strategy,
                    validated.FileName,
                    validated.SizeBytes,
                    validated.ContentType,
                    validated.Sha256,
                    stagingKey,
                    requestHash,
                    idempotencyKey,
                    now.Add(UploadLifetime)),
                cancellationToken);
            if (!await HandleReserveFailureAsync(
                    context,
                    reservation,
                    cancellationToken))
            {
                return;
            }

            UploadSessionSnapshot session = reservation.Session!;
            EnsureOwnedSession(access, session);
            if (now >= session.ExpiresAtUtc || session.State == "expired")
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status410Gone,
                    "upload_expired",
                    "The upload session has expired",
                    cancellationToken);
                return;
            }

            if (!ActiveStates.Contains(session.State))
            {
                if (reservation.Status != UploadReserveStatus.Replayed)
                {
                    throw new InvalidOperationException(
                        "A newly reserved upload is not active.");
                }

                SetStatusHeaders(context, session);
                context.Response.Headers.Location =
                    $"/api/v1/uploads/{session.UploadId:D}";
                context.Response.Headers["Idempotency-Replayed"] = "true";
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status200OK,
                    ToContract(session),
                    cancellationToken);
                return;
            }

            UploadIssuance issuance =
                await application.IssueAsync(session, cancellationToken);
            EnsureOwnedSession(access, issuance.Session);
            EnsureSessionContinuity(session, issuance.Session);
            UploadPlanResponse plan = CreatePlanResponse(issuance, policy, now);
            SetStatusHeaders(context, issuance.Session);
            context.Response.Headers.Location =
                $"/api/v1/uploads/{issuance.Session.UploadId:D}";
            if (reservation.Status == UploadReserveStatus.Replayed)
            {
                context.Response.Headers["Idempotency-Replayed"] = "true";
            }

            await WriteJsonAsync(
                context,
                reservation.Status == UploadReserveStatus.Replayed
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status201Created,
                ToContract(issuance.Session, plan),
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteUnavailableAsync(context, cancellationToken);
        }
    }

    public static async Task GetStatusAsync(
        HttpContext context,
        Guid uploadId,
        IUploadAuthorizationPort authorization,
        IUploadApplicationPort application,
        CancellationToken cancellationToken)
    {
        (UploadAccess? access, UploadSessionSnapshot? session) =
            await GetAuthorizedSessionAsync(
                context,
                uploadId,
                authorization,
                application,
                cancellationToken);
        if (access is null || session is null)
        {
            return;
        }

        SetStatusHeaders(context, session);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            ToContract(session),
            cancellationToken);
    }

    public static async Task UploadContentAsync(
        HttpContext context,
        Guid uploadId,
        IUploadAuthorizationPort authorization,
        IUploadApplicationPort application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        (UploadAccess? access, UploadSessionSnapshot? session) =
            await GetAuthorizedSessionAsync(
                context,
                uploadId,
                authorization,
                application,
                cancellationToken);
        if (access is null || session is null)
        {
            return;
        }

        if (!await EnsureMutableAsync(context, session, clock, cancellationToken) ||
            !await EnsureVersionAsync(context, session, cancellationToken))
        {
            return;
        }

        if (session.Strategy != "proxy" || session.State != "uploadIssued")
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "upload_invalid_state",
                "The upload cannot accept proxy content in its current state",
                cancellationToken);
            return;
        }

        if (!string.Equals(
                NormalizeMediaType(context.Request.ContentType),
                session.DeclaredContentType,
                StringComparison.Ordinal))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "upload_content_type_mismatch",
                "The proxy content type does not match the upload declaration",
                cancellationToken);
            return;
        }

        if (context.Request.ContentLength is > 0 &&
            context.Request.ContentLength != session.ExpectedSizeBytes)
        {
            await WriteProblemAsync(
                context,
                context.Request.ContentLength > session.ExpectedSizeBytes
                    ? StatusCodes.Status413PayloadTooLarge
                    : StatusCodes.Status422UnprocessableEntity,
                context.Request.ContentLength > session.ExpectedSizeBytes
                    ? "upload_too_large"
                    : "upload_size_mismatch",
                "The proxy content length does not match the upload declaration",
                cancellationToken);
            return;
        }

        try
        {
            var bounded = new ExactLengthReadStream(
                context.Request.Body,
                session.ExpectedSizeBytes);
            UploadWriteResult result = await application.WriteProxyAsync(
                session,
                bounded,
                session.Version,
                cancellationToken);
            await bounded.EnsureCompleteAsync(cancellationToken);
            if (!await HandleWriteFailureAsync(
                    context,
                    result,
                    cancellationToken))
            {
                return;
            }

            EnsureOwnedSession(access, result.Session!);
            EnsureSessionContinuity(session, result.Session!);
            SetStatusHeaders(context, result.Session!);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers.CacheControl = "no-store";
        }
        catch (UploadBodyTooLargeException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "upload_too_large",
                "The proxy upload exceeds the declared size",
                cancellationToken);
        }
        catch (UploadBodyTooShortException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "upload_size_mismatch",
                "The proxy upload is shorter than the declared size",
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteUnavailableAsync(context, cancellationToken);
        }
    }

    public static async Task RefreshPartsAsync(
        HttpContext context,
        Guid uploadId,
        IUploadAuthorizationPort authorization,
        IUploadApplicationPort application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        (UploadAccess? access, UploadSessionSnapshot? session) =
            await GetAuthorizedSessionAsync(
                context,
                uploadId,
                authorization,
                application,
                cancellationToken);
        if (access is null || session is null)
        {
            return;
        }

        if (!await EnsureMutableAsync(context, session, clock, cancellationToken) ||
            !await EnsureVersionAsync(context, session, cancellationToken))
        {
            return;
        }

        if (session.Strategy != "multipart" || session.State != "uploadIssued")
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "upload_invalid_state",
                "Multipart plans are unavailable in the current upload state",
                cancellationToken);
            return;
        }

        RefreshUploadPartsRequest? request =
            await ReadJsonAsync<RefreshUploadPartsRequest>(
                context,
                required: true,
                cancellationToken);
        if (request?.PartNumbers is not { Count: > 0 } partNumbers ||
            partNumbers.Count > MaximumPartPlanCount ||
            partNumbers.Any(partNumber => partNumber < 1) ||
            partNumbers.Distinct().Count() != partNumbers.Count)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "upload_parts_invalid",
                "The requested multipart part numbers are invalid",
                cancellationToken);
            return;
        }

        try
        {
            UploadPartPlanResult result = await application.RefreshPartPlansAsync(
                session,
                partNumbers,
                session.Version,
                cancellationToken);
            if (!await HandlePartPlanFailureAsync(
                    context,
                    result,
                    cancellationToken))
            {
                return;
            }

            int[] returnedPartNumbers = result.Parts
                .Select(part => part.PartNumber)
                .Order()
                .ToArray();
            int[] requestedPartNumbers = partNumbers.Order().ToArray();
            if (returnedPartNumbers.Length != result.Parts.Count ||
                !returnedPartNumbers.SequenceEqual(requestedPartNumbers))
            {
                throw new InvalidOperationException(
                    "The storage provider returned an unexpected multipart plan.");
            }

            DateTimeOffset now = EnsureUtc(clock.UtcNow);
            SignedUploadPartResponse[] parts = result.Parts
                .Select(part => ToContract(part, now))
                .ToArray();
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new UploadPartPlanResponse(parts),
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteUnavailableAsync(context, cancellationToken);
        }
    }

    public static async Task CommitAsync(
        HttpContext context,
        Guid uploadId,
        IUploadAuthorizationPort authorization,
        IUploadApplicationPort application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        (UploadAccess? access, UploadSessionSnapshot? session) =
            await GetAuthorizedSessionAsync(
                context,
                uploadId,
                authorization,
                application,
                cancellationToken);
        if (access is null || session is null)
        {
            return;
        }

        if (!await EnsureMutableAsync(context, session, clock, cancellationToken) ||
            !await EnsureVersionAsync(context, session, cancellationToken))
        {
            return;
        }

        if (!TryReadIdempotencyKey(
                context.Request.Headers["Idempotency-Key"],
                out IdempotencyKey idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_idempotency_key",
                "A valid Idempotency-Key header is required",
                cancellationToken);
            return;
        }

        CommitUploadRequest? request = await ReadJsonAsync<CommitUploadRequest>(
            context,
            required: session.Strategy == "multipart",
            cancellationToken);
        if (request is null && session.Strategy == "multipart")
        {
            return;
        }

        if (!TryValidateCommittedParts(
                session,
                request?.Parts,
                out IReadOnlyList<CommittedUploadPart> parts))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "upload_parts_invalid",
                "The multipart completion list is invalid",
                cancellationToken);
            return;
        }

        try
        {
            UploadCommitResult result = await application.CommitAsync(
                session,
                parts,
                idempotencyKey,
                session.Version,
                cancellationToken);
            if (!await HandleCommitFailureAsync(
                    context,
                    result,
                    cancellationToken))
            {
                return;
            }

            UploadSessionSnapshot updated = result.Session!;
            EnsureOwnedSession(access, updated);
            EnsureSessionContinuity(session, updated);
            SetStatusHeaders(context, updated);
            context.Response.Headers.Location = $"/api/v1/uploads/{updated.UploadId:D}";
            if (result.Status == UploadCommitStatus.Replayed)
            {
                context.Response.Headers["Idempotency-Replayed"] = "true";
            }

            await WriteJsonAsync(
                context,
                result.Status == UploadCommitStatus.AlreadyAccepted
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status202Accepted,
                ToContract(updated),
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteUnavailableAsync(context, cancellationToken);
        }
    }

    public static async Task AbortAsync(
        HttpContext context,
        Guid uploadId,
        IUploadAuthorizationPort authorization,
        IUploadApplicationPort application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        (UploadAccess? access, UploadSessionSnapshot? session) =
            await GetAuthorizedSessionAsync(
                context,
                uploadId,
                authorization,
                application,
                cancellationToken);
        if (access is null || session is null)
        {
            return;
        }

        if (!await EnsureMutableAsync(
                context,
                session,
                clock,
                cancellationToken,
                allowAborted: true) ||
            !await EnsureVersionAsync(context, session, cancellationToken))
        {
            return;
        }

        try
        {
            UploadAbortResult result = await application.AbortAsync(
                session,
                session.Version,
                cancellationToken);
            if (!await HandleAbortFailureAsync(
                    context,
                    result,
                    cancellationToken))
            {
                return;
            }

            EnsureOwnedSession(access, result.Session!);
            EnsureSessionContinuity(session, result.Session!);
            SetStatusHeaders(context, result.Session!);
            if (result.Status == UploadAbortStatus.AlreadyAborted)
            {
                context.Response.Headers["Idempotency-Replayed"] = "true";
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers.CacheControl = "no-store";
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteUnavailableAsync(context, cancellationToken);
        }
    }

    private static async ValueTask<(UploadAccess? Access, UploadSessionSnapshot? Session)>
        GetAuthorizedSessionAsync(
            HttpContext context,
            Guid uploadId,
            IUploadAuthorizationPort authorization,
            IUploadApplicationPort application,
            CancellationToken cancellationToken)
    {
        UploadAccess? access = await AuthorizeAsync(
            context,
            () => authorization.AuthorizeSessionAsync(
                context,
                uploadId,
                cancellationToken),
            cancellationToken);
        if (access is null)
        {
            return (null, null);
        }

        try
        {
            UploadSessionSnapshot? session = await application.GetAsync(
                access.TenantId!.Value,
                uploadId,
                cancellationToken);
            if (session is null ||
                session.UploadId != uploadId ||
                session.TenantId != access.TenantId ||
                session.ActorId != access.ActorId)
            {
                await WriteNotFoundAsync(context, cancellationToken);
                return (null, null);
            }

            EnsureSafeSnapshot(session);
            return (access, session);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteUnavailableAsync(context, cancellationToken);
            return (null, null);
        }
    }

    private static async ValueTask<UploadAccess?> AuthorizeAsync(
        HttpContext context,
        Func<ValueTask<UploadAccess>> authorize,
        CancellationToken cancellationToken)
    {
        UploadAccess access = await authorize();
        if (access.Status == UploadAccessStatus.Authorized &&
            access.TenantId is not null &&
            access.ActorId is not null)
        {
            return access;
        }

        (int status, string code, string title) = access.Status switch
        {
            UploadAccessStatus.Unauthenticated =>
                (
                    StatusCodes.Status401Unauthorized,
                    "authentication_required",
                    "Authentication is required"
                ),
            UploadAccessStatus.Forbidden =>
                (
                    StatusCodes.Status403Forbidden,
                    "upload_forbidden",
                    "Upload access is forbidden"
                ),
            _ => (
                StatusCodes.Status404NotFound,
                "upload_not_found",
                "The requested upload was not found"
            ),
        };
        await WriteProblemAsync(context, status, code, title, cancellationToken);
        return null;
    }

    private static async ValueTask<bool> EnsureMutableAsync(
        HttpContext context,
        UploadSessionSnapshot session,
        IClock clock,
        CancellationToken cancellationToken,
        bool allowAborted = false)
    {
        DateTimeOffset now = EnsureUtc(clock.UtcNow);
        if (allowAborted && session.State == "aborted")
        {
            return true;
        }

        if (session.State == "expired" || now >= session.ExpiresAtUtc)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status410Gone,
                "upload_expired",
                "The upload session has expired",
                cancellationToken);
            return false;
        }

        if (!allowAborted && session.State == "aborted")
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "upload_invalid_state",
                "The upload is no longer active",
                cancellationToken);
            return false;
        }

        return true;
    }

    private static async ValueTask<bool> EnsureVersionAsync(
        HttpContext context,
        UploadSessionSnapshot session,
        CancellationToken cancellationToken)
    {
        StringValues values = context.Request.Headers.IfMatch;
        if (values.Count == 0)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status428PreconditionRequired,
                "if_match_required",
                "An If-Match header is required",
                cancellationToken);
            return false;
        }

        if (values.Count != 1 ||
            !string.Equals(
                values[0],
                $"\"v{session.Version.ToString(CultureInfo.InvariantCulture)}\"",
                StringComparison.Ordinal))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "upload_version_conflict",
                "The upload version does not match",
                cancellationToken);
            return false;
        }

        return true;
    }

    private static bool TryValidateCreateRequest(
        CreateUploadRequest request,
        out ValidatedCreateRequest validated,
        out string errorCode)
    {
        validated = default;
        errorCode = "upload_declaration_invalid";
        string? fileName = request.FileName?.Trim();
        if (string.IsNullOrEmpty(fileName) ||
            fileName.Length > 255 ||
            fileName.Any(char.IsControl))
        {
            return false;
        }

        string? contentType = request.ContentType?.Trim().ToLowerInvariant();
        if (contentType is null || !AllowedContentTypes.Contains(contentType))
        {
            errorCode = "upload_content_type_not_allowed";
            return false;
        }

        string? checksum = request.Sha256?.Trim().ToLowerInvariant();
        if (!IsSha256(checksum) || request.SizeBytes <= 0)
        {
            return false;
        }

        validated = new ValidatedCreateRequest(
            fileName.Normalize(),
            request.SizeBytes,
            contentType,
            checksum!);
        return true;
    }

    private static string SelectStrategy(UploadProviderPolicy policy, long sizeBytes)
    {
        if (!policy.Capabilities.SupportsDirectUpload)
        {
            return "proxy";
        }

        return policy.Capabilities.SupportsMultipartUpload &&
            sizeBytes >= policy.MultipartThresholdBytes
                ? "multipart"
                : "direct";
    }

    private static string CreateStagingKey(Guid tenantId, Guid uploadId)
    {
        string tenant = tenantId.ToString("D");
        string shard = tenantId.ToString("N")[..2];
        return $"staging/{shard}/{tenant}/{uploadId:D}";
    }

    private static string ComputeRequestHash(ValidatedCreateRequest request)
    {
        string canonical = string.Join(
            '\n',
            request.FileName,
            request.SizeBytes.ToString(CultureInfo.InvariantCulture),
            request.ContentType,
            request.Sha256);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static UploadPlanResponse CreatePlanResponse(
        UploadIssuance issuance,
        UploadProviderPolicy policy,
        DateTimeOffset now)
    {
        EnsureSafeSnapshot(issuance.Session);
        return issuance.Session.Strategy switch
        {
            "proxy" when issuance.DirectRequest is null && issuance.Parts.Count == 0 =>
                new UploadPlanResponse(
                    "proxy",
                    null,
                    $"/api/v1/uploads/{issuance.Session.UploadId:D}/content",
                    null,
                    null),
            "direct" when issuance.DirectRequest is not null &&
                issuance.Parts.Count == 0 =>
                new UploadPlanResponse(
                    "direct",
                    issuance.DirectRequest.ExpiresAtUtc,
                    null,
                    ToContract(issuance.DirectRequest, now, issuance.Session),
                    null),
            "multipart" when issuance.DirectRequest is null &&
                issuance.MaxParts > 0 &&
                issuance.MaxParts <= policy.Capabilities.Limits.MaxMultipartParts &&
                issuance.MinPartBytes >= policy.Capabilities.Limits.MinMultipartPartBytes &&
                issuance.MaxPartBytes <= policy.Capabilities.Limits.MaxMultipartPartBytes &&
                issuance.MinPartBytes <= issuance.MaxPartBytes =>
                new UploadPlanResponse(
                    "multipart",
                    issuance.Parts.Count == 0
                        ? now.Add(policy.PlanLifetime)
                        : issuance.Parts.Min(part => part.Request.ExpiresAtUtc),
                    null,
                    null,
                    new MultipartUploadPlanResponse(
                        issuance.MaxParts,
                        issuance.MinPartBytes,
                        issuance.MaxPartBytes,
                        issuance.Parts.Select(part => ToContract(part, now)).ToArray())),
            _ => throw new InvalidOperationException("The upload issuance is invalid."),
        };
    }

    private static SignedUploadPartResponse ToContract(
        UploadSignedPartRequest part,
        DateTimeOffset now)
    {
        if (part.PartNumber < 1 ||
            part.MinBytes <= 0 ||
            part.MaxBytes < part.MinBytes)
        {
            throw new InvalidOperationException("The multipart plan is invalid.");
        }

        return new SignedUploadPartResponse(
            part.PartNumber,
            ToContract(part.Request, now),
            part.MinBytes,
            part.MaxBytes,
            part.Request.ExpiresAtUtc);
    }

    private static SignedUploadRequestResponse ToContract(
        UploadSignedRequest request,
        DateTimeOffset now,
        UploadSessionSnapshot? session = null)
    {
        if (!string.Equals(request.Method, "PUT", StringComparison.Ordinal) ||
            !request.Url.IsAbsoluteUri ||
            request.Url.Scheme is not ("http" or "https") ||
            request.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            request.ExpiresAtUtc < now.Add(MinimumPlanLifetime) ||
            request.ExpiresAtUtc > now.Add(MaximumPlanLifetime))
        {
            throw new InvalidOperationException("The signed upload request is invalid.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in request.Headers)
        {
            if (!IsSafeSignedHeader(name, value) || !headers.TryAdd(name, value))
            {
                throw new InvalidOperationException("The signed upload headers are invalid.");
            }
        }

        if (session is not null)
        {
            if (headers.TryGetValue("Content-Type", out string? contentType) &&
                !string.Equals(
                    NormalizeMediaType(contentType),
                    session.DeclaredContentType,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The signed upload content type is invalid.");
            }

            if (headers.TryGetValue("Content-Length", out string? contentLength) &&
                (!long.TryParse(
                    contentLength,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long parsedLength) ||
                 parsedLength != session.ExpectedSizeBytes))
            {
                throw new InvalidOperationException(
                    "The signed upload content length is invalid.");
            }
        }

        return new SignedUploadRequestResponse(
            request.Method,
            request.Url.AbsoluteUri,
            headers);
    }

    private static bool IsSafeSignedHeader(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Any(character =>
                character > 127 ||
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) ||
            value.Any(character => char.IsControl(character) && character != '\t'))
        {
            return false;
        }

        string normalized = name.Trim().ToLowerInvariant();
        return normalized is
            "content-type" or
            "content-length" or
            "content-md5" or
            "x-amz-checksum-sha256" or
            "x-amz-content-sha256" or
            "x-ms-blob-type" or
            "x-ms-version" or
            "x-ms-blob-content-type" or
            "x-ms-content-crc64";
    }

    private static bool TryValidateCommittedParts(
        UploadSessionSnapshot session,
        IReadOnlyList<CompletedUploadPartRequest>? requested,
        out IReadOnlyList<CommittedUploadPart> parts)
    {
        parts = [];
        if (session.Strategy != "multipart")
        {
            return requested is null || requested.Count == 0;
        }

        if (requested is not { Count: > 0 })
        {
            return false;
        }

        var validated = new CommittedUploadPart[requested.Count];
        long total = 0;
        for (int index = 0; index < requested.Count; index++)
        {
            CompletedUploadPartRequest part = requested[index];
            if (part.PartNumber != index + 1 ||
                string.IsNullOrWhiteSpace(part.EntityTag) ||
                part.EntityTag.Length > 1_024 ||
                part.EntityTag.Any(char.IsControl) ||
                !IsSha256(part.Checksum) ||
                part.SizeBytes <= 0)
            {
                return false;
            }

            try
            {
                total = checked(total + part.SizeBytes);
            }
            catch (OverflowException)
            {
                return false;
            }

            validated[index] = new CommittedUploadPart(
                part.PartNumber,
                part.EntityTag.Trim(),
                part.Checksum!.ToLowerInvariant(),
                part.SizeBytes);
        }

        if (total != session.ExpectedSizeBytes)
        {
            return false;
        }

        parts = validated;
        return true;
    }

    private static async ValueTask<bool> HandleReserveFailureAsync(
        HttpContext context,
        UploadReserveResult result,
        CancellationToken cancellationToken)
    {
        switch (result.Status)
        {
            case UploadReserveStatus.Created or UploadReserveStatus.Replayed
                when result.Session is not null:
                return true;
            case UploadReserveStatus.IdempotencyConflict:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "idempotency_key_conflict",
                    "The Idempotency-Key was already used for a different request",
                    cancellationToken);
                return false;
            case UploadReserveStatus.QuotaExceeded:
                if (result.RetryAfter is { } retryAfter)
                {
                    context.Response.Headers.RetryAfter = Math.Max(
                        1,
                        (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(
                            CultureInfo.InvariantCulture);
                }

                await WriteProblemAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "upload_quota_exceeded",
                    "The upload quota is exhausted",
                    cancellationToken);
                return false;
            default:
                await WriteUnavailableAsync(context, cancellationToken);
                return false;
        }
    }

    private static async ValueTask<bool> HandleWriteFailureAsync(
        HttpContext context,
        UploadWriteResult result,
        CancellationToken cancellationToken)
    {
        (int Status, string Code, string Title)? problem = result.Status switch
        {
            UploadWriteStatus.Written when result.Session is not null => null,
            UploadWriteStatus.VersionConflict =>
                (
                    StatusCodes.Status412PreconditionFailed,
                    "upload_version_conflict",
                    "The upload version does not match"
                ),
            UploadWriteStatus.Expired =>
                (
                    StatusCodes.Status410Gone,
                    "upload_expired",
                    "The upload session has expired"
                ),
            UploadWriteStatus.TooLarge =>
                (
                    StatusCodes.Status413PayloadTooLarge,
                    "upload_too_large",
                    "The proxy upload exceeds the declared size"
                ),
            UploadWriteStatus.IntegrityMismatch =>
                (
                    StatusCodes.Status422UnprocessableEntity,
                    "upload_integrity_failed",
                    "The proxy upload did not match its declaration"
                ),
            UploadWriteStatus.InvalidState =>
                (
                    StatusCodes.Status409Conflict,
                    "upload_invalid_state",
                    "The upload cannot accept proxy content in its current state"
                ),
            _ => (
                StatusCodes.Status503ServiceUnavailable,
                "upload_service_unavailable",
                "The upload service is unavailable"
            ),
        };
        return await WriteOptionalProblemAsync(
            context,
            problem,
            cancellationToken);
    }

    private static async ValueTask<bool> HandlePartPlanFailureAsync(
        HttpContext context,
        UploadPartPlanResult result,
        CancellationToken cancellationToken)
    {
        (int Status, string Code, string Title)? problem = result.Status switch
        {
            UploadPartPlanStatus.Created => null,
            UploadPartPlanStatus.VersionConflict =>
                (
                    StatusCodes.Status412PreconditionFailed,
                    "upload_version_conflict",
                    "The upload version does not match"
                ),
            UploadPartPlanStatus.Expired =>
                (
                    StatusCodes.Status410Gone,
                    "upload_expired",
                    "The upload session has expired"
                ),
            UploadPartPlanStatus.InvalidState =>
                (
                    StatusCodes.Status409Conflict,
                    "upload_invalid_state",
                    "Multipart plans are unavailable in the current upload state"
                ),
            _ => (
                StatusCodes.Status503ServiceUnavailable,
                "upload_service_unavailable",
                "The upload service is unavailable"
            ),
        };
        return await WriteOptionalProblemAsync(
            context,
            problem,
            cancellationToken);
    }

    private static async ValueTask<bool> HandleCommitFailureAsync(
        HttpContext context,
        UploadCommitResult result,
        CancellationToken cancellationToken)
    {
        (int Status, string Code, string Title)? problem = result.Status switch
        {
            UploadCommitStatus.Queued or
            UploadCommitStatus.Replayed or
            UploadCommitStatus.AlreadyAccepted when result.Session is not null => null,
            UploadCommitStatus.IdempotencyConflict =>
                (
                    StatusCodes.Status409Conflict,
                    "idempotency_key_conflict",
                    "The Idempotency-Key was already used for a different commit"
                ),
            UploadCommitStatus.VersionConflict =>
                (
                    StatusCodes.Status412PreconditionFailed,
                    "upload_version_conflict",
                    "The upload version does not match"
                ),
            UploadCommitStatus.Expired =>
                (
                    StatusCodes.Status410Gone,
                    "upload_expired",
                    "The upload session has expired"
                ),
            UploadCommitStatus.OutcomeUnknown =>
                (
                    StatusCodes.Status409Conflict,
                    "upload_outcome_unknown",
                    "The storage completion outcome is not yet known"
                ),
            UploadCommitStatus.InvalidState =>
                (
                    StatusCodes.Status409Conflict,
                    "upload_invalid_state",
                    "The upload cannot be committed in its current state"
                ),
            _ => (
                StatusCodes.Status503ServiceUnavailable,
                "upload_service_unavailable",
                "The upload service is unavailable"
            ),
        };
        return await WriteOptionalProblemAsync(
            context,
            problem,
            cancellationToken);
    }

    private static async ValueTask<bool> HandleAbortFailureAsync(
        HttpContext context,
        UploadAbortResult result,
        CancellationToken cancellationToken)
    {
        (int Status, string Code, string Title)? problem = result.Status switch
        {
            UploadAbortStatus.Aborted or UploadAbortStatus.AlreadyAborted
                when result.Session is not null => null,
            UploadAbortStatus.VersionConflict =>
                (
                    StatusCodes.Status412PreconditionFailed,
                    "upload_version_conflict",
                    "The upload version does not match"
                ),
            UploadAbortStatus.Expired =>
                (
                    StatusCodes.Status410Gone,
                    "upload_expired",
                    "The upload session has expired"
                ),
            UploadAbortStatus.InvalidState =>
                (
                    StatusCodes.Status409Conflict,
                    "upload_invalid_state",
                    "The upload cannot be aborted in its current state"
                ),
            _ => (
                StatusCodes.Status503ServiceUnavailable,
                "upload_service_unavailable",
                "The upload service is unavailable"
            ),
        };
        return await WriteOptionalProblemAsync(
            context,
            problem,
            cancellationToken);
    }

    private static async ValueTask<bool> WriteOptionalProblemAsync(
        HttpContext context,
        (int Status, string Code, string Title)? problem,
        CancellationToken cancellationToken)
    {
        if (problem is null)
        {
            return true;
        }

        await WriteProblemAsync(
            context,
            problem.Value.Status,
            problem.Value.Code,
            problem.Value.Title,
            cancellationToken);
        return false;
    }

    private static UploadStatusResponse ToContract(
        UploadSessionSnapshot session,
        UploadPlanResponse? plan = null)
    {
        EnsureSafeSnapshot(session);
        return new UploadStatusResponse(
            session.UploadId,
            session.DisplayFileName,
            session.Strategy,
            session.State,
            session.ExpectedSizeBytes,
            session.DeclaredContentType,
            session.Sha256,
            session.ExpiresAtUtc,
            session.Version,
            session.Parts
                .OrderBy(part => part.PartNumber)
                .Select(part => new UploadPartResponse(
                    part.PartNumber,
                    part.SizeBytes,
                    IsSha256(part.Checksum) ? part.Checksum!.ToLowerInvariant() : null))
                .ToArray(),
            plan);
    }

    private static void EnsureOwnedSession(
        UploadAccess access,
        UploadSessionSnapshot session)
    {
        if (session.TenantId != access.TenantId ||
            session.ActorId != access.ActorId)
        {
            throw new InvalidOperationException(
                "The upload service returned a session outside the authorized scope.");
        }

        EnsureSafeSnapshot(session);
    }

    private static void EnsureSessionContinuity(
        UploadSessionSnapshot previous,
        UploadSessionSnapshot current)
    {
        if (current.UploadId != previous.UploadId ||
            current.TenantId != previous.TenantId ||
            current.ActorId != previous.ActorId ||
            current.Strategy != previous.Strategy ||
            current.ExpectedSizeBytes != previous.ExpectedSizeBytes ||
            current.DeclaredContentType != previous.DeclaredContentType ||
            current.Sha256 != previous.Sha256 ||
            current.DisplayFileName != previous.DisplayFileName ||
            current.StagingKey != previous.StagingKey ||
            current.ExpiresAtUtc != previous.ExpiresAtUtc ||
            current.Version < previous.Version)
        {
            throw new InvalidOperationException(
                "The upload service returned a different upload session.");
        }
    }

    private static void EnsureSafeSnapshot(UploadSessionSnapshot session)
    {
        EnsureUuid7(session.TenantId, nameof(session));
        EnsureUuid7(session.ActorId, nameof(session));
        EnsureUuid7(session.UploadId, nameof(session));
        if (session.Strategy is not ("proxy" or "direct" or "multipart") ||
            !SafeStates.Contains(session.State) ||
            session.ExpectedSizeBytes <= 0 ||
            !AllowedContentTypes.Contains(session.DeclaredContentType) ||
            !IsSha256(session.Sha256) ||
            string.IsNullOrWhiteSpace(session.DisplayFileName) ||
            session.DisplayFileName.Length > 255 ||
            session.DisplayFileName.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(session.StagingKey) ||
            session.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            session.Version <= 0)
        {
            throw new InvalidOperationException(
                "The upload service returned an invalid session.");
        }
    }

    private static string? NormalizeMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        int separator = contentType.IndexOf(';', StringComparison.Ordinal);
        return (separator < 0 ? contentType : contentType[..separator])
            .Trim()
            .ToLowerInvariant();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(Uri.IsHexDigit);

    private static bool TryReadIdempotencyKey(
        StringValues values,
        out IdempotencyKey idempotencyKey)
    {
        idempotencyKey = default;
        if (values.Count != 1)
        {
            return false;
        }

        string? value = values[0];
        if (string.IsNullOrEmpty(value) ||
            value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':')))
        {
            return false;
        }

        idempotencyKey = new IdempotencyKey(value);
        return true;
    }

    private static async ValueTask<T?> ReadJsonAsync<T>(
        HttpContext context,
        bool required,
        CancellationToken cancellationToken)
        where T : class
    {
        if ((!required && context.Request.ContentLength is null or 0) ||
            (!required && context.Request.Body == Stream.Null))
        {
            return null;
        }

        if (context.Request.ContentLength is > MaximumJsonRequestBytes ||
            context.Request.ContentType is null ||
            !context.Request.ContentType.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "upload_request_invalid",
                "The upload request is invalid",
                cancellationToken);
            return null;
        }

        try
        {
            T? request = await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                JsonOptions,
                cancellationToken);
            if (request is null)
            {
                throw new JsonException("A request body is required.");
            }

            return request;
        }
        catch (JsonException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "upload_request_invalid",
                "The upload request is invalid",
                cancellationToken);
            return null;
        }
    }

    private static void SetStatusHeaders(
        HttpContext context,
        UploadSessionSnapshot session)
    {
        context.Response.Headers.ETag =
            $"\"v{session.Version.ToString(CultureInfo.InvariantCulture)}\"";
        context.Response.Headers.CacheControl = "no-store";
    }

    private static Task WriteNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status404NotFound,
            "upload_not_found",
            "The requested upload was not found",
            cancellationToken);

    private static Task WriteUnavailableAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "upload_service_unavailable",
            "The upload service is unavailable",
            cancellationToken);

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        CancellationToken cancellationToken)
    {
        var problem = new ApiProblemDetails(
            $"https://vistara.dev/problems/{code.Replace('_', '-')}",
            title,
            status,
            new ErrorCode(code),
            traceId: context.TraceIdentifier);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            JsonOptions,
            cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        int status,
        T response,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            JsonOptions,
            cancellationToken);
    }

    private static DateTimeOffset EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The clock must return UTC.");
        }

        return value;
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new InvalidOperationException(
                $"{parameterName} must produce a UUIDv7 value.");
        }
    }

    private static bool IsDependencyFailure(Exception exception) =>
        exception is InvalidOperationException or IOException or TimeoutException;

    private readonly record struct ValidatedCreateRequest(
        string FileName,
        long SizeBytes,
        string ContentType,
        string Sha256);
}

internal sealed class UploadBodyTooLargeException : IOException;

internal sealed class UploadBodyTooShortException : IOException;

internal sealed class ExactLengthReadStream(Stream inner, long expectedLength) : Stream
{
    private long _read;
    private bool _checkedOverflow;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Only asynchronous upload reads are supported.");

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_read < expectedLength)
        {
            int permitted = (int)Math.Min(buffer.Length, expectedLength - _read);
            int read = await inner.ReadAsync(buffer[..permitted], cancellationToken);
            _read += read;
            return read;
        }

        if (_checkedOverflow)
        {
            return 0;
        }

        _checkedOverflow = true;
        byte[] probe = new byte[1];
        int overflow = await inner.ReadAsync(probe, cancellationToken);
        if (overflow != 0)
        {
            throw new UploadBodyTooLargeException();
        }

        return 0;
    }

    public async ValueTask EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        if (_read < expectedLength)
        {
            throw new UploadBodyTooShortException();
        }

        _ = await ReadAsync(Memory<byte>.Empty, cancellationToken);
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
