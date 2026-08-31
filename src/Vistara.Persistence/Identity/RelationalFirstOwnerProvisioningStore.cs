using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Common.Auditing;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Model;
using Vistara.Persistence.Repositories;

namespace Vistara.Persistence.Identity;

public enum FirstOwnerProvisioningStatus
{
    /// <summary>This attempt is the single winner and every row is committed.</summary>
    Provisioned,

    /// <summary>Another attempt already owns the bootstrap marker.</summary>
    AlreadyProvisioned,

    /// <summary>
    /// A concurrent attempt made this one fail before any winner was observed.
    /// Nothing was written and the caller may retry.
    /// </summary>
    Contended,
}

public sealed record FirstOwnerProvisioningRequest
{
    /// <summary>
    /// Builds the shipped local-password bootstrap. The owner signs in with the
    /// local identity carried by <paramref name="user"/>.
    /// </summary>
    public FirstOwnerProvisioningRequest(
        Tenant tenant,
        User user,
        TenantMembership membership,
        Guid localIdentityId,
        string passwordHash,
        AuditRecord audit)
        : this(
            tenant,
            user,
            membership,
            new LocalFirstOwnerCredential(localIdentityId, passwordHash),
            audit)
    {
    }

    /// <summary>
    /// Builds a bootstrap for one authentication factor. The credential type
    /// decides which rows the store writes, so a request can never carry both a
    /// local password and an external directory identity.
    /// </summary>
    public FirstOwnerProvisioningRequest(
        Tenant tenant,
        User user,
        TenantMembership membership,
        FirstOwnerCredential credential,
        AuditRecord audit)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(audit);
        credential.EnsureMatches(user);
        Tenant = tenant;
        User = user;
        Membership = membership;
        Credential = credential;
        Audit = audit;
    }

    public Tenant Tenant { get; }

    public User User { get; }

    public TenantMembership Membership { get; }

    /// <summary>The single authentication factor the owner is provisioned with.</summary>
    public FirstOwnerCredential Credential { get; }

    public AuditRecord Audit { get; }
}

/// <summary>
/// The one authentication factor a first owner is created with. The hierarchy
/// is closed: a request supplies either a local password credential or an
/// external directory identity, never both and never neither.
/// </summary>
public abstract record FirstOwnerCredential
{
    private protected FirstOwnerCredential()
    {
    }

    /// <summary>
    /// Rejects an owner whose identity links disagree with this credential, so
    /// a mixed-factor bootstrap fails before any row is written.
    /// </summary>
    internal abstract void EnsureMatches(User user);

    /// <summary>
    /// Adds the credential rows for <paramref name="userId"/> to the same
    /// change tracker the rest of the bootstrap uses.
    /// </summary>
    internal abstract void Write(
        VistaraDbContext context,
        Guid userId,
        DateTimeOffset occurredAtUtc);
}

/// <summary>A password verifier for the local recovery owner.</summary>
public sealed record LocalFirstOwnerCredential : FirstOwnerCredential
{
    public LocalFirstOwnerCredential(Guid localIdentityId, string passwordHash)
    {
        if (localIdentityId == Guid.Empty)
        {
            throw new ArgumentException(
                "The local identity identifier is required.",
                nameof(localIdentityId));
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException(
                "The password verifier is required.",
                nameof(passwordHash));
        }

        LocalIdentityId = localIdentityId;
        PasswordHash = passwordHash;
    }

    public Guid LocalIdentityId { get; }

    public string PasswordHash { get; }

    internal override void EnsureMatches(User user)
    {
        if (user.ExternalIdentities.Count > 0)
        {
            throw new ArgumentException(
                "A local first owner cannot also carry an external identity.",
                nameof(user));
        }

        if (!user.LocalIdentities.Any(link => link.Id.Value == LocalIdentityId))
        {
            throw new ArgumentException(
                "The password verifier must belong to one of the owner's local identities.",
                nameof(user));
        }
    }

