using Vistara.Application.Sharing;

namespace Vistara.Auth.Sharing;

public sealed class InMemoryShareChallengeRateLimiter : IShareChallengeRateLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows =
        new(StringComparer.Ordinal);

    public ValueTask<ShareRateLimitDecision> TryAcquireAsync(
        string keyHash,
        DateTimeOffset nowUtc,
        TimeSpan window,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        if (keyHash.Length != 64 ||
            keyHash.Any(character => !Uri.IsHexDigit(character)) ||
            nowUtc.Offset != TimeSpan.Zero ||
            window <= TimeSpan.Zero ||
            limit < 1)
        {
            throw new ArgumentException("The share rate-limit request is invalid.");
        }

        lock (_gate)
        {
            foreach (string expiredKey in _windows
                         .Where(item =>
                             nowUtc >= item.Value.StartedAtUtc.Add(window))
                         .Select(item => item.Key)
                         .ToArray())
            {
                _windows.Remove(expiredKey);
            }

            if (!_windows.TryGetValue(keyHash, out Window? current) ||
                nowUtc >= current.StartedAtUtc.Add(window))
            {
                _windows[keyHash] = new Window(nowUtc, 1);
                return ValueTask.FromResult(
                    new ShareRateLimitDecision(true, null));
            }

            if (current.Count >= limit)
            {
                return ValueTask.FromResult(
                    new ShareRateLimitDecision(
                        false,
                        current.StartedAtUtc.Add(window) - nowUtc));
            }

            _windows[keyHash] = current with { Count = current.Count + 1 };
            return ValueTask.FromResult(
                new ShareRateLimitDecision(true, null));
        }
    }

    private sealed record Window(
        DateTimeOffset StartedAtUtc,
        int Count);
}
