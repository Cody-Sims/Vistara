using Vistara.Application.Common;

namespace Vistara.UnitTests.Common;

public sealed class Uuid7GeneratorTests
{
    [Fact]
    public void New_id_uses_the_injected_clock_and_is_a_rfc_compatible_uuid7()
    {
        DateTimeOffset timestamp = new(2030, 4, 5, 6, 7, 8, 123, TimeSpan.Zero);
        var clock = new StubClock(timestamp);
        var generator = new Uuid7Generator(clock);

        Guid id = generator.NewId();
        byte[] bytes = id.ToByteArray(bigEndian: true);

        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(7, bytes[6] >> 4);
        Assert.Equal(0b10, bytes[8] >> 6);
        Assert.Equal(timestamp.ToUnixTimeMilliseconds(), ReadUnixTimeMilliseconds(bytes));
    }

    [Fact]
    public void New_ids_sort_in_timestamp_order()
    {
        DateTimeOffset earlier = new(2030, 4, 5, 6, 7, 8, 123, TimeSpan.Zero);
        DateTimeOffset later = earlier.AddMilliseconds(1);
        var generator = new Uuid7Generator(new SequenceClock(earlier, later));

        Guid first = generator.NewId();
        Guid second = generator.NewId();

        Assert.True(CompareRfcBytes(first, second) < 0);
    }

    private static long ReadUnixTimeMilliseconds(byte[] bytes)
    {
        long timestamp = 0;

        for (int index = 0; index < 6; index++)
        {
            timestamp = (timestamp << 8) | bytes[index];
        }

        return timestamp;
    }

    private static int CompareRfcBytes(Guid left, Guid right)
    {
        return left
            .ToByteArray(bigEndian: true)
            .AsSpan()
            .SequenceCompareTo(right.ToByteArray(bigEndian: true));
    }

    private sealed class StubClock(DateTimeOffset timestamp) : IClock
    {
        public int ReadCount { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                ReadCount++;
                return timestamp;
            }
        }
    }

    private sealed class SequenceClock(params DateTimeOffset[] timestamps) : IClock
    {
        private int _index;

        public DateTimeOffset UtcNow => timestamps[_index++];
    }
}
