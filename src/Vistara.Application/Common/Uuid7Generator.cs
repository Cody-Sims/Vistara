namespace Vistara.Application.Common;

public sealed class Uuid7Generator(IClock clock) : IUuid7Generator
{
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public Guid NewId() => Guid.CreateVersion7(_clock.UtcNow);
}
