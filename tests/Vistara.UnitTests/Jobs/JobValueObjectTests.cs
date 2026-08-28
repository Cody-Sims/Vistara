using Vistara.Domain.Jobs;

namespace Vistara.UnitTests.Jobs;

public sealed class JobValueObjectTests
{
    [Fact]
    public void Equal_stable_dedupe_keys_compare_equal()
    {
        JobDedupeKey first = new("tenant-1:derivative:r1:recipe-1");
        JobDedupeKey second = new("tenant-1:derivative:r1:recipe-1");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Dedupe_key_rejects_missing_values(string value)
    {
        Assert.Throws<ArgumentException>(() => new JobDedupeKey(value));
    }

    [Fact]
    public void Dedupe_key_rejects_values_too_large_for_a_stable_index()
    {
        string value = new('a', JobDedupeKey.MaximumLength + 1);

        Assert.Throws<ArgumentException>(() => new JobDedupeKey(value));
    }

    [Fact]
    public void Dedupe_identity_is_scoped_to_the_tenant()
    {
        JobDedupeKey key = new("derivative:r1:recipe-1");
        JobDedupeIdentity first = new(
            new JobTenantId(Guid.Parse("01990a2a-bc00-7000-8000-000000000041")),
            key);
        JobDedupeIdentity same = new(
            new JobTenantId(Guid.Parse("01990a2a-bc00-7000-8000-000000000041")),
            key);
        JobDedupeIdentity anotherTenant = new(
            new JobTenantId(Guid.Parse("01990a2a-bc00-7000-8000-000000000042")),
            key);

        Assert.Equal(first, same);
        Assert.NotEqual(first, anotherTenant);
    }

    [Fact]
    public void Job_ids_require_uuid_version_seven()
    {
        Guid versionFour = Guid.Parse("11111111-1111-4111-8111-111111111111");

        Assert.Throws<ArgumentException>(() => new JobId(versionFour));
    }
}
