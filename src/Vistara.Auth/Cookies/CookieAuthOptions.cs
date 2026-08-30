namespace Vistara.Auth.Cookies;

public sealed class CookieAuthOptions
{
    public const string ProductionCookieName = "__Host-vistara-session";
    public const string DefaultAntiforgeryHeaderName = "X-Vistara-CSRF";
    public static readonly TimeSpan MaximumAbsoluteLifetime = TimeSpan.FromDays(30);

    public CookieAuthOptions(
        TimeSpan? idleLifetime = null,
        TimeSpan? absoluteLifetime = null,
        TimeSpan? slidingRefreshInterval = null,
        string antiforgeryHeaderName = DefaultAntiforgeryHeaderName)
    {
        CookieName = ProductionCookieName;
        Path = "/";
        Domain = null;
        Secure = true;
        HttpOnly = true;
        SameSite = BrowserSameSite.Lax;
        IdleLifetime = idleLifetime ?? TimeSpan.FromMinutes(30);
        AbsoluteLifetime = absoluteLifetime ?? TimeSpan.FromHours(24);
        SlidingRefreshInterval = slidingRefreshInterval ?? TimeSpan.FromMinutes(10);

        if (IdleLifetime <= TimeSpan.Zero || IdleLifetime > AbsoluteLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleLifetime),
                "The idle lifetime must be positive and no longer than the absolute lifetime.");
        }

        if (AbsoluteLifetime <= TimeSpan.Zero ||
            AbsoluteLifetime > MaximumAbsoluteLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(absoluteLifetime),
                "The absolute lifetime must be positive and no longer than 30 days.");
        }

        if (SlidingRefreshInterval <= TimeSpan.Zero ||
            SlidingRefreshInterval > IdleLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slidingRefreshInterval),
                "The sliding refresh interval must be positive and no longer than the idle lifetime.");
        }

        AntiforgeryHeaderName = !IsValidHeaderName(antiforgeryHeaderName)
            ? throw new ArgumentException(
                "The antiforgery header name is invalid.",
                nameof(antiforgeryHeaderName))
            : antiforgeryHeaderName;
    }

    public string CookieName { get; }

    public string Path { get; }

    public string? Domain { get; }

    public bool Secure { get; }

    public bool HttpOnly { get; }

    public BrowserSameSite SameSite { get; }

    public TimeSpan IdleLifetime { get; }

    public TimeSpan AbsoluteLifetime { get; }

    public TimeSpan SlidingRefreshInterval { get; }

    public string AntiforgeryHeaderName { get; }

    private static bool IsValidHeaderName(string? value) =>
        value is { Length: >= 1 and <= 128 } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character == '-');
}

public enum BrowserSameSite
{
    Lax,
}