    internal override void Write(
        VistaraDbContext context,
        Guid userId,
        DateTimeOffset occurredAtUtc) =>
        context.LocalCredentials.Add(new LocalCredentialRow
        {
            LocalIdentityId = LocalIdentityId,
            UserId = userId,
            PasswordHash = PasswordHash,
            UpdatedAtUtc = occurredAtUtc,
            Version = 1,
        });
}

/// <summary>Provider keys the store accepts for an external first owner.</summary>
public static class ExternalFirstOwnerProviders
{
    /// <summary>Microsoft Entra ID.</summary>
    public const string Entra = "entra";

    private static readonly HashSet<string> Supported =
        new(StringComparer.Ordinal) { Entra };

    /// <summary>
    /// Normalizes a provider key and reports whether the store supports it.
    /// Unknown providers fail closed rather than reaching the database.
    /// </summary>
    public static bool TryNormalize(string? provider, out string normalized)
    {
        normalized = provider?.Trim().ToLowerInvariant() ?? string.Empty;
        return Supported.Contains(normalized);
    }
}

/// <summary>
/// A directory identity for the hosted entry point, keyed by provider,
/// directory tenant identifier (Entra <c>tid</c>) and object identifier (Entra
/// <c>oid</c>). The key is stored as the issuer that binds the provider to the
/// directory plus the object identifier as the subject, which is the pair every
/// external sign-in already resolves users by. Email and display name are
/// profile attributes and never part of the key.
/// </summary>
public sealed record ExternalFirstOwnerCredential : FirstOwnerCredential
{
    public ExternalFirstOwnerCredential(
        Guid externalIdentityId,
        string provider,
        string issuer,
        Guid directoryTenantId,
        Guid objectId)
    {
        if (externalIdentityId == Guid.Empty)
        {
            throw new ArgumentException(
                "The external identity identifier is required.",
                nameof(externalIdentityId));
        }

        if (!ExternalFirstOwnerProviders.TryNormalize(
                provider,
                out string normalizedProvider))
        {
            throw new ArgumentException(
                $"'{provider}' is not a supported external identity provider.",
                nameof(provider));
        }

        if (directoryTenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "The directory tenant identifier is required.",
                nameof(directoryTenantId));
        }

        if (objectId == Guid.Empty)
        {
            throw new ArgumentException(
                "The directory object identifier is required.",
                nameof(objectId));
        }

