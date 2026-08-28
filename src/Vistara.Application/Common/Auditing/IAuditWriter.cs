namespace Vistara.Application.Common.Auditing;

public interface IAuditWriter
{
    ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken);
}
