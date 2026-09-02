using System.Globalization;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Security;
using Vistara.Api.Security;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Whose requests a persisted bucket counts.
///
/// Both request ceilings - the in-process framework limiter and the persisted
/// counter - partition on the connection peer as it stands after
/// forwarded-header processing. What that peer is depends on the deployment,
/// and no code can tell which one it is, so the deployment says.
/// </summary>
public enum PlatformRateLimitPartitionMode
{
    /// <summary>
    /// The peer is the client: a direct connection, or the forwarded client
    /// behind a proxy the deployment trusts. Each bucket is a per-client
    /// budget. This is what a Compose deployment is, and what an unconfigured
    /// deployment stays.
    /// </summary>
    ForwardedClient,

    /// <summary>
    /// The peer is a shared ingress that no forwarded header is trusted from,
    /// so every request in the deployment shares one bucket and each limit is
    /// a ceiling for the whole deployment rather than for a client.
    /// </summary>
    SharedIngress,
}

/// <summary>
/// The persisted request-bucket limits, bound from <c>Platform:RateLimits</c>.
///
/// These limits belong to the database-backed counter behind
/// <see cref="IPlatformRateLimitHook"/>, which is a second ceiling in front of
/// the in-process framework limiter in the security composition. Both count
/// the connection peer as it stands after forwarded-header processing, so
/// behind a managed ingress with no trusted proxy every request shares one
/// peer and each bucket is a ceiling for the whole deployment. That is why
/// these are configuration and not constants: a limit that is a fair
/// per-client budget on a Compose host is an outage on a hosted deployment
/// where the entire tenant shares one bucket.
///
/// The defaults are the values this adapter shipped with, so a deployment that
/// configures nothing behaves exactly as before. Every value is bounded and
/// validated at startup, and there is no disabled, zero, or unlimited setting,
/// because a limit an operator can switch off is a limit that will be found
/// switched off. Raising a bucket requires declaring
/// <see cref="PartitionMode"/>, so hosted-scale limits can never be applied to
/// a deployment that hands them to every client.
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
    /// Whose requests a bucket counts. Undeclared means the shipped
    /// per-client behaviour, which is the only thing an existing deployment
    /// can be, and any bucket raised above the limit it ships with requires
    /// this to be declared.
    /// </summary>
    public PlatformRateLimitPartitionMode? PartitionMode { get; set; }

    internal PlatformRateLimitPartitionMode Mode =>
        PartitionMode ?? PlatformRateLimitPartitionMode.ForwardedClient;

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

internal static class PlatformRateLimitBuckets
{
    internal static readonly PlatformRateLimitBucket[] All =
    [
        PlatformRateLimitBucket.Api,
        PlatformRateLimitBucket.Events,
        PlatformRateLimitBucket.Delivery,
        PlatformRateLimitBucket.Media,
    ];

    /// <summary>
    /// The stored key segment. These strings are part of the persisted key, so
    /// changing one silently resets every live window.
    /// </summary>
    internal static string Key(PlatformRateLimitBucket bucket) => bucket switch
    {
        PlatformRateLimitBucket.Events => "events",
        PlatformRateLimitBucket.Delivery => "delivery",
        PlatformRateLimitBucket.Media => "media",
        _ => "api",
    };

    internal static string Setting(PlatformRateLimitBucket bucket) => bucket switch
    {
        PlatformRateLimitBucket.Events => nameof(PlatformRateLimitOptions.Events),
        PlatformRateLimitBucket.Delivery => nameof(PlatformRateLimitOptions.Delivery),
        PlatformRateLimitBucket.Media => nameof(PlatformRateLimitOptions.Media),
        _ => nameof(PlatformRateLimitOptions.Api),
    };

    internal static int ShippedLimit(PlatformRateLimitBucket bucket) => bucket switch
    {
        PlatformRateLimitBucket.Events => PlatformRateLimitOptions.DefaultEvents,
        PlatformRateLimitBucket.Delivery => PlatformRateLimitOptions.DefaultDelivery,
        PlatformRateLimitBucket.Media => PlatformRateLimitOptions.DefaultMedia,
        _ => PlatformRateLimitOptions.DefaultApi,
    };

