using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Portal.Common.Abstractions;

namespace Portal.Common.Services;

/// <summary>
/// Configuration options for NetworkRateLimiter.
/// </summary>
public class NetworkRateLimiterOptions
{
    /// <summary>
    /// Maximum general requests allowed per IP address within the sliding window.
    /// Default: 20 requests.
    /// </summary>
    public int IpMaxRequests { get; set; } = 20;

    /// <summary>
    /// Sliding window duration for general IP rate limiting.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan IpWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Number of consecutive/frequent authentication failures within the window before triggering a temporary ban.
    /// Default: 5 failures.
    /// </summary>
    public int AuthFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Time window for tracking authentication failures per IP.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan AuthFailureWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Duration of the temporary IP ban when auth failure threshold is exceeded.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan AuthFailureBanDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum unlock requests allowed per client certificate within the sliding window.
    /// Default: 3 requests.
    /// </summary>
    public int UnlockMaxRequests { get; set; } = 3;

    /// <summary>
    /// Sliding window duration for certificate unlock requests.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan UnlockWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum WebSocket connection attempts allowed per client certificate within the sliding window.
    /// Default: 5 connections.
    /// </summary>
    public int WsConnectMaxRequests { get; set; } = 5;

    /// <summary>
    /// Sliding window duration for certificate WebSocket connections.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan WsConnectWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fallback maximum requests for unlisted actions per client certificate.
    /// Default: 10 requests.
    /// </summary>
    public int DefaultActionMaxRequests { get; set; } = 10;

    /// <summary>
    /// Fallback sliding window duration for unlisted actions.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan DefaultActionWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum number of tracked entries before triggering memory cap pruning.
    /// Default: 1000 entries.
    /// </summary>
    public int MaxTrackedEntries { get; set; } = NetworkRateLimiter.DefaultMaxTrackedEntries;
}

/// <summary>
/// Thread-safe rate limiter providing dual-layer protection (IP-level burst control + client cert action throttling)
/// and automatic temporary lockout for abusive IPs.
/// </summary>
public class NetworkRateLimiter : INetworkRateLimiter
{
    public const int DefaultMaxTrackedEntries = 1000;

    private sealed class RateLimitBucket
    {
        private readonly object _lock = new();
        private readonly Queue<long> _timestamps;
        private readonly int _maxRequests;
        private readonly long _windowTicks;
        public DateTime LastAccessedUtc { get; private set; }

        public RateLimitBucket(int maxRequests, TimeSpan window)
        {
            _maxRequests = Math.Max(1, maxRequests);
            _windowTicks = window.Ticks;
            _timestamps = new Queue<long>(_maxRequests);
            LastAccessedUtc = DateTime.UtcNow;
        }

        public bool TryAcquire(long nowTicks, out int retryAfterSeconds)
        {
            lock (_lock)
            {
                LastAccessedUtc = new DateTime(nowTicks, DateTimeKind.Utc);
                var cutoff = nowTicks - _windowTicks;

                while (_timestamps.Count > 0 && _timestamps.Peek() <= cutoff)
                {
                    _timestamps.Dequeue();
                }

                if (_timestamps.Count >= _maxRequests)
                {
                    var oldest = _timestamps.Peek();
                    var remainingTicks = (oldest + _windowTicks) - nowTicks;
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(TimeSpan.FromTicks(remainingTicks).TotalSeconds));
                    return false;
                }

                _timestamps.Enqueue(nowTicks);
                retryAfterSeconds = 0;
                return true;
            }
        }

