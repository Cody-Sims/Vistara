namespace Vistara.Persistence.Media;

internal sealed class PublicDerivativeRouteRow
{
    public string LookupDigest { get; set; } = string.Empty;
    public Guid RoutedTenantId { get; set; }
    public Guid RequestId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