    /// <summary>
    /// A path this bucket counts, used to ask the security composition whether
    /// the framework limiter counts the same requests.
    /// </summary>
    internal static PathString RepresentativePath(PlatformRateLimitBucket bucket) =>
        bucket switch
        {
            PlatformRateLimitBucket.Events => new PathString("/api/v1/events"),
            PlatformRateLimitBucket.Delivery => new PathString("/delivery/asset"),
            PlatformRateLimitBucket.Media => new PathString("/media/asset"),
            _ => new PathString("/api/v1/assets"),
        };
}

/// <summary>
/// The hosted handoff for a deployment that runs behind a managed ingress with
/// no trusted proxy network, where both ceilings are deployment-wide rather
/// than per-client.
///
/// Both are raised together, because raising one alone changes nothing: the
/// framework limiter counts the same shared peer, so it would simply become
/// the binding constraint. The window stays at one minute in both, and events
/// stays an order of magnitude lower than the rest because a stream request
/// holds a connection instead of completing, so it is the one bucket where a
/// runaway caller is still worth stopping early.
///
/// These values do not make either ceiling per-client, and nothing here is a
/// substitute for authorization: they stop one shared bucket from failing the
/// deployment, while the sensitive setup and storage-validation surfaces keep
/// their own guards - guards that are honestly shared under this profile,
/// because a shared ingress is what the deployment has.
/// </summary>
public static class PlatformRateLimitHostedProfile
{
    public const int Api = 6000;

    public const int Events = 600;

    public const int Delivery = 6000;

    public const int Media = 6000;

    public const string Window = "00:01:00";

    /// <summary>The framework limiter, raised to match the shared ceiling.</summary>
    public const int FrameworkRequestsPerWindow = 6000;

    public const string FrameworkWindow = "00:01:00";

