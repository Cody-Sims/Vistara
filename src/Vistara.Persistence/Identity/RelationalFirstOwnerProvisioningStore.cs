using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
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

public sealed record FirstOwnerProvisioningRequest(
    Tenant Tenant,
    User User,
    TenantMembership Membership,
    Guid LocalIdentityId,
    string PasswordHash,
    AuditRecord Audit);

/// <summary>
/// Writes the whole first-owner bootstrap inside one serializable transaction
/// on one connection so tenant, user, local identity, credential, membership,
/// audit, and the singleton marker either all land or none do.
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

                context.LocalCredentials.Add(new LocalCredentialRow
                {
                    LocalIdentityId = request.LocalIdentityId,
                    UserId = request.User.Id.Value,
                    PasswordHash = request.PasswordHash,
                    UpdatedAtUtc = request.Audit.OccurredAtUtc,
                    Version = 1,
                });
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
    public static bool IsContentionOrConstraint(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SqliteException sqlite when sqlite.SqliteErrorCode is 5 or 6 or 19:
                case SqliteException locked when locked.SqliteExtendedErrorCode is 261 or 262:
                    return true;
                case PostgresException postgres when postgres.SqlState is
                    "23505" or "23514" or "40001" or "40P01" or "55P03":
                    return true;
                default:
                    continue;
            }
        }

        return false;
    }

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
