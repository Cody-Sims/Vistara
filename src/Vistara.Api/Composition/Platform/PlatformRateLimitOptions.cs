using System.Globalization;
using Microsoft.Extensions.Options;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// The persisted request-bucket limits, bound from <c>Platform:RateLimits</c>.
///
/// These limits belong to the database-backed counter behind
/// <see cref="IPlatformRateLimitHook"/>, which is a second, coarser ceiling in
/// front of the in-process framework limiter. The counter is keyed by the
/// transport peer address and no forwarded header is ever trusted, so the key
/// is only a client when the peer is the client. Behind a reverse proxy or a
/// managed ingress with no trusted proxy network configured, every request
/// arrives from the same peer and each bucket becomes one shared ceiling for
/// the whole deployment. That is why these are configuration and not
/// constants: a limit that is a fair per-client budget on a Compose host is an
/// outage on a hosted deployment where the entire tenant shares one bucket.
///
/// The defaults are the values this adapter shipped with, so a deployment that
/// configures nothing behaves exactly as before. Every value is bounded and
/// validated at startup, and there is no disabled, zero, or unlimited setting,
/// because a limit an operator can switch off is a limit that will be found
/// switched off.
/// </summary>
public sealed class PlatformRateLimitOptions
{
    public const string SectionName = "Platform:RateLimits";

    /// <summary>The lowest permit count that is still a limit, not a block.</summary>
    internal const int MinimumLimit = 1;

    /// <summary>
    /// The highest permit count that is still a limit. It is far above the
    /// throughput a single deployment serves, which keeps an operator from
    /// spelling "unlimited" as a very large number.
    /// </summary>
    internal const int MaximumLimit = 1_000_000;

    internal const int DefaultApi = 300;

    internal const int DefaultEvents = 30;

    internal const int DefaultDelivery = 120;

    internal const int DefaultMedia = 600;

    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan MaximumWindow = TimeSpan.FromHours(1);

    private readonly List<string> _configurationFailures = [];

    /// <summary>
    /// The fixed window each bucket counts within, configured as a duration
    /// such as <c>00:01:00</c>.
    /// </summary>
    public TimeSpan Window { get; set; } = DefaultWindow;

    /// <summary>Permits per window for <c>/api/v1</c> requests.</summary>
    public int Api { get; set; } = DefaultApi;

    /// <summary>Permits per window for the <c>/api/v1/events</c> stream.</summary>
    public int Events { get; set; } = DefaultEvents;

    /// <summary>Permits per window for <c>/delivery</c> requests.</summary>
    public int Delivery { get; set; } = DefaultDelivery;

    /// <summary>Permits per window for <c>/media</c> requests.</summary>
    public int Media { get; set; } = DefaultMedia;

    /// <summary>
    /// Settings that were present but unreadable. They are collected while
    /// binding and reported by the validator, so a deployment learns about
    /// every unreadable setting at once instead of one restart at a time.
    /// </summary>
    internal IReadOnlyList<string> ConfigurationFailures => _configurationFailures;

    internal void AddConfigurationFailure(string setting, string requirement) =>
        _configurationFailures.Add(Failure(setting, requirement));

    internal static string Failure(string setting, string requirement) =>
        $"{SectionName} is invalid: {setting} {requirement}.";

    internal int LimitFor(PlatformRateLimitBucket bucket) => bucket switch
    {
        PlatformRateLimitBucket.Events => Events,
        PlatformRateLimitBucket.Delivery => Delivery,
        PlatformRateLimitBucket.Media => Media,
        _ => Api,
    };
}

/// <summary>The request families the persisted counter separates.</summary>
internal enum PlatformRateLimitBucket
{
    Api,
    Events,
    Delivery,
    Media,
}

/// <summary>
/// The hosted profile for a deployment that runs behind a managed ingress with
/// no trusted proxy network, where the persisted counter is a deployment-wide
/// ceiling rather than a per-client budget.
///
/// The window stays at one minute so the ceiling is expressed in the same unit
/// as the in-process framework limiter, and the bucket limits are raised to
/// meet that limiter so the shared counter stops being the binding constraint
/// on ordinary traffic. Events stays an order of magnitude lower because a
/// stream request holds a connection instead of completing, so it is the one
/// bucket where a runaway caller is still worth stopping early.
///
/// These values do not make the counter per-client, and nothing here is a
/// substitute for authorization: they only stop one shared bucket from failing
/// the deployment, while the sensitive setup and storage surfaces keep their
/// own guards.
/// </summary>
public static class PlatformRateLimitHostedProfile
{
    public const int Api = 6000;

    public const int Events = 600;

    public const int Delivery = 6000;

    public const int Media = 6000;

