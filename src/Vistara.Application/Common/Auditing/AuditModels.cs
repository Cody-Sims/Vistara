using System.Collections.ObjectModel;
using Vistara.Domain.Common;

namespace Vistara.Application.Common.Auditing;

public readonly record struct AuditEventId
{
    public AuditEventId(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Audit event IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct AuditTenantId
{
    public AuditTenantId(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Audit tenant IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public enum AuditActorKind
{
    User,
    ApiKey,
    System,
}

public sealed record AuditActor
{
    public AuditActor(AuditActorKind kind, string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        Kind = kind;
        Identifier = identifier;
    }

    public AuditActorKind Kind { get; }

    public string Identifier { get; }
}

public sealed record AuditResource
{
    public AuditResource(string type, string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        Type = type;
        Identifier = identifier;
    }

    public string Type { get; }

    public string Identifier { get; }
}

public enum AuditOutcome
{
    Succeeded,
    Rejected,
    Failed,
}

public sealed record AuditField
{
    public const string RedactedValue = "[REDACTED]";
    private static readonly string[] SensitiveNameParts =
    [
        "authorization",
        "password",
        "token",
        "secret",
        "credential",
        "cookie",
        "signedurl",
        "payload",
        "privatemetadata",
    ];

    private AuditField(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }

    public static Result<AuditField> CreatePlain(string name, string value)
    {
        ValidateNameAndValue(name, value);
        if (IsSensitive(name))
        {
            return Result.Failure<AuditField>(
                ResultError.Validation(
                    "audit.sensitive_value_must_be_redacted",
                    "Sensitive audit fields must be redacted."));
        }

        return Result.Success(new AuditField(name, value));
    }

    public static AuditField Plain(string name, string value)
    {
        Result<AuditField> result = CreatePlain(name, value);
        if (result.TryGetValue(out AuditField? field))
        {
            return field;
        }

        throw new ArgumentException(result.Error!.Message, nameof(name));
    }

    public static AuditField Redacted(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new AuditField(name, RedactedValue);
    }

    private static bool IsSensitive(string name)
    {
        string normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return SensitiveNameParts.Any(normalized.Contains);
    }

    private static void ValidateNameAndValue(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (name.Length > 128)
        {
            throw new ArgumentException("Audit field name cannot exceed 128 characters.", nameof(name));
        }

        if (value.Length > 512)
        {
            throw new ArgumentException("Audit field value cannot exceed 512 characters.", nameof(value));
        }
    }
}

public sealed class AuditChangeSummary
{
    private AuditChangeSummary(IReadOnlyDictionary<string, string> fields)
    {
        Fields = fields;
    }

    public static AuditChangeSummary Empty { get; } =
        new(new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)));

    public IReadOnlyDictionary<string, string> Fields { get; }

    public static Result<AuditChangeSummary> Create(IEnumerable<AuditField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (AuditField field in fields)
        {
            if (!values.TryAdd(field.Name, field.Value))
            {
                return Result.Failure<AuditChangeSummary>(
                    ResultError.Validation(
                        "audit.duplicate_field",
                        "Audit summaries cannot contain duplicate fields."));
            }
        }

        return Result.Success(
            new AuditChangeSummary(
                new ReadOnlyDictionary<string, string>(values)));
    }
}

public sealed record AuditRecord
{
    public AuditRecord(
        AuditEventId id,
        AuditTenantId tenantId,
        AuditActor actor,
        string action,
        AuditResource resource,
        AuditChangeSummary before,
        AuditChangeSummary after,
        AuditOutcome outcome,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Audit timestamp must be UTC.", nameof(occurredAtUtc));
        }

        Id = id;
        TenantId = tenantId;
        Actor = actor;
        Action = action;
        Resource = resource;
        Before = before;
        After = after;
        Outcome = outcome;
        OccurredAtUtc = occurredAtUtc;
    }

    public AuditEventId Id { get; }

    public AuditTenantId TenantId { get; }

    public AuditActor Actor { get; }

    public string Action { get; }

    public AuditResource Resource { get; }

    public AuditChangeSummary Before { get; }

    public AuditChangeSummary After { get; }

    public AuditOutcome Outcome { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
