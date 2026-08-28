using Vistara.Application.Common;
using Vistara.Application.Identity;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.UnitTests.Identity;

public sealed class IdentityFactoryTests
{
    [Fact]
    public void Factory_uses_injected_time_and_uuid7_ids_for_identity_metadata()
    {
        DateTimeOffset now = new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);
        Guid userGuid = Guid.CreateVersion7(now);
        Guid keyGuid = Guid.CreateVersion7(now.AddMilliseconds(1));
        var generator = new SequenceIdGenerator(userGuid, keyGuid);
        var factory = new IdentityFactory(generator, new StubClock(now));

        var userResult = factory.CreateUser("alice@example.com", "Alice");
        Assert.True(userResult.TryGetValue(out User? user));
        Assert.Equal(new UserId(userGuid), user.Id);
        Assert.Equal(now, user.CreatedAt);

        var keyResult = factory.CreateApiKeyMetadata(
            new TenantId(Guid.CreateVersion7(now.AddMilliseconds(2))),
            user.Id,
            "vst_01abc",
            new string('a', 64),
            ApiKeyScope.ReadAssets,
            now.AddDays(1));
        Assert.True(keyResult.TryGetValue(out ApiKeyMetadata? key));
        Assert.Equal(new ApiKeyId(keyGuid), key.Id);
        Assert.Equal(now, key.CreatedAt);
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class SequenceIdGenerator(params Guid[] ids) : IUuid7Generator
    {
        private int _index;

        public Guid NewId() => ids[_index++];
    }
}
