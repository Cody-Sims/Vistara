using System.Text.Json;
using Vistara.Application.Common.Auditing;
using Vistara.Persistence.Ingest;

namespace Vistara.Persistence.Auditing;

/// <summary>
/// Appends tenant-scoped audit events to the <c>audit_events</c> table.
/// </summary>
public sealed class RelationalAuditWriter(VistaraDbContext context) : IAuditWriter
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask AppendAsync(
        AuditRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        if (_context.TenantId != record.TenantId.Value)
        {
            throw new InvalidOperationException(
                "The audit record does not match the active tenant scope.");
        }

        _context.AuditEvents.Add(new AuditEventRow
        {
            Id = record.Id.Value,
            TenantId = record.TenantId.Value,
            ActorKind = record.Actor.Kind.ToString(),
            ActorIdentifier = record.Actor.Identifier,
            Action = record.Action,
            ResourceType = record.Resource.Type,
            ResourceIdentifier = record.Resource.Identifier,
            BeforeJson = Serialize(record.Before),
            AfterJson = Serialize(record.After),
            Outcome = record.Outcome.ToString(),
            OccurredAtUtc = record.OccurredAtUtc,
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string Serialize(AuditChangeSummary summary) =>
        JsonSerializer.Serialize(summary.Fields);
}
