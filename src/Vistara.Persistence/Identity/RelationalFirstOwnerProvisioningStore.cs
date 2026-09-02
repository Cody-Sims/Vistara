using System.Data;
using System.Data.Common;
using System.Globalization;
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

/// <summary>
/// Provider keys the store accepts for an external first owner. A key is not a
/// label: it selects one exact issuer policy, and an issuer that does not match
/// that policy character for character is refused.
/// </summary>
public static class ExternalFirstOwnerProviders
{
    /// <summary>Microsoft Entra ID in the public cloud.</summary>
    public const string Entra = "entra";

    private static readonly Dictionary<string, ExternalIssuerPolicy> Policies =
        new(StringComparer.Ordinal)
        {
            [Entra] = ExternalIssuerPolicy.EntraPublicCloud,
        };

    /// <summary>
    /// Normalizes a provider key and reports whether the store supports it.
    /// Unknown providers fail closed rather than reaching the database.
    /// </summary>
    public static bool TryNormalize(string? provider, out string normalized) =>
        TryResolve(provider, out normalized, out _);

    internal static bool TryResolve(
        string? provider,
        out string normalized,
        out ExternalIssuerPolicy policy)
    {
        normalized = provider?.Trim().ToLowerInvariant() ?? string.Empty;
        return Policies.TryGetValue(normalized, out policy!);
    }
}

/// <summary>
/// The single issuer shape one provider is allowed to present. The policy names
/// an exact DNS authority and version segment, so the only accepted issuer for a
/// directory tenant is <c>https://{authority}/{tid}/{version}</c> on the default
/// HTTPS port with no user information, query, fragment, or extra path.
/// </summary>
internal sealed class ExternalIssuerPolicy
{
    /// <summary>
    /// Microsoft Entra ID public cloud. Legacy <c>sts.windows.net</c> and
    /// sovereign authorities are absent on purpose: the approved provider
    /// contract does not emit them, so they must not be accepted.
    /// </summary>
    internal static readonly ExternalIssuerPolicy EntraPublicCloud =
        new("login.microsoftonline.com", "v2.0");

    private ExternalIssuerPolicy(string authority, string version)
    {
        Authority = authority;
        Version = version;
    }

    internal string Authority { get; }

    internal string Version { get; }

    /// <summary>The one issuer string this policy stores for a directory tenant.</summary>
    internal string CanonicalIssuer(Guid directoryTenantId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"https://{Authority}/{directoryTenantId:D}/{Version}");

    /// <summary>
    /// Reports whether a candidate issuer is the canonical issuer for
    /// <paramref name="directoryTenantId"/>. Only surrounding whitespace, host
    /// and tenant letter case, and one trailing slash may differ; everything
    /// else, including encoded characters, is rejected before parsing.
    /// </summary>
    internal bool Accepts(string candidate, Guid directoryTenantId)
    {
        if (candidate.Length is 0 or > 256 || !IsPlainAscii(candidate))
        {
            return false;
        }

        // Dot segments are rejected before Uri collapses them into something
        // that resembles the canonical path.
        if (candidate.Contains("..", StringComparison.Ordinal) ||
            candidate.Contains("/./", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps ||
            !issuer.IsDefaultPort ||
            issuer.UserInfo.Length > 0 ||
            issuer.Query.Length > 0 ||
            issuer.Fragment.Length > 0 ||
            issuer.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        // Host comparison is case-insensitive because DNS is; a trailing dot,
        // an added label, or a punycode form is a different host and fails.
        if (!string.Equals(issuer.Host, Authority, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(issuer.IdnHost, Authority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = issuer.AbsolutePath;
        if (path.EndsWith('/'))
        {
            path = path[..^1];
        }

        // A leading empty part plus exactly the tenant and version segments:
        // an empty middle segment, a prefix such as common, or any extra path
        // yields a different part count and fails.
        string[] parts = path.Split('/');
        return parts.Length == 3 &&
            parts[0].Length == 0 &&
            Guid.TryParseExact(parts[1], "D", out Guid tenant) &&
            tenant == directoryTenantId &&
            string.Equals(parts[2], Version, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects control characters, non-ASCII text, percent-encoding, and
    /// backslashes before <see cref="Uri"/> can normalize them into something
    /// that resembles the canonical issuer.
    /// </summary>
    private static bool IsPlainAscii(string candidate)
    {
        foreach (char character in candidate)
        {
            if (character is '%' or '\\' or '<' or '>' ||
                character > '\u007e' ||
                char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A directory identity for the hosted entry point, identified by provider,
/// directory tenant identifier (Entra <c>tid</c>) and object identifier (Entra
/// <c>oid</c>). The provider is validation input rather than a stored column: it
/// selects the issuer policy, and the persisted key is the canonical issuer
/// (which embeds the provider authority and the directory tenant) together with
/// the object identifier as the subject, the pair external sign-in resolves
/// users by. Email and display name are profile attributes and never part of
/// the key.
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
        Issuer = NormalizeIssuer(normalizedProvider, issuer, directoryTenantId);
        Subject = SubjectFor(objectId);
    }

    public Guid ExternalIdentityId { get; }

    /// <summary>
    /// The normalized provider key, for example <c>entra</c>. It selects the
    /// issuer policy and is not persisted as its own column.
    /// </summary>
    public string Provider { get; }

    /// <summary>
    /// The canonical issuer for <see cref="Provider"/> and
    /// <see cref="DirectoryTenantId"/>. It is generated from the policy rather
    /// than copied from the caller, so no supplied text reaches the database.
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
    /// Returns the one issuer string <paramref name="provider"/> may present for
    /// a directory tenant. Provisioning stores this value and sign-in must
    /// compare identifier tokens against it.
    /// </summary>
    public static string CanonicalIssuer(string provider, Guid directoryTenantId)
    {
        if (!ExternalFirstOwnerProviders.TryResolve(
                provider,
                out _,
                out ExternalIssuerPolicy policy))
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

        return policy.CanonicalIssuer(directoryTenantId);
    }

    /// <summary>
    /// Validates an issuer against the policy the provider selects and returns
    /// the canonical form. Sign-in and identifier-token validation must run a
    /// token issuer through this method before comparing it with a stored
    /// identity, because the stored key is always the canonical form.
    /// </summary>
    public static string NormalizeIssuer(
        string provider,
        string issuer,
        Guid directoryTenantId)
    {
        if (!ExternalFirstOwnerProviders.TryResolve(
                provider,
                out string normalizedProvider,
                out ExternalIssuerPolicy policy))
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

        string candidate = issuer?.Trim() ?? string.Empty;
        if (!policy.Accepts(candidate, directoryTenantId))
        {
            throw new ArgumentException(
                $"The {normalizedProvider} issuer must be " +
                $"'{policy.CanonicalIssuer(directoryTenantId)}'.",
                nameof(issuer));
        }

        return policy.CanonicalIssuer(directoryTenantId);
    }

    /// <summary>
    /// Returns the stored subject for a directory object identifier. Sign-in
    /// must resolve owners by this form together with the canonical issuer.
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
