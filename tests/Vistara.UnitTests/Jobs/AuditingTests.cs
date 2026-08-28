using Vistara.Application.Common.Auditing;

namespace Vistara.UnitTests.Jobs;

public sealed class AuditingTests
{
    [Theory]
    [InlineData("authorization")]
    [InlineData("password")]
    [InlineData("accessToken")]
    [InlineData("signed_url")]
    [InlineData("payload")]
    [InlineData("private.metadata")]
    public void Sensitive_audit_fields_require_redaction(string name)
    {
        var field = AuditField.CreatePlain(name, "secret");

        Assert.True(field.IsFailure);
        Assert.Equal("audit.sensitive_value_must_be_redacted", field.Error?.Code);
    }

    [Fact]
    public void Audit_record_accepts_redacted_sensitive_fields_and_safe_summaries()
    {
        AuditField redacted = AuditField.Redacted("authorization");
        Assert.True(AuditChangeSummary.Create([
            redacted,
            AuditField.Plain("state", "active"),
        ]).TryGetValue(out AuditChangeSummary? after));

        AuditRecord record = new(
            new AuditEventId(Guid.Parse("01990a2a-bc00-7000-8000-000000000021")),
            new AuditTenantId(Guid.Parse("01990a2a-bc00-7000-8000-000000000022")),
            new AuditActor(AuditActorKind.User, "user-1"),
            "asset.updated",
            new AuditResource("asset", "asset-1"),
            AuditChangeSummary.Empty,
            after,
            AuditOutcome.Succeeded,
            new DateTimeOffset(2026, 8, 28, 20, 0, 0, TimeSpan.Zero));

        Assert.Equal(AuditField.RedactedValue, record.After.Fields["authorization"]);
        Assert.Equal("active", record.After.Fields["state"]);
    }

    [Fact]
    public void Duplicate_audit_summary_fields_are_rejected()
    {
        var summary = AuditChangeSummary.Create([
            AuditField.Plain("state", "pending"),
            AuditField.Plain("state", "completed"),
        ]);

        Assert.True(summary.IsFailure);
        Assert.Equal("audit.duplicate_field", summary.Error?.Code);
    }

    [Fact]
    public void Audit_ids_require_uuid_version_seven()
    {
        Guid versionFour = Guid.Parse("11111111-1111-4111-8111-111111111111");

        Assert.Throws<ArgumentException>(() => new AuditEventId(versionFour));
        Assert.Throws<ArgumentException>(() => new AuditTenantId(versionFour));
    }
}