        public bool IsExpired(long nowTicks)
        {
            lock (_lock)
            {
                var cutoff = nowTicks - _windowTicks;
                while (_timestamps.Count > 0 && _timestamps.Peek() <= cutoff)
                {
                    _timestamps.Dequeue();
                }
                return _timestamps.Count == 0;
            }
        }
    }

    private sealed class BanBucket
    {
        private readonly object _lock = new();
        private readonly Queue<long> _failureTimestamps;
        private readonly int _threshold;
        private readonly long _windowTicks;
        private readonly long _banDurationTicks;
        private long _bannedUntilTicks;
        public DateTime LastAccessedUtc { get; private set; }

        public BanBucket(int threshold, TimeSpan window, TimeSpan banDuration)
        {
            _threshold = Math.Max(1, threshold);
            _windowTicks = window.Ticks;
            _banDurationTicks = banDuration.Ticks;
            _failureTimestamps = new Queue<long>(_threshold);
            LastAccessedUtc = DateTime.UtcNow;
        }

        public bool IsBanned(long nowTicks, out int retryAfterSeconds)
        {
            lock (_lock)
            {
                LastAccessedUtc = new DateTime(nowTicks, DateTimeKind.Utc);
                if (_bannedUntilTicks > nowTicks)
                {
                    var remainingTicks = _bannedUntilTicks - nowTicks;
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(TimeSpan.FromTicks(remainingTicks).TotalSeconds));
                    return true;
                }

                retryAfterSeconds = 0;
                return false;
            }
        }

        public void RecordFailure(long nowTicks)
        {
            lock (_lock)
            {
                LastAccessedUtc = new DateTime(nowTicks, DateTimeKind.Utc);
                if (_bannedUntilTicks > nowTicks)
                {
                    return;
                }

                var cutoff = nowTicks - _windowTicks;
                while (_failureTimestamps.Count > 0 && _failureTimestamps.Peek() <= cutoff)
                {
                    _failureTimestamps.Dequeue();
                }

                _failureTimestamps.Enqueue(nowTicks);

                if (_failureTimestamps.Count >= _threshold)
                {
                    _bannedUntilTicks = nowTicks + _banDurationTicks;
                    _failureTimestamps.Clear();
                }
            }
        }

        public void RecordSuccess()
        {
            lock (_lock)
            {
                _failureTimestamps.Clear();
                _bannedUntilTicks = 0;
            }
        }

        public bool IsExpired(long nowTicks)
        {
            lock (_lock)
            {
                if (_bannedUntilTicks > nowTicks)
                {
                    return false;
                }

                var cutoff = nowTicks - _windowTicks;
                while (_failureTimestamps.Count > 0 && _failureTimestamps.Peek() <= cutoff)
                {
                    _failureTimestamps.Dequeue();
                }

                return _failureTimestamps.Count == 0;
            }
        }
    }

    private readonly NetworkRateLimiterOptions _options;
    private readonly ConcurrentDictionary<string, RateLimitBucket> _rateBuckets = new();
    private readonly ConcurrentDictionary<string, BanBucket> _banBuckets = new();
    private int _pruningInProgress;

    public int TrackedEntriesCount => _rateBuckets.Count + _banBuckets.Count;

    public NetworkRateLimiter()
        : this(new NetworkRateLimiterOptions())
    {
    }

    public NetworkRateLimiter(NetworkRateLimiterOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool TryAcquireIp(string? ip, out int retryAfterSeconds)
    {
        var normalizedIp = NormalizeIp(ip);
        var now = DateTime.UtcNow;
        PruneIfNeeded(now);

        var key = $"ip:{normalizedIp}";
        var bucket = _rateBuckets.GetOrAdd(key, _ => new RateLimitBucket(_options.IpMaxRequests, _options.IpWindow));
        return bucket.TryAcquire(now.Ticks, out retryAfterSeconds);
    }

    public bool IsIpBanned(string? ip, out int retryAfterSeconds)
    {
        var normalizedIp = NormalizeIp(ip);
        var key = $"ban:{normalizedIp}";
        if (_banBuckets.TryGetValue(key, out var bucket))
        {
            return bucket.IsBanned(DateTime.UtcNow.Ticks, out retryAfterSeconds);
        }

        retryAfterSeconds = 0;
        return false;
    }

    public void RecordAuthFailure(string? ip)
    {
        var normalizedIp = NormalizeIp(ip);
        var now = DateTime.UtcNow;
        PruneIfNeeded(now);

        var key = $"ban:{normalizedIp}";
        var bucket = _banBuckets.GetOrAdd(key, _ => new BanBucket(
            _options.AuthFailureThreshold,
            _options.AuthFailureWindow,
            _options.AuthFailureBanDuration));
        bucket.RecordFailure(now.Ticks);
    }

    public void RecordAuthSuccess(string? ip)
    {
        var normalizedIp = NormalizeIp(ip);
        var key = $"ban:{normalizedIp}";
        if (_banBuckets.TryGetValue(key, out var bucket))
        {
            bucket.RecordSuccess();
        }
    }

    public bool TryAcquireCertificate(string? certHash, string action, out int retryAfterSeconds)
    {
        if (string.IsNullOrWhiteSpace(certHash))
        {
            retryAfterSeconds = 0;
            return true;
        }

        var normalizedCert = NormalizeCertHash(certHash);
        var normalizedAction = string.IsNullOrWhiteSpace(action) ? "default" : action.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;
        PruneIfNeeded(now);

        var key = $"cert:{normalizedCert}:{normalizedAction}";
        var bucket = _rateBuckets.GetOrAdd(key, _ =>
        {
            var (max, win) = ResolveActionPolicy(normalizedAction);
            return new RateLimitBucket(max, win);
        });

        return bucket.TryAcquire(now.Ticks, out retryAfterSeconds);
    }

    public void Reset(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var trimmed = key.Trim();
        _rateBuckets.TryRemove(trimmed, out _);
        _banBuckets.TryRemove(trimmed, out _);

        var normalizedIp = NormalizeIp(trimmed);
        _rateBuckets.TryRemove($"ip:{normalizedIp}", out _);
        _banBuckets.TryRemove($"ban:{normalizedIp}", out _);

        var normalizedCert = NormalizeCertHash(trimmed);
        _rateBuckets.TryRemove($"cert:{normalizedCert}:unlock", out _);
        _rateBuckets.TryRemove($"cert:{normalizedCert}:ws_connect", out _);
    }

    public void ResetAll()
    {
        _rateBuckets.Clear();
        _banBuckets.Clear();
    }

    private (int MaxRequests, TimeSpan Window) ResolveActionPolicy(string action)
    {
        if (string.Equals(action, "unlock", StringComparison.OrdinalIgnoreCase))
        {
            return (_options.UnlockMaxRequests, _options.UnlockWindow);
        }

        if (string.Equals(action, "ws_connect", StringComparison.OrdinalIgnoreCase))
        {
            return (_options.WsConnectMaxRequests, _options.WsConnectWindow);
        }

        return (_options.DefaultActionMaxRequests, _options.DefaultActionWindow);
    }

    private void PruneIfNeeded(DateTime now)
    {
        var totalCount = _rateBuckets.Count + _banBuckets.Count;
        if (totalCount < _options.MaxTrackedEntries)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _pruningInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var nowTicks = now.Ticks;

            // 1. Evict expired entries
            foreach (var kvp in _rateBuckets)
            {
                if (kvp.Value.IsExpired(nowTicks))
                {
                    _rateBuckets.TryRemove(kvp.Key, out _);
                }
            }

            foreach (var kvp in _banBuckets)
            {
                if (kvp.Value.IsExpired(nowTicks))
                {
                    _banBuckets.TryRemove(kvp.Key, out _);
                }
            }

            // 2. If still over capacity, evict oldest entries down to 80% capacity
            var remainingTotal = _rateBuckets.Count + _banBuckets.Count;
            if (remainingTotal >= _options.MaxTrackedEntries)
            {
                var targetCount = (int)(_options.MaxTrackedEntries * 0.8);
                var toEvict = remainingTotal - targetCount;

                var oldest = _rateBuckets
                    .Select(k => (Key: k.Key, IsBan: false, Time: k.Value.LastAccessedUtc))
                    .Concat(_banBuckets.Select(k => (Key: k.Key, IsBan: true, Time: k.Value.LastAccessedUtc)))
                    .OrderBy(x => x.Time)
                    .Take(toEvict)
                    .ToList();

                foreach (var item in oldest)
                {
                    if (item.IsBan)
                    {
                        _banBuckets.TryRemove(item.Key, out _);
                    }
                    else
                    {
                        _rateBuckets.TryRemove(item.Key, out _);
                    }
                }
            }
        }
        catch
        {
            // Pruning is opportunistic, never fail operation
        }
        finally
        {
            Volatile.Write(ref _pruningInProgress, 0);
        }
    }

    private static string NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "unknown";
        var trimmed = ip.Trim();
        if (IPAddress.TryParse(trimmed, out var parsed))
        {
            if (parsed.IsIPv4MappedToIPv6)
            {
                return parsed.MapToIPv4().ToString();
            }
            return parsed.ToString();
        }
        return trimmed;
    }

    private static string NormalizeCertHash(string? certHash)
    {
        return certHash?.Trim().ToUpperInvariant() ?? string.Empty;
    }
}
