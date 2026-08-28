using System.Security.Cryptography;
using System.Text;

namespace Vistara.Domain.Lifecycle;

public enum RelationshipKind
{
    Album,
    Tag,
    Favorite,
    Share,
    Grant,
}

public sealed record RelationshipReference(
    RelationshipKind Kind,
    Guid ResourceId);

public sealed class RelationshipSnapshot : IEquatable<RelationshipSnapshot>
{
    private readonly RelationshipReference[] _relationships;

    private RelationshipSnapshot(RelationshipReference[] relationships)
    {
        _relationships = relationships;
        Digest = ComputeDigest(relationships);
    }

    public static RelationshipSnapshot Empty { get; } = new([]);

    public IReadOnlyList<RelationshipReference> Relationships =>
        Array.AsReadOnly(_relationships);

    public int Count => _relationships.Length;

    public string Digest { get; }

    public static RelationshipSnapshot Create(IEnumerable<RelationshipReference> relationships)
    {
        ArgumentNullException.ThrowIfNull(relationships);

        RelationshipReference[] snapshot = relationships
            .Where(relationship => relationship.ResourceId != Guid.Empty)
            .Distinct()
            .OrderBy(relationship => relationship.Kind)
            .ThenBy(relationship => relationship.ResourceId)
            .ToArray();
        return snapshot.Length == 0 ? Empty : new RelationshipSnapshot(snapshot);
    }

    public bool Equals(RelationshipSnapshot? other) =>
        other is not null &&
        Digest == other.Digest &&
        _relationships.SequenceEqual(other._relationships);

    public override bool Equals(object? obj) => Equals(obj as RelationshipSnapshot);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Digest);

    private static string ComputeDigest(IEnumerable<RelationshipReference> relationships)
    {
        string canonical = string.Join(
            '\n',
            relationships.Select(relationship =>
                $"{(int)relationship.Kind}:{relationship.ResourceId:N}"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }
}
