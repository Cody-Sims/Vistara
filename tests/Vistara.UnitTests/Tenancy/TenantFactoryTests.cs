using Vistara.Application.Common;
using Vistara.Application.Tenancy;
using Vistara.Domain.Tenancy;

namespace Vistara.UnitTests.Tenancy;

public sealed class TenantFactoryTests
{
    [Fact]
    public void Factory_uses_injected_uuid7_generator_and_clock()
    {
        DateTimeOffset now = new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);
        Guid generatedId = Guid.CreateVersion7(now);
        var factory = new TenantFactory(new StubIdGenerator(generatedId), new StubClock(now));

        var result = factory.Create("family", "Family");

        Assert.True(result.TryGetValue(out Tenant? tenant));
        Assert.Equal(new TenantId(generatedId), tenant.Id);
        Assert.Equal(now, tenant.CreatedAt);
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StubIdGenerator(Guid id) : IUuid7Generator
    {
        public Guid NewId() => id;
    }
}
