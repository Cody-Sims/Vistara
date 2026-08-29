using System.Reflection;
using System.Text.Json;
using Vistara.Domain.Assets;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Domain.Uploads;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Repositories;

internal static class DomainMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static TenantRow ToRow(Tenant tenant) => new()
    {
        Id = tenant.Id.Value,
        TenantId = tenant.Id.Value,
        Slug = tenant.Slug.Value,
        Name = tenant.Name,
        Status = tenant.Status.ToString(),
        CreatedAtUtc = tenant.CreatedAt,
        UpdatedAtUtc = tenant.UpdatedAt,
        Version = tenant.Version,
    };

    internal static Tenant ToDomain(TenantRow row)
    {
        Tenant tenant = Construct<Tenant>(
            [typeof(TenantId), typeof(TenantSlug), typeof(string), typeof(DateTimeOffset)],
            new TenantId(row.Id),
            Required(TenantSlug.Create(row.Slug)),
            row.Name,
            row.CreatedAtUtc);
        Set(tenant, nameof(Tenant.Status), Enum.Parse<TenantStatus>(row.Status));
        Set(tenant, nameof(Tenant.UpdatedAt), row.UpdatedAtUtc);
        Set(tenant, nameof(Tenant.Version), row.Version);
        return tenant;
    }

    internal static UserRow ToRow(User user) => new()
    {
        Id = user.Id.Value,
        NormalizedEmail = user.Email.Value,
        DisplayName = user.DisplayName,
        Status = user.Status.ToString(),
        CreatedAtUtc = user.CreatedAt,
        UpdatedAtUtc = user.UpdatedAt,
        Version = user.Version,
    };

    internal static User ToDomain(
        UserRow row,
        IReadOnlyCollection<LocalIdentityRow> localIdentities,
        IReadOnlyCollection<ExternalIdentityRow> externalIdentities)
    {
        User user = Construct<User>(
            [typeof(UserId), typeof(NormalizedEmail), typeof(string), typeof(DateTimeOffset)],
            new UserId(row.Id),
            Required(NormalizedEmail.Create(row.NormalizedEmail)),
            row.DisplayName,
            row.CreatedAtUtc);

        List<LocalIdentityLink> localLinks = GetField<List<LocalIdentityLink>>(
            user,
            "_localIdentities");
        foreach (LocalIdentityRow identity in localIdentities.OrderBy(item => item.LinkedAtUtc))
        {
            localLinks.Add(Construct<LocalIdentityLink>(
                [
                    typeof(LocalIdentityId),
                    typeof(UserId),
                    typeof(NormalizedLogin),
                    typeof(DateTimeOffset),
                ],
                new LocalIdentityId(identity.Id),
                user.Id,
                Required(NormalizedLogin.Create(identity.NormalizedLogin)),
                identity.LinkedAtUtc));
        }

        List<ExternalIdentityLink> externalLinks = GetField<List<ExternalIdentityLink>>(
            user,
            "_externalIdentities");
        foreach (ExternalIdentityRow identity in externalIdentities.OrderBy(item => item.LinkedAtUtc))
        {
            externalLinks.Add(Construct<ExternalIdentityLink>(
                [
                    typeof(ExternalIdentityId),
                    typeof(UserId),
                    typeof(ExternalIssuer),
                    typeof(string),
                    typeof(DateTimeOffset),
                ],
                new ExternalIdentityId(identity.Id),
                user.Id,
                Required(ExternalIssuer.Create(identity.Issuer)),
                identity.Subject,
                identity.LinkedAtUtc));
        }

        Set(user, nameof(User.Status), Enum.Parse<UserStatus>(row.Status));
        Set(user, nameof(User.UpdatedAt), row.UpdatedAtUtc);
        Set(user, nameof(User.Version), row.Version);
        return user;
    }

    internal static IEnumerable<LocalIdentityRow> ToLocalIdentityRows(User user) =>
        user.LocalIdentities.Select(identity => new LocalIdentityRow
        {
            Id = identity.Id.Value,
            UserId = user.Id.Value,
            NormalizedLogin = identity.Login.Value,
            LinkedAtUtc = identity.LinkedAt,
        });

    internal static IEnumerable<ExternalIdentityRow> ToExternalIdentityRows(User user) =>
        user.ExternalIdentities.Select(identity => new ExternalIdentityRow
        {
            Id = identity.Id.Value,
            UserId = user.Id.Value,
            Issuer = identity.Issuer.Value,
            Subject = identity.Subject,
            LinkedAtUtc = identity.LinkedAt,
        });

    internal static TenantMembershipRow ToRow(TenantMembership membership) => new()
    {
        TenantId = membership.TenantId.Value,
        UserId = membership.UserId.Value,
        Role = membership.Role.ToString(),
        Status = membership.Status.ToString(),
        InvitedAtUtc = membership.InvitedAt,
        JoinedAtUtc = membership.JoinedAt,
        UpdatedAtUtc = membership.UpdatedAt,
        Version = membership.Version,
    };

    internal static TenantMembership ToDomain(TenantMembershipRow row)
    {
        TenantMembership membership = Construct<TenantMembership>(
            [typeof(TenantId), typeof(UserId), typeof(TenantRole), typeof(DateTimeOffset)],
            new TenantId(row.TenantId),
            new UserId(row.UserId),
            Enum.Parse<TenantRole>(row.Role),
            row.InvitedAtUtc);
        Set(membership, nameof(TenantMembership.Status), Enum.Parse<MembershipStatus>(row.Status));
        Set(membership, nameof(TenantMembership.JoinedAt), row.JoinedAtUtc);
        Set(membership, nameof(TenantMembership.UpdatedAt), row.UpdatedAtUtc);
        Set(membership, nameof(TenantMembership.Version), row.Version);
        return membership;
    }

    internal static AuthSessionRow ToRow(AuthSession session) => new()
    {
        Id = session.Id.Value,
        UserId = session.UserId.Value,
        Digest = session.Digest.Value,
        CreatedAtUtc = session.CreatedAt,
        ExpiresAtUtc = session.ExpiresAt,
        RevokedAtUtc = session.RevokedAt,
        UpdatedAtUtc = session.UpdatedAt,
        Version = session.Version,
    };

    internal static AuthSession ToDomain(AuthSessionRow row)
    {
        AuthSession session = Construct<AuthSession>(
            [
                typeof(AuthSessionId),
                typeof(UserId),
                typeof(SessionDigest),
                typeof(DateTimeOffset),
                typeof(DateTimeOffset),
            ],
            new AuthSessionId(row.Id),
            new UserId(row.UserId),
            new SessionDigest(row.Digest),
            row.CreatedAtUtc,
            row.ExpiresAtUtc);
        Set(session, nameof(AuthSession.RevokedAt), row.RevokedAtUtc);
        Set(session, nameof(AuthSession.UpdatedAt), row.UpdatedAtUtc);
        Set(session, nameof(AuthSession.Version), row.Version);
        return session;
    }

    internal static ApiKeyRow ToRow(ApiKeyMetadata apiKey) => new()
    {
        Id = apiKey.Id.Value,
        TenantId = apiKey.TenantId.Value,
        OwnerId = apiKey.OwnerId.Value,
        Prefix = apiKey.Prefix.Value,
        Digest = apiKey.Digest.Value,
        Scopes = (int)apiKey.Scopes,
        CreatedAtUtc = apiKey.CreatedAt,
        ExpiresAtUtc = apiKey.ExpiresAt,
        RevokedAtUtc = apiKey.RevokedAt,
        LastUsedAtUtc = apiKey.LastUsedAt,
        UpdatedAtUtc = apiKey.UpdatedAt,
        Version = apiKey.Version,
    };

    internal static ApiKeyMetadata ToDomain(ApiKeyRow row)
    {
        ApiKeyMetadata apiKey = Construct<ApiKeyMetadata>(
            [
                typeof(ApiKeyId),
                typeof(TenantId),
                typeof(UserId),
                typeof(ApiKeyPrefix),
                typeof(ApiKeyDigest),
                typeof(ApiKeyScope),
                typeof(DateTimeOffset),
                typeof(DateTimeOffset?),
            ],
            new ApiKeyId(row.Id),
            new TenantId(row.TenantId),
            new UserId(row.OwnerId),
            Required(ApiKeyPrefix.Create(row.Prefix)),
            Required(ApiKeyDigest.Create(row.Digest)),
            (ApiKeyScope)row.Scopes,
            row.CreatedAtUtc,
            row.ExpiresAtUtc);
        Set(apiKey, nameof(ApiKeyMetadata.RevokedAt), row.RevokedAtUtc);
        Set(apiKey, nameof(ApiKeyMetadata.LastUsedAt), row.LastUsedAtUtc);
        Set(apiKey, nameof(ApiKeyMetadata.UpdatedAt), row.UpdatedAtUtc);
        Set(apiKey, nameof(ApiKeyMetadata.Version), row.Version);
        return apiKey;
    }

    internal static BlobRow ToRow(BlobObjectMetadata blob) => new()
    {
        Id = blob.Id,
        TenantId = blob.TenantId,
        Provider = blob.Provider,
        Container = blob.Container,
        ObjectKey = blob.ObjectKey,
        ProviderVersion = blob.ProviderVersion,
        Sha256 = blob.Sha256.Value,
        ProviderChecksum = blob.ProviderChecksum,
        SizeBytes = blob.SizeBytes,
        ContentType = blob.ContentType.Value,
        State = "Active",
        CreatedAtUtc = blob.CreatedAtUtc,
    };

    internal static BlobObjectMetadata ToDomain(BlobRow row) => new(
        row.Id,
        row.TenantId,
        row.Provider,
        row.Container,
        row.ObjectKey,
        row.ProviderVersion,
        new Sha256Checksum(row.Sha256),
        row.ProviderChecksum,
        row.SizeBytes,
        new MediaContentType(row.ContentType),
        row.CreatedAtUtc);

    internal static AssetRow ToRow(Asset asset) => new()
    {
        Id = asset.Id,
        TenantId = asset.TenantId,
        OwnerId = asset.OwnerId,
        CurrentRevisionId = asset.CurrentRevision?.Id,
        Title = asset.Title,
        Description = asset.Description,
        Status = asset.Status.ToString(),
        Visibility = asset.Visibility.ToString(),
        CreatedAtUtc = asset.CreatedAtUtc,
        UpdatedAtUtc = asset.UpdatedAtUtc,
        Version = asset.Version,
    };

    internal static AssetRevisionRow ToRow(AssetRevision revision) => new()
    {
        Id = revision.Id,
        TenantId = revision.TenantId,
        AssetId = revision.AssetId,
        RevisionNumber = revision.RevisionNumber,
        BlobId = revision.Original.Id,
        DetectedFormat = revision.Media.DetectedFormat,
        DetectedContentType = revision.Media.DetectedContentType.Value,
        Width = revision.Media.Dimensions.Width,
        Height = revision.Media.Dimensions.Height,
        FrameCount = revision.Media.FrameCount,
        SafeMetadataJson = JsonSerializer.Serialize(
            revision.Media.PrivacyMetadata.SafeProperties,
            JsonOptions),
        PrivateMetadataJson = JsonSerializer.Serialize(
            revision.Media.PrivacyMetadata.PrivateProperties,
            JsonOptions),
        CreatedAtUtc = revision.CreatedAtUtc,
    };

    internal static Asset ToDomain(
        AssetRow row,
        IEnumerable<(AssetRevisionRow Revision, BlobRow Blob)> revisions)
    {
        Asset asset = Construct<Asset>(
            [
                typeof(Guid),
                typeof(Guid),
                typeof(Guid),
                typeof(string),
                typeof(AssetVisibility),
                typeof(DateTimeOffset),
            ],
            row.Id,
            row.TenantId.Value,
            row.OwnerId,
            row.Title,
            Enum.Parse<AssetVisibility>(row.Visibility),
            row.CreatedAtUtc);
        Set(asset, nameof(Asset.Description), row.Description);
        Set(asset, nameof(Asset.Status), Enum.Parse<AssetStatus>(row.Status));
        Set(asset, nameof(Asset.UpdatedAtUtc), row.UpdatedAtUtc);
        Set(asset, nameof(Asset.Version), row.Version);

        List<AssetRevision> domainRevisions = GetField<List<AssetRevision>>(asset, "_revisions");
        foreach ((AssetRevisionRow revision, BlobRow blob) in revisions
                     .OrderBy(item => item.Revision.RevisionNumber))
        {
            var safe = JsonSerializer.Deserialize<Dictionary<string, string>>(
                revision.SafeMetadataJson,
                JsonOptions) ?? [];
            var privateProperties = JsonSerializer.Deserialize<Dictionary<string, string>>(
                revision.PrivateMetadataJson,
                JsonOptions) ?? [];
            domainRevisions.Add(new AssetRevision(
                revision.Id,
                revision.TenantId,
                revision.AssetId,
                revision.RevisionNumber,
                ToDomain(blob),
                new MediaDescriptor(
                    revision.DetectedFormat,
                    new MediaContentType(revision.DetectedContentType),
                    new PixelDimensions(revision.Width, revision.Height),
                    revision.FrameCount,
                    new MediaPrivacyMetadata(safe, privateProperties)),
                revision.CreatedAtUtc));
        }

        return asset;
    }

    internal static UploadSessionRow ToRow(UploadSession session) => new()
    {
        Id = session.Id,
        TenantId = session.TenantId,
        ActorId = session.ActorId,
        DisplayFileName = "upload",
        Strategy = session.Strategy.ToString(),
        StagingKey = session.StagingKey,
        ProviderUploadId = session.ProviderUploadId,
        ExpectedBytes = session.Integrity.ExpectedSizeBytes,
        ExpectedSha256 = session.Integrity.ExpectedSha256.Value,
        DeclaredContentType = session.Integrity.DeclaredContentType.Value,
        State = session.State.ToString(),
        LastKnownState = GetField<UploadState?>(session, "_lastKnownState")?.ToString(),
        ExpiresAtUtc = session.ExpiresAtUtc,
        CreatedAtUtc = session.CreatedAtUtc,
        UpdatedAtUtc = session.UpdatedAtUtc,
        Version = session.Version,
    };

    internal static QuotaReservationRow ToReservationRow(
        Guid uploadSessionId,
        Guid tenantId,
        UploadReservationMetadata reservation) =>
        new()
        {
            Id = reservation.Id,
            TenantId = tenantId,
            UploadSessionId = uploadSessionId,
            IdempotencyKey = uploadSessionId.ToString("D"),
            RequestFingerprint = uploadSessionId.ToString("N").PadRight(64, '0'),
            ReservedUploads = 1,
            ReservedBytes = reservation.ReservedBytes,
            ReservedObjects = reservation.ReservedObjects,
            ReservedComputeUnits = reservation.ReservedComputeUnits,
            State = reservation.State.ToString(),
            CreatedAtUtc = reservation.ExpiresAtUtc.AddHours(-1),
            ExpiresAtUtc = reservation.ExpiresAtUtc,
            UpdatedAtUtc = reservation.ExpiresAtUtc.AddHours(-1),
            Version = reservation.State == UploadReservationState.Reserved ? 1 : 2,
        };

    internal static IdempotencyRequestRow ToIdempotencyRow(UploadSession session) => new()
    {
        TenantId = session.TenantId,
        PrincipalId = session.ActorId,
        Key = session.Idempotency.Key,
        RequestHash = session.Idempotency.RequestHash.Value,
        UploadSessionId = session.Id,
        ResponseReference = session.Id.ToString(),
        ExpiresAtUtc = session.Idempotency.ExpiresAtUtc,
    };

    internal static IEnumerable<UploadPartRow> ToPartRows(UploadSession session) =>
        session.Parts.Select(part => new UploadPartRow
        {
            TenantId = session.TenantId,
            UploadSessionId = session.Id,
            PartNumber = part.PartNumber,
            EntityTag = part.EntityTag,
            Checksum = part.Checksum,
            SizeBytes = part.SizeBytes,
        });

    internal static UploadSession ToDomain(
        UploadSessionRow row,
        QuotaReservationRow reservationRow,
        IdempotencyRequestRow idempotencyRow,
        IEnumerable<UploadPartRow> parts)
    {
        UploadReservationMetadata reservation = Construct<UploadReservationMetadata>(
            [
                typeof(Guid),
                typeof(long),
                typeof(int),
                typeof(long),
                typeof(DateTimeOffset),
                typeof(UploadReservationState),
            ],
            reservationRow.Id,
            reservationRow.ReservedBytes,
            checked((int)reservationRow.ReservedObjects),
            reservationRow.ReservedComputeUnits,
            reservationRow.ExpiresAtUtc,
            Enum.Parse<UploadReservationState>(reservationRow.State));
        var intent = new UploadIntent(
            row.TenantId,
            row.ActorId,
            Enum.Parse<UploadStrategy>(row.Strategy),
            new UploadIntegrityExpectation(
                row.ExpectedBytes,
                new Sha256Checksum(row.ExpectedSha256),
                new MediaContentType(row.DeclaredContentType)),
            new UploadIdempotencyMetadata(
                idempotencyRow.Key,
                new Sha256Checksum(idempotencyRow.RequestHash),
                idempotencyRow.ExpiresAtUtc),
            reservation);
        UploadSession session = Construct<UploadSession>(
            [
                typeof(Guid),
                typeof(UploadIntent),
                typeof(string),
                typeof(DateTimeOffset),
                typeof(DateTimeOffset),
            ],
            row.Id,
            intent,
            row.StagingKey,
            row.ExpiresAtUtc,
            row.CreatedAtUtc);

        Set(session, nameof(UploadSession.ProviderUploadId), row.ProviderUploadId);
        Set(session, nameof(UploadSession.State), Enum.Parse<UploadState>(row.State));
        Set(session, nameof(UploadSession.UpdatedAtUtc), row.UpdatedAtUtc);
        Set(session, nameof(UploadSession.Version), row.Version);
        SetField(
            session,
            "_lastKnownState",
            row.LastKnownState is null
                ? (UploadState?)null
                : Enum.Parse<UploadState>(row.LastKnownState));
        SortedDictionary<int, UploadPart> domainParts =
            GetField<SortedDictionary<int, UploadPart>>(session, "_parts");
        foreach (UploadPartRow part in parts.OrderBy(item => item.PartNumber))
        {
            domainParts.Add(
                part.PartNumber,
                new UploadPart(part.PartNumber, part.EntityTag, part.Checksum, part.SizeBytes));
        }

        return session;
    }

    private static T Construct<T>(Type[] parameterTypes, params object?[] arguments)
    {
        ConstructorInfo constructor = typeof(T).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null)
            ?? throw new InvalidOperationException(
                $"Persistence cannot find the expected {typeof(T).Name} constructor.");
        return (T)constructor.Invoke(arguments);
    }

    private static void Set<T, TValue>(T target, string propertyName, TValue value) =>
        SetField(target!, $"<{propertyName}>k__BackingField", value);

    private static void SetField<T, TValue>(T target, string fieldName, TValue value)
    {
        FieldInfo field = typeof(T).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Persistence cannot find field '{fieldName}' on {typeof(T).Name}.");
        field.SetValue(target, value);
    }

    private static TValue GetField<TValue>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Persistence cannot find field '{fieldName}' on {target.GetType().Name}.");
        return (TValue)field.GetValue(target)!;
    }

    private static T Required<T>(Vistara.Domain.Common.Result<T> result)
        where T : notnull
    {
        if (!result.TryGetValue(out T? value))
        {
            throw new InvalidOperationException(result.Error?.Message);
        }

        return value;
    }
}
