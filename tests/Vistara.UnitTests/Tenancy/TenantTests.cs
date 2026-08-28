using Vistara.Domain.Common;
using Vistara.Domain.Tenancy;

namespace Vistara.UnitTests.Tenancy;

public sealed class TenantTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [Fact]
    public void Create_normalizes_slug_and_initializes_active_versioned_tenant()
    {
        TenantId id = new(Guid.CreateVersion7(CreatedAt));

        Result<Tenant> result = Tenant.Create(id, "  Family-PHOTOS  ", " Family Photos ", CreatedAt);

        Assert.True(result.TryGetValue(out Tenant? tenant));
        Assert.Equal(id, tenant.Id);
        Assert.Equal("family-photos", tenant.Slug.Value);
        Assert.Equal("Family Photos", tenant.Name);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(CreatedAt, tenant.CreatedAt);
        Assert.Equal(CreatedAt, tenant.UpdatedAt);
        Assert.Equal(1, tenant.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two--hyphens")]
    [InlineData("contains space")]
    public void Create_rejects_invalid_slugs(string slug)
    {
        Result<Tenant> result = Tenant.Create(
            new TenantId(Guid.CreateVersion7(CreatedAt)),
            slug,
            "Family Photos",
            CreatedAt);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Validation, result.Error?.Category);
        Assert.Equal("tenancy.invalid_slug", result.Error?.Code);
    }

    [Fact]
    public void Suspend_activate_and_deactivate_enforce_status_transitions_and_versions()
    {
        Tenant tenant = CreateTenant();
        DateTimeOffset suspendedAt = CreatedAt.AddMinutes(1);
        DateTimeOffset activatedAt = CreatedAt.AddMinutes(2);
        DateTimeOffset deactivatedAt = CreatedAt.AddMinutes(3);

        Assert.True(tenant.Suspend(suspendedAt).IsSuccess);
        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.Equal(2, tenant.Version);

        Result duplicateSuspend = tenant.Suspend(suspendedAt);
        Assert.Equal("tenancy.status_unchanged", duplicateSuspend.Error?.Code);
        Assert.Equal(2, tenant.Version);

        Assert.True(tenant.Activate(activatedAt).IsSuccess);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(3, tenant.Version);

        Assert.True(tenant.Deactivate(deactivatedAt).IsSuccess);
        Assert.Equal(TenantStatus.Deactivated, tenant.Status);
        Assert.Equal(4, tenant.Version);

        Result terminalTransition = tenant.Activate(deactivatedAt.AddMinutes(1));
        Assert.Equal("tenancy.invalid_status_transition", terminalTransition.Error?.Code);
        Assert.Equal(4, tenant.Version);
    }

    [Fact]
    public void Mutations_reject_non_utc_or_backdated_timestamps()
    {
        Tenant tenant = CreateTenant();

        Result nonUtc = tenant.Suspend(CreatedAt.ToOffset(TimeSpan.FromHours(2)));
        Result backdated = tenant.Suspend(CreatedAt.AddTicks(-1));

        Assert.Equal("common.timestamp_not_utc", nonUtc.Error?.Code);
        Assert.Equal("common.timestamp_out_of_order", backdated.Error?.Code);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(1, tenant.Version);
    }

    [Fact]
    public void Tenant_id_requires_a_uuid7()
    {
        Assert.Throws<ArgumentException>(() => new TenantId(Guid.NewGuid()));
    }

    private static Tenant CreateTenant()
    {
        Result<Tenant> result = Tenant.Create(
            new TenantId(Guid.CreateVersion7(CreatedAt)),
            "family-photos",
            "Family Photos",
            CreatedAt);
        Assert.True(result.TryGetValue(out Tenant? tenant));
        return tenant;
    }
}
