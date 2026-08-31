using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Reconciliation.Storage;

public static class BlobIntegrityServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraBlobIntegrityReconciliation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new BlobIntegrityOptions());
        services.TryAddScoped<
            IBlobIntegrityStatePort,
            RelationalBlobIntegrityStateAdapter>();
        services.TryAddScoped<BlobIntegrityService>();
        services.TryAddScoped<BlobIntegrityJobHandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJobHandler, BlobIntegrityJobHandler>());
        return services;
    }
}

internal sealed class RelationalBlobIntegrityStateAdapter(
    VistaraDbContext context,
    IMutableTenantScope tenantScope) : IBlobIntegrityStatePort
{
    private const string ActiveState = "Active";
    private const string MissingState = "Missing";

    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IMutableTenantScope _tenantScope =
        tenantScope ?? throw new ArgumentNullException(nameof(tenantScope));

    public async ValueTask<BlobIntegrityPage> ScanActiveAsync(
        Guid tenantId,
        Guid? cursor,
        int batchSize,
        CancellationToken cancellationToken)
    {
        EstablishTenant(tenantId);
        Guid after = cursor ?? Guid.Empty;
        List<BlobRow> rows = await _context.Blobs
            .AsNoTracking()
            .Where(row => row.State == ActiveState && row.Id.CompareTo(after) > 0)
            .OrderBy(row => row.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        BlobIntegrityRecord[] records =
        [
            .. rows.Select(row => new BlobIntegrityRecord(
                row.Id,
                row.ObjectKey,
                row.CreatedAtUtc)),
        ];
        return new BlobIntegrityPage(
            records,
            records.Length == batchSize ? records[^1].BlobId : null);
    }

    public async ValueTask<bool> MarkMissingAsync(
        Guid tenantId,
        Guid blobId,
        CancellationToken cancellationToken)
    {
        EstablishTenant(tenantId);
        int updated = await _context.Blobs
            .Where(row => row.Id == blobId && row.State == ActiveState)
            .ExecuteUpdateAsync(
                update => update.SetProperty(row => row.State, MissingState),
                cancellationToken);
        return updated == 1;
    }

    public async ValueTask<IReadOnlyCollection<string>>
        FilterUnknownObjectKeysAsync(
            Guid tenantId,
            IReadOnlyCollection<string> objectKeys,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectKeys);
        EstablishTenant(tenantId);
        if (objectKeys.Count == 0)
        {
            return [];
        }

        string[] candidates = [.. objectKeys];
        List<string> matched = await _context.Blobs
            .AsNoTracking()
            .Where(row => candidates.Contains(row.ObjectKey))
            .Select(row => row.ObjectKey)
            .ToListAsync(cancellationToken);
        var known = new HashSet<string>(matched, StringComparer.Ordinal);
        return [.. candidates.Where(key => !known.Contains(key))];
    }

    private void EstablishTenant(Guid tenantId) =>
        _tenantScope.Establish(tenantId);
}