    /// <summary>
    /// The exact configuration a hosted deployment sets: both ceilings, the
    /// declared partition, and no proxy trust, because trusting a forwarded
    /// header from an ingress that anyone can send through would let a caller
    /// mint an unlimited number of buckets.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Configuration { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{PlatformRateLimitOptions.SectionName}:PartitionMode"] =
                nameof(PlatformRateLimitPartitionMode.SharedIngress),
            [$"{PlatformRateLimitOptions.SectionName}:Window"] = Window,
            [$"{PlatformRateLimitOptions.SectionName}:Api"] = Text(Api),
            [$"{PlatformRateLimitOptions.SectionName}:Events"] = Text(Events),
            [$"{PlatformRateLimitOptions.SectionName}:Delivery"] = Text(Delivery),
            [$"{PlatformRateLimitOptions.SectionName}:Media"] = Text(Media),
            [$"{VistaraSecurityOptions.SectionName}:Limits:RequestsPerWindow"] =
                Text(FrameworkRequestsPerWindow),
            [$"{VistaraSecurityOptions.SectionName}:Limits:RateLimitWindow"] =
                FrameworkWindow,
        };

    /// <summary>
    /// The same handoff as container environment variables, which is the form
    /// an infrastructure template sets it in.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnvironmentVariables { get; } =
        Configuration.ToDictionary(
            static entry => entry.Key.Replace(":", "__", StringComparison.Ordinal),
            static entry => entry.Value,
            StringComparer.Ordinal);

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
/// and never resets, and it would read a partition of <c>0</c> as a declared
/// mode.
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

        if (Read(section, nameof(PlatformRateLimitOptions.PartitionMode))
            is { } partition)
        {
            if (TryParseMode(partition, out PlatformRateLimitPartitionMode mode))
            {
                options.PartitionMode = mode;
            }
            else
            {
                options.AddConfigurationFailure(
                    nameof(PlatformRateLimitOptions.PartitionMode),
                    $"must be {nameof(PlatformRateLimitPartitionMode.SharedIngress)} " +
                    $"or {nameof(PlatformRateLimitPartitionMode.ForwardedClient)}");
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

    /// <summary>
    /// Only the two names are accepted. Enum parsing on its own would take a
    /// number, and a partition declared as <c>0</c> is not a declaration.
    /// </summary>
    private static bool TryParseMode(
        string value,
        out PlatformRateLimitPartitionMode mode)
    {
        foreach (PlatformRateLimitPartitionMode candidate in
            Enum.GetValues<PlatformRateLimitPartitionMode>())
        {
            if (string.Equals(
                    value,
                    candidate.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
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

        bool raised = false;
        foreach (PlatformRateLimitBucket bucket in PlatformRateLimitBuckets.All)
        {
            int limit = options.LimitFor(bucket);
            if (limit is < PlatformRateLimitOptions.MinimumLimit
                or > PlatformRateLimitOptions.MaximumLimit)
            {
                failures.Add(PlatformRateLimitOptions.Failure(
                    PlatformRateLimitBuckets.Setting(bucket),
                    "must be between one and one million requests per window"));
                continue;
            }

            raised |= limit > PlatformRateLimitBuckets.ShippedLimit(bucket);
        }

        // Raising a bucket is the moment the deployment has to say whose
        // requests it counts. Lowering one is always safe, and an untouched
        // deployment keeps the per-client behaviour it already had.
        if (raised && options.PartitionMode is null)
        {
            failures.Add(PlatformRateLimitOptions.Failure(
                nameof(PlatformRateLimitOptions.PartitionMode),
                $"must be declared as " +
                $"{nameof(PlatformRateLimitPartitionMode.SharedIngress)} or " +
                $"{nameof(PlatformRateLimitPartitionMode.ForwardedClient)} " +
                "before a bucket is raised above the limit it ships with"));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Checks the declared partition against the rest of the deployment.
///
/// A deployment that declares a shared ingress but trusts a proxy would hand
/// the shared allowance to every client, and one whose framework limiter
/// cannot admit a persisted bucket has raised a ceiling nothing will ever
/// reach - which is exactly the misconfiguration that made a hosted
/// deployment unusable while looking correctly configured.
/// </summary>
internal sealed class PlatformRateLimitCouplingValidator(
    IOptions<VistaraSecurityOptions> security) :
    IValidateOptions<PlatformRateLimitOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        PlatformRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode != PlatformRateLimitPartitionMode.SharedIngress)
        {
            return ValidateOptionsResult.Success;
        }

        VistaraSecurityOptions configured = security.Value;
        List<string> failures = [];
        if (configured.Proxy.KnownProxies.Count > 0 ||
            configured.Proxy.KnownNetworks.Count > 0)
        {
            failures.Add(PlatformRateLimitOptions.Failure(
                nameof(PlatformRateLimitOptions.PartitionMode),
                $"cannot be {nameof(PlatformRateLimitPartitionMode.SharedIngress)} " +
                "while Security:Proxy trusts a proxy: a trusted forwarded header " +
                "makes the peer a client, and each bucket a per-client budget"));
        }

        // The framework limiter counts the same shared peer, so a persisted
        // bucket it cannot admit is a ceiling the deployment never reaches.
        // Only the paths that limiter governs are compared: it never sees a
        // media request.
        SecurityLimitOptions limits = configured.Limits;
        if (limits.RequestsPerWindow >= PlatformRateLimitOptions.MinimumLimit &&
            limits.RateLimitWindow > TimeSpan.Zero &&
            options.Window > TimeSpan.Zero)
        {
            foreach (PlatformRateLimitBucket bucket in PlatformRateLimitBuckets.All)
            {
                if (!SecurityRequestClassifier.IsRateLimitedPath(
                        PlatformRateLimitBuckets.RepresentativePath(bucket)))
                {
                    continue;
                }

                int limit = options.LimitFor(bucket);
                if (limit is < PlatformRateLimitOptions.MinimumLimit
                    or > PlatformRateLimitOptions.MaximumLimit)
                {
                    continue;
                }

                // frameworkPermits / frameworkWindow >= limit / window, as
                // whole numbers so no rate is rounded into passing.
                if ((long)limits.RequestsPerWindow * options.Window.Ticks <
                    (long)limit * limits.RateLimitWindow.Ticks)
                {
                    failures.Add(PlatformRateLimitOptions.Failure(
                        PlatformRateLimitBuckets.Setting(bucket),
                        "is above the rate Security:Limits:RequestsPerWindow and " +
                        "Security:Limits:RateLimitWindow admit, and behind a " +
                        "shared ingress the framework limiter counts the same peer"));
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
