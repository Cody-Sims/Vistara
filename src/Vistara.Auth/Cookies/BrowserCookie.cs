using System.Globalization;

namespace Vistara.Auth.Cookies;

public sealed record BrowserCookie
{
    private BrowserCookie(
        string name,
        string value,
        string path,
        string? domain,
        bool secure,
        bool httpOnly,
        BrowserSameSite sameSite,
        TimeSpan maxAge)
    {
        Name = name;
        Value = value;
        Path = path;
        Domain = domain;
        Secure = secure;
        HttpOnly = httpOnly;
        SameSite = sameSite;
        MaxAge = maxAge;
    }

    public string Name { get; }

    public string Value { get; }

    public string Path { get; }

    public string? Domain { get; }

    public bool Secure { get; }

    public bool HttpOnly { get; }

    public BrowserSameSite SameSite { get; }

    public TimeSpan MaxAge { get; }

    public bool IsDeletion => MaxAge == TimeSpan.Zero;

    public static BrowserCookie Session(
        CookieAuthOptions options,
        string value,
        TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(character =>
                character <= 0x20 ||
                character >= 0x7f ||
                character is ';' or ',') ||
            maxAge <= TimeSpan.Zero ||
            maxAge > options.AbsoluteLifetime)
        {
            throw new ArgumentException("The browser cookie is invalid.");
        }

        return new BrowserCookie(
            options.CookieName,
            value,
            options.Path,
            options.Domain,
            options.Secure,
            options.HttpOnly,
            options.SameSite,
            maxAge);
    }

    public static BrowserCookie Delete(CookieAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new BrowserCookie(
            options.CookieName,
            string.Empty,
            options.Path,
            options.Domain,
            options.Secure,
            options.HttpOnly,
            options.SameSite,
            TimeSpan.Zero);
    }

    public string ToSetCookieHeader()
    {
        long seconds = checked((long)MaxAge.TotalSeconds);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Name}={Value}; Path={Path}; Max-Age={seconds}; Secure; HttpOnly; SameSite={SameSite}");
    }

    public override string ToString() =>
        $"{nameof(BrowserCookie)} {{ Name = {Name}, Value = [REDACTED], Path = {Path}, Domain = {Domain}, Secure = {Secure}, HttpOnly = {HttpOnly}, SameSite = {SameSite}, MaxAge = {MaxAge} }}";
}