    public const string Window = "00:01:00";

    /// <summary>
    /// The exact configuration keys and values a hosted deployment sets. An
    /// environment-variable deployment spells each key with <c>__</c> in place
    /// of <c>:</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Configuration { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{PlatformRateLimitOptions.SectionName}:Window"] = Window,
            [$"{PlatformRateLimitOptions.SectionName}:Api"] = Text(Api),
            [$"{PlatformRateLimitOptions.SectionName}:Events"] = Text(Events),
            [$"{PlatformRateLimitOptions.SectionName}:Delivery"] = Text(Delivery),
            [$"{PlatformRateLimitOptions.SectionName}:Media"] = Text(Media),
        };

    private static string Text(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads <c>Platform:RateLimits</c> without the configuration binder's own
/// conversion, so an unreadable setting is reported by name and by what the
/// setting accepts, and never by repeating what the deployment wrote.
///
/// Reading the window explicitly also rejects a bare number. The binder would
/// read <c>60</c> as sixty days, which is a rate limit that looks configured
/// and never resets.
/// </summary>
internal sealed class PlatformRateLimitOptionsSetup(IConfiguration configuration) :
    IConfigureOptions<PlatformRateLimitOptions>
{
    public void Configure(PlatformRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IConfigurationSection section =
            configuration.GetSection(PlatformRateLimitOptions.SectionName);
        if (Read(section, nameof(PlatformRateLimitOptions.Window)) is { } window)
        {
            if (TryParseWindow(window, out TimeSpan parsed))
            {
                options.Window = parsed;
            }
            else
            {
                options.AddConfigurationFailure(
                    nameof(PlatformRateLimitOptions.Window),
                    "must be a duration such as 00:01:00");
            }
        }

        Bind(
            section,
            options,
            nameof(PlatformRateLimitOptions.Api),
            static (target, value) => target.Api = value);
        Bind(
            section,
            options,
            nameof(PlatformRateLimitOptions.Events),
            static (target, value) => target.Events = value);
        Bind(
            section,
            options,
            nameof(PlatformRateLimitOptions.Delivery),
            static (target, value) => target.Delivery = value);
        Bind(
            section,
            options,
            nameof(PlatformRateLimitOptions.Media),
            static (target, value) => target.Media = value);
    }

    private static void Bind(
        IConfigurationSection section,
        PlatformRateLimitOptions options,
        string setting,
        Action<PlatformRateLimitOptions, int> assign)
    {
        if (Read(section, setting) is not { } value)
        {
            return;
        }

        if (int.TryParse(
                value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int limit))
        {
            assign(options, limit);
            return;
        }

        options.AddConfigurationFailure(
            setting,
            "must be a whole number of requests per window");
    }

    /// <summary>
    /// A present setting is read, including an empty one: a setting an
    /// operator wrote and left blank is a mistake, not a request for the
    /// default.
    /// </summary>
    private static string? Read(IConfigurationSection section, string setting) =>
        section.GetSection(setting).Value;

    private static bool TryParseWindow(string value, out TimeSpan window)
    {
        window = default;
        return value.Contains(':', StringComparison.Ordinal) &&
            TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out window);
    }
}

/// <summary>
/// Rejects a rate-limit configuration at startup rather than at the first
/// request. Failures name the setting and what it accepts, and never repeat
/// the configured value, so a rejected deployment cannot echo its own
/// configuration into a log.
/// </summary>
internal sealed class PlatformRateLimitOptionsValidator :
    IValidateOptions<PlatformRateLimitOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        PlatformRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [.. options.ConfigurationFailures];
        if (options.Window < PlatformRateLimitOptions.MinimumWindow ||
            options.Window > PlatformRateLimitOptions.MaximumWindow)
        {
            failures.Add(PlatformRateLimitOptions.Failure(
                nameof(PlatformRateLimitOptions.Window),
                "must be between 1 second and 1 hour"));
        }

        ValidateLimit(nameof(PlatformRateLimitOptions.Api), options.Api, failures);
        ValidateLimit(nameof(PlatformRateLimitOptions.Events), options.Events, failures);
        ValidateLimit(
            nameof(PlatformRateLimitOptions.Delivery),
            options.Delivery,
            failures);
        ValidateLimit(nameof(PlatformRateLimitOptions.Media), options.Media, failures);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateLimit(
        string setting,
        int value,
        List<string> failures)
    {
        if (value is < PlatformRateLimitOptions.MinimumLimit
            or > PlatformRateLimitOptions.MaximumLimit)
        {
            failures.Add(PlatformRateLimitOptions.Failure(
                setting,
                "must be between one and one million requests per window"));
        }
    }
}