        ExternalIdentityId = externalIdentityId;
        Provider = normalizedProvider;
        DirectoryTenantId = directoryTenantId;
        ObjectId = objectId;
        Issuer = NormalizeIssuer(issuer, directoryTenantId);
        Subject = SubjectFor(objectId);
    }

    public Guid ExternalIdentityId { get; }

    /// <summary>The normalized provider key, for example <c>entra</c>.</summary>
    public string Provider { get; }

    /// <summary>
    /// The normalized token issuer. It is bound to
    /// <see cref="DirectoryTenantId"/>, so a multi-tenant or common-endpoint
    /// issuer cannot claim ownership.
    /// </summary>
    public string Issuer { get; }

    /// <summary>The directory tenant identifier (Entra <c>tid</c>).</summary>
    public Guid DirectoryTenantId { get; }

    /// <summary>The stable directory object identifier (Entra <c>oid</c>).</summary>
    public Guid ObjectId { get; }

    /// <summary>
    /// The stored subject. Sign-in resolves the owner by
    /// <see cref="Issuer"/> and this value.
    /// </summary>
    public string Subject { get; }

    internal override void EnsureMatches(User user)
    {
        if (user.LocalIdentities.Count > 0)
        {
            throw new ArgumentException(
                "An external first owner cannot also carry a local identity.",
                nameof(user));
        }

        if (user.ExternalIdentities.Count > 0)
        {
            throw new ArgumentException(
                "The store writes the external identity; the owner must not carry one.",
                nameof(user));
        }
    }

    internal override void Write(
        VistaraDbContext context,
        Guid userId,
        DateTimeOffset occurredAtUtc) =>
        context.ExternalIdentities.Add(new ExternalIdentityRow
        {
            Id = ExternalIdentityId,
            UserId = userId,
            Issuer = Issuer,
            Subject = Subject,
            LinkedAtUtc = occurredAtUtc,
        });

    /// <summary>
    /// Normalizes the issuer and proves it belongs to the claimed directory
    /// tenant. The identifier must appear as a path segment, which rejects the
    /// multi-tenant common endpoint and any issuer from another directory.
    /// Sign-in must normalize a token issuer through this method before it
    /// looks an owner up, because the stored key is the normalized form.
    /// </summary>
    public static string NormalizeIssuer(string issuer, Guid directoryTenantId)
    {
        if (directoryTenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "The directory tenant identifier is required.",
                nameof(directoryTenantId));
        }

        if (!ExternalIssuer.Create(issuer ?? string.Empty)
                .TryGetValue(out ExternalIssuer normalized))
        {
            throw new ArgumentException(
                "The external issuer must be an absolute URL without a query or fragment.",
                nameof(issuer));
        }

        var parsed = new Uri(normalized.Value, UriKind.Absolute);
        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The external issuer must use HTTPS.",
                nameof(issuer));
        }

        string canonical = directoryTenantId.ToString("D");
        var path = new StringBuilder();
        bool bound = false;
        foreach (string segment in parsed.Segments)
        {
            string name = segment.TrimEnd('/');
            if (Guid.TryParseExact(name, "D", out Guid candidate) &&
                candidate == directoryTenantId)
            {
                bound = true;
                _ = path.Append(canonical).Append(segment[name.Length..]);
                continue;
            }

            _ = path.Append(segment);
        }

        if (!bound)
        {
            throw new ArgumentException(
                "The external issuer must be bound to the directory tenant identifier.",
                nameof(issuer));
        }

        return (parsed.GetLeftPart(UriPartial.Authority) + path).TrimEnd('/');
    }

    /// <summary>
    /// Returns the stored subject for a directory object identifier. Sign-in
    /// must resolve owners by this form together with the normalized issuer.
    /// </summary>
    public static string SubjectFor(Guid objectId)
    {
        if (objectId == Guid.Empty)
        {
            throw new ArgumentException(
                "The directory object identifier is required.",
                nameof(objectId));
        }

        return objectId.ToString("D");
    }
}

