using Microsoft.EntityFrameworkCore;
using Vistara.Application.Sharing;

namespace Vistara.Persistence.Sharing;

public sealed class RelationalShareChallengeRateLimiter(
    SharingDbContext context) : IShareChallengeRateLimiter
{
    private const int MaximumAttempts = 3;
    private readonly SharingDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<ShareRateLimitDecision> TryAcquireAsync(
        string keyHash,
        DateTimeOffset nowUtc,
        TimeSpan window,
        int limit,
        CancellationToken cancellationToken)
    {
        if (keyHash.Length != 64 ||
            keyHash.Any(character => !Uri.IsHexDigit(character)) ||
            nowUtc.Offset != TimeSpan.Zero ||
            window <= TimeSpan.Zero ||
            limit < 1)
        {
            throw new ArgumentException("The share rate-limit request is invalid.");
        }

        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            await _context.RateLimits
                .Where(row => row.WindowStartedAtUtc < nowUtc.Subtract(window))
                .ExecuteDeleteAsync(cancellationToken);
            SharingRateLimitRow? row =
                await _context.RateLimits.SingleOrDefaultAsync(
                    candidate => candidate.KeyHash == keyHash,
                    cancellationToken);
            if (row is null)
            {
                _context.RateLimits.Add(new SharingRateLimitRow
                {
                    KeyHash = keyHash,
                    WindowStartedAtUtc = nowUtc,
                    RequestCount = 1,
                    Version = 1,
                });
            }
            else
            {
                DateTimeOffset windowEnd =
                    row.WindowStartedAtUtc.Add(window);
                if (nowUtc >= windowEnd)
                {
                    row.WindowStartedAtUtc = nowUtc;
                    row.RequestCount = 1;
                    row.Version = checked(row.Version + 1);
                }
                else if (row.RequestCount >= limit)
                {
                    return new(false, windowEnd - nowUtc);
                }
                else
                {
                    row.RequestCount = checked(row.RequestCount + 1);
                    row.Version = checked(row.Version + 1);
                }
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return new(true, null);
            }
            catch (DbUpdateException) when (attempt + 1 < MaximumAttempts)
            {
                _context.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException(
            "The share rate-limit window could not be updated atomically.");
    }
}
