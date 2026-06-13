using System.Collections.Concurrent;
using EzOdata.Core.Time;

namespace EzOdata.Core.RateLimiting;

/// <summary>One resolved limit to enforce (spec 03 §2.10 / 08 §6).</summary>
public sealed record RateLimitPolicy(string ScopeKey, int WindowSeconds, int MaxRequests);

public sealed record RateLimitResult(bool Allowed, string? FailedScope, int RetryAfterSeconds, int Remaining);

/// <summary>
/// In-memory token bucket store (spec 08 §6): burst = maxRequests, refill =
/// maxRequests/window. A request must pass ALL applicable buckets; the strictest
/// failing policy is reported. Redis-backed distributed buckets land in Phase 8.
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly ISystemClock _clock;

    public TokenBucketRateLimiter(ISystemClock clock) => _clock = clock;

    public RateLimitResult Check(IReadOnlyList<RateLimitPolicy> policies)
    {
        if (policies.Count == 0)
        {
            return new RateLimitResult(true, null, 0, int.MaxValue);
        }

        var now = _clock.UtcNow;

        // Two passes: verify all buckets first so a denied request consumes no tokens.
        foreach (var policy in policies)
        {
            var bucket = _buckets.GetOrAdd(policy.ScopeKey, _ => new Bucket(policy.MaxRequests, now));
            if (!bucket.WouldAllow(policy, now, out var retryAfter))
            {
                return new RateLimitResult(false, policy.ScopeKey, retryAfter, 0);
            }
        }

        var minRemaining = int.MaxValue;
        foreach (var policy in policies)
        {
            var remaining = _buckets[policy.ScopeKey].Take(policy, now);
            minRemaining = Math.Min(minRemaining, remaining);
        }

        return new RateLimitResult(true, null, 0, minRemaining);
    }

    private sealed class Bucket
    {
        private readonly object _lock = new();
        private double _tokens;
        private DateTimeOffset _lastRefill;

        public Bucket(int capacity, DateTimeOffset now)
        {
            _tokens = capacity;
            _lastRefill = now;
        }

        public bool WouldAllow(RateLimitPolicy policy, DateTimeOffset now, out int retryAfterSeconds)
        {
            lock (_lock)
            {
                Refill(policy, now);
                if (_tokens >= 1)
                {
                    retryAfterSeconds = 0;
                    return true;
                }

                var refillPerSecond = (double)policy.MaxRequests / policy.WindowSeconds;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((1 - _tokens) / refillPerSecond));
                return false;
            }
        }

        public int Take(RateLimitPolicy policy, DateTimeOffset now)
        {
            lock (_lock)
            {
                Refill(policy, now);
                if (_tokens >= 1) _tokens -= 1;
                return (int)Math.Floor(_tokens);
            }
        }

        private void Refill(RateLimitPolicy policy, DateTimeOffset now)
        {
            var elapsed = (now - _lastRefill).TotalSeconds;
            if (elapsed <= 0) return;

            var refillPerSecond = (double)policy.MaxRequests / policy.WindowSeconds;
            _tokens = Math.Min(policy.MaxRequests, _tokens + elapsed * refillPerSecond);
            _lastRefill = now;
        }
    }
}