/// <summary>
/// Writes the whole first-owner bootstrap inside one serializable transaction
/// on one connection so tenant, user, the single authentication factor
/// (local credential or external directory identity), membership, audit, and
/// the singleton marker either all land or none do.
/// </summary>
public sealed class RelationalFirstOwnerProvisioningStore(
    TenantDbContextFactory tenantContexts)
{
    private readonly TenantDbContextFactory _tenantContexts =
        tenantContexts ?? throw new ArgumentNullException(nameof(tenantContexts));

    /// <summary>
    /// Runs the bootstrap. <paramref name="beforeCommit"/> executes inside the
    /// transaction immediately before commit; throwing from it must leave the
    /// database untouched.
    /// </summary>
    public async ValueTask<FirstOwnerProvisioningStatus> ProvisionAsync(
        FirstOwnerProvisioningRequest request,
        Func<CancellationToken, ValueTask>? beforeCommit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Guid tenantId = request.Tenant.Id.Value;
        await using VistaraDbContext context = _tenantContexts.Create(tenantId);
        if (await IsProvisionedAsync(context, cancellationToken))
        {
            return FirstOwnerProvisioningStatus.AlreadyProvisioned;
        }

        IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                await EstablishRowSecurityAsync(context, tenantId, cancellationToken);

                // Claim the singleton marker first so a concurrent bootstrap
                // loses on a database key rather than on application checks.
                context.PlatformBootstrap.Add(new PlatformBootstrapRow
                {
                    Id = PlatformBootstrapRow.SingletonId,
                    OwnerTenantId = tenantId,
                    OwnerUserId = request.User.Id.Value,
                    ProvisionedAtUtc = request.Audit.OccurredAtUtc,
                    Version = 1,
                });
                await context.SaveChangesAsync(cancellationToken);

                await new TenantRepository(context)
                    .AddAsync(request.Tenant, cancellationToken);
                await new UserRepository(context)
                    .AddAsync(request.User, cancellationToken);
                await new TenantMembershipRepository(context)
                    .AddAsync(request.Membership, cancellationToken);

                request.Credential.Write(
                    context,
                    request.User.Id.Value,
                    request.Audit.OccurredAtUtc);
                context.AuditEvents.Add(ToRow(request.Audit));
                await context.SaveChangesAsync(cancellationToken);

                if (beforeCommit is not null)
                {
                    await beforeCommit(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                return FirstOwnerProvisioningStatus.Provisioned;
            }
            catch (Exception failure)
            {
                await RollbackAsync(transaction);
                if (failure is OperationCanceledException)
                {
                    throw;
                }

                return await ClassifyAsync(tenantId, failure, cancellationToken);
            }
        }
    }

    /// <summary>Reports whether a winner already owns the bootstrap marker.</summary>
    public async ValueTask<bool> IsProvisionedAsync(CancellationToken cancellationToken)
    {
        await using VistaraDbContext context =
            _tenantContexts.Create(Guid.CreateVersion7());
        return await IsProvisionedAsync(context, cancellationToken);
    }

    private static Task<bool> IsProvisionedAsync(
        VistaraDbContext context,
        CancellationToken cancellationToken) =>
        context.PlatformBootstrap.AsNoTracking().AnyAsync(cancellationToken);

    private async ValueTask<FirstOwnerProvisioningStatus> ClassifyAsync(
        Guid tenantId,
        Exception failure,
        CancellationToken cancellationToken)
    {
        // Classify by observed database state, never by exception type alone,
        // so an unrelated write failure is not reported as a completed
        // bootstrap.
        await using VistaraDbContext probe = _tenantContexts.Create(tenantId);
        if (await IsProvisionedAsync(probe, cancellationToken))
        {
            return FirstOwnerProvisioningStatus.AlreadyProvisioned;
        }

        if (IsContentionOrConstraint(failure))
        {
            return FirstOwnerProvisioningStatus.Contended;
        }

        throw failure;
    }

    private static async Task RollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The transaction was already completed or aborted by the provider.
        }
        catch (DbException)
        {
            // The connection is unusable; disposal releases it.
        }
    }

    private static async Task EstablishRowSecurityAsync(
        VistaraDbContext context,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (context.Database.ProviderName != PersistenceProviderNames.PostgreSql)
        {
            return;
        }

        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('vistara.tenant_id', {tenantId.ToString("D")}, true);",
            cancellationToken);
    }

    /// <summary>
    /// Reports whether a failure is database contention or a constraint
    /// violation. Classification never converts an unrelated failure into a
    /// completed bootstrap; the caller always confirms against the marker.
    /// </summary>
    public static bool IsContentionOrConstraint(Exception failure) =>
        RelationalFaultClassifier.IsContentionOrConstraint(failure);

    private static AuditEventRow ToRow(AuditRecord record) => new()
    {
        Id = record.Id.Value,
        TenantId = record.TenantId.Value,
        ActorKind = record.Actor.Kind.ToString(),
        ActorIdentifier = record.Actor.Identifier,
        Action = record.Action,
        ResourceType = record.Resource.Type,
        ResourceIdentifier = record.Resource.Identifier,
        BeforeJson = "{}",
        AfterJson = System.Text.Json.JsonSerializer.Serialize(record.After.Fields),
        Outcome = record.Outcome.ToString(),
        OccurredAtUtc = record.OccurredAtUtc,
    };
}

internal static class PersistenceProviderNames
{
    internal const string PostgreSql = "Npgsql.EntityFrameworkCore.PostgreSQL";
}
