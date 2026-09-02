using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Vistara.Persistence.Auth;

internal sealed class RateLimitWindowRow
{
    public string KeyHash { get; set; } = string.Empty;
    public DateTimeOffset WindowStartedAtUtc { get; set; }
    public int RequestCount { get; set; }
    public long Version { get; set; }
}

internal static class RateLimitPersistenceContributor
{
    internal static void Configure(ModelBuilder modelBuilder) =>
        Configure(modelBuilder.Entity<RateLimitWindowRow>());

    internal static void Configure(
        Microsoft.EntityFrameworkCore.Metadata.Builders
            .EntityTypeBuilder<RateLimitWindowRow> entity)
    {
        entity.ToTable("rate_limit_windows", table =>
        {
            table.HasCheckConstraint(
                "ck_rate_limit_windows_count",
                "\"request_count\" > 0");
            table.HasCheckConstraint(
                "ck_rate_limit_windows_version",
                "\"version\" >= 1");
        });
        entity.HasKey(row => row.KeyHash);
        entity.Property(row => row.KeyHash).HasMaxLength(64);
        entity.Property(row => row.Version).IsConcurrencyToken();
    }
}

public sealed class RateLimitCatalogDbContext(
    DbContextOptions<RateLimitCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<RateLimitWindowRow> Windows => Set<RateLimitWindowRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        RateLimitPersistenceContributor.Configure(
            modelBuilder.Entity<RateLimitWindowRow>());
        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        foreach (var property in modelBuilder.Entity<RateLimitWindowRow>()
                     .Metadata.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
            if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(converter);
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}

public sealed record PersistedRateLimitDecision(
    bool IsAllowed,
    TimeSpan? RetryAfter);

/// <summary>
/// The deployment-wide request counter. Behind a shared ingress this is the
/// hottest row in the database: every replica counts every request into the
/// same key, so the increment cannot be a read, a decision, and a write.
///
/// Each step is one conditional statement the database evaluates against the
/// row it holds a lock on, which is what makes the limit exact under
/// contention. Reading the row first and writing it back would either admit
/// more than the limit or turn an ordinary race into a failed request, and a
/// caller that is being counted is not the caller that should be told the
/// deployment is broken.
/// </summary>
public sealed class RelationalRateLimitStore(
    RateLimitCatalogDbContext context)
{
    /// <summary>
    /// Attempts are only spent on a row appearing, elapsing, or being reset
    /// underneath this caller, and on database contention. The ordinary path
    /// resolves in the first statement.
    /// </summary>
    private const int MaximumAttempts = 8;

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(2);

    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMilliseconds(50);

    public async ValueTask<PersistedRateLimitDecision> TryAcquireAsync(
        string keyHash,
        DateTimeOffset nowUtc,
        TimeSpan window,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        if (keyHash.Length != 64 ||
            keyHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Rate-limit keys must be SHA-256 digests.",
                nameof(keyHash));
        }

        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Rate-limit timestamps must use UTC.",
                nameof(nowUtc));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            window,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // A window that started at or before this instant has elapsed. Both
        // conditions below are expressed against it so the database, and not
        // this process, decides which window a request belongs to.
        DateTimeOffset elapsedAtOrBefore = nowUtc - window;
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PersistedRateLimitDecision? decision = await AttemptAsync(
                    keyHash,
                    nowUtc,
                    window,
                    limit,
                    elapsedAtOrBefore,
                    cancellationToken);
                if (decision is not null)
                {
                    return decision;
                }
            }
            catch (Exception failure) when (
                !cancellationToken.IsCancellationRequested &&
                attempt + 1 < MaximumAttempts &&
                RelationalFaultClassifier.IsContentionOrConstraint(failure))
            {
                context.ChangeTracker.Clear();
            }

            if (attempt + 1 >= MaximumAttempts)
            {
                // Every attempt was lost to another writer or to database
                // contention. Refusing the request is the honest answer: the
                // count could not be established, and a limiter that fails
                // open is not a limiter.
                return new PersistedRateLimitDecision(false, window);
            }

            await Task.Delay(Backoff(attempt), cancellationToken);
        }
    }

    /// <summary>
    /// One attempt. Returns null when the row moved underneath it and the
    /// decision has to be made again.
    /// </summary>
    private async ValueTask<PersistedRateLimitDecision?> AttemptAsync(
        string keyHash,
        DateTimeOffset nowUtc,
        TimeSpan window,
        int limit,
        DateTimeOffset elapsedAtOrBefore,
        CancellationToken cancellationToken)
    {
        // Count one request into a live window that is still under its limit.
        // The database re-evaluates both conditions while it holds the row, so
        // exactly `limit` of these succeed however many replicas race.
        int counted = await context.Windows
            .Where(row =>
                row.KeyHash == keyHash &&
                row.WindowStartedAtUtc > elapsedAtOrBefore &&
                row.RequestCount < limit)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.RequestCount, row => row.RequestCount + 1)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);
        if (counted > 0)
        {
            return new PersistedRateLimitDecision(true, null);
        }

        // Start the next window. The condition stops being true the moment the
        // first writer commits, so a burst that arrives together on an elapsed
        // window resets it once between them and counts the rest.
        int restarted = await context.Windows
            .Where(row =>
                row.KeyHash == keyHash &&
                row.WindowStartedAtUtc <= elapsedAtOrBefore)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.WindowStartedAtUtc, nowUtc)
                    .SetProperty(row => row.RequestCount, 1)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);
        if (restarted > 0)
        {
            return new PersistedRateLimitDecision(true, null);
        }

        // Neither statement matched, so either there is no window yet or the
        // live one is full.
        var snapshot = await context.Windows
            .AsNoTracking()
            .Where(row => row.KeyHash == keyHash)
            .Select(row => new
            {
                row.WindowStartedAtUtc,
                row.RequestCount,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null)
        {
            context.ChangeTracker.Clear();
            context.Windows.Add(new RateLimitWindowRow
            {
                KeyHash = keyHash,
                WindowStartedAtUtc = nowUtc,
                RequestCount = 1,
                Version = 1,
            });
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            return new PersistedRateLimitDecision(true, null);
        }

        DateTimeOffset windowEnd = snapshot.WindowStartedAtUtc.Add(window);
        return snapshot.WindowStartedAtUtc > elapsedAtOrBefore &&
            snapshot.RequestCount >= limit
                ? new PersistedRateLimitDecision(false, windowEnd - nowUtc)
                : null;
    }

    /// <summary>
    /// Bounded backoff with jitter. A lost attempt waits rather than spinning
    /// on the row every other writer is already queued behind.
    /// </summary>
    private static TimeSpan Backoff(int attempt)
    {
        double milliseconds = Math.Min(
            FirstBackoff.TotalMilliseconds * Math.Pow(2, attempt),
            MaximumBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(
            milliseconds * Random.Shared.NextDouble());
    }
}
