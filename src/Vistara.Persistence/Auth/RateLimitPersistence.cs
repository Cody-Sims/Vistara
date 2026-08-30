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

public sealed class RelationalRateLimitStore(
    RateLimitCatalogDbContext context)
{
    private const int MaximumAttempts = 3;

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
        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            RateLimitWindowRow? row = await context.Windows
                .SingleOrDefaultAsync(
                    candidate => candidate.KeyHash == keyHash,
                    cancellationToken);
            if (row is null)
            {
                context.Windows.Add(new RateLimitWindowRow
                {
                    KeyHash = keyHash,
                    WindowStartedAtUtc = nowUtc,
                    RequestCount = 1,
                    Version = 1,
                });
            }
            else
            {
                DateTimeOffset windowEnd = row.WindowStartedAtUtc.Add(window);
                if (nowUtc >= windowEnd)
                {
                    row.WindowStartedAtUtc = nowUtc;
                    row.RequestCount = 1;
                    row.Version = checked(row.Version + 1);
                }
                else if (row.RequestCount >= limit)
                {
                    return new PersistedRateLimitDecision(
                        false,
                        windowEnd - nowUtc);
                }
                else
                {
                    row.RequestCount = checked(row.RequestCount + 1);
                    row.Version = checked(row.Version + 1);
                }
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return new PersistedRateLimitDecision(true, null);
            }
            catch (DbUpdateException) when (
                attempt + 1 < MaximumAttempts)
            {
                context.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException(
            "The rate-limit window could not be updated atomically.");
    }
}
