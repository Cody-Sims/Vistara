using System.Text;
using Vistara.Api.Features;
using Xunit;

namespace Vistara.Api.ContractTests.Account;

public sealed class AdminCursorTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private const string Fingerprint = "0123456789abcdef";

    [Fact]
    public void A_cursor_round_trips_within_its_tenant_and_query()
    {
        Guid id = Guid.CreateVersion7();
        var cursor = new AdminCursor(TenantId, Fingerprint, 1_000, id);

        Assert.True(AdminCursor.TryDecode(
            cursor.Encode(),
            TenantId,
            Fingerprint,
            out AdminCursor decoded));
        Assert.Equal(cursor, decoded);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(3_155_378_976_000_000_000L)]
    public void Ticks_outside_the_representable_range_are_refused(long ticks)
    {
        string encoded = Encode(TenantId, Fingerprint, ticks, Guid.CreateVersion7());

        Assert.False(AdminCursor.TryDecode(
            encoded,
            TenantId,
            Fingerprint,
            out _));
    }

    [Fact]
    public void The_maximum_representable_tick_is_accepted()
    {
        string encoded = Encode(
            TenantId,
            Fingerprint,
            DateTime.MaxValue.Ticks,
            Guid.CreateVersion7());

        Assert.True(AdminCursor.TryDecode(encoded, TenantId, Fingerprint, out AdminCursor decoded));
        Assert.Equal(DateTime.MaxValue.Ticks, decoded.Ticks);
        _ = new DateTimeOffset(decoded.Ticks, TimeSpan.Zero);
    }

    [Fact]
    public void A_cursor_from_another_tenant_or_query_is_refused()
    {
        string encoded = Encode(TenantId, Fingerprint, 10, Guid.CreateVersion7());

        Assert.False(AdminCursor.TryDecode(
            encoded,
            Guid.CreateVersion7(),
            Fingerprint,
            out _));
        Assert.False(AdminCursor.TryDecode(
            encoded,
            TenantId,
            "fedcba9876543210",
            out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64url!!")]
    [InlineData("YWJj")]
    public void A_malformed_cursor_is_refused(string value)
    {
        Assert.False(AdminCursor.TryDecode(value, TenantId, Fingerprint, out _));
    }

    private static string Encode(
        Guid tenantId,
        string fingerprint,
        long ticks,
        Guid id)
    {
        string payload = string.Join(
            '|',
            tenantId.ToString("N"),
            fingerprint,
            ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            id.ToString("N"));
        return System.Buffers.Text.Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes(payload));
    }
}
