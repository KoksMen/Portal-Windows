using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Portal.Common.Abstractions;

namespace Portal.Common;

public class AttemptTracker : IAttemptTracker
{
    private sealed class AttemptRecord
    {
        public int FailedCount { get; init; }
        public DateTime FirstFailureTime { get; init; }
    }

    public const int DefaultMaxTrackedEntries = 1000;

    private readonly ConcurrentDictionary<string, AttemptRecord> _records = new();
    private readonly int _maxAttempts;
    private readonly TimeSpan _lockoutDuration;
    private readonly int _maxTrackedEntries;
    private int _pruningInProgress;

    public int TrackedEntriesCount => _records.Count;

    public AttemptTracker(int maxAttempts = 5, int lockoutMinutes = 5, int maxTrackedEntries = DefaultMaxTrackedEntries)
        : this(maxAttempts, TimeSpan.FromMinutes(lockoutMinutes), maxTrackedEntries)
    {
    }

    public AttemptTracker(int maxAttempts, TimeSpan lockoutDuration, int maxTrackedEntries = DefaultMaxTrackedEntries)
    {
        _maxAttempts = maxAttempts;
        _lockoutDuration = lockoutDuration;
        _maxTrackedEntries = maxTrackedEntries > 0 ? maxTrackedEntries : DefaultMaxTrackedEntries;
    }

    public bool IsBlocked(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        if (_records.TryGetValue(id, out var record))
        {
            if (record.FailedCount >= _maxAttempts)
            {
                if (DateTime.UtcNow - record.FirstFailureTime < _lockoutDuration)
                {
                    return true;
                }
                else
                {
                    // Lockout period has expired, reset
                    _records.TryRemove(id, out _);
                    return false;
                }
            }
        }
        return false;
    }

    public TimeSpan? GetRemainingLockout(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        if (_records.TryGetValue(id, out var record) && record.FailedCount >= _maxAttempts)
        {
            var elapsed = DateTime.UtcNow - record.FirstFailureTime;
            if (elapsed < _lockoutDuration)
            {
                return _lockoutDuration - elapsed;
            }

            _records.TryRemove(id, out _);
        }

        return null;
    }

    public void RecordFailure(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        var now = DateTime.UtcNow;
        PruneIfNeeded(now);

        _records.AddOrUpdate(id,
            _ => new AttemptRecord { FailedCount = 1, FirstFailureTime = now },
            (_, existing) =>
            {
                // If it's been longer than lockout duration since first failure, reset the counter
                if (now - existing.FirstFailureTime >= _lockoutDuration)
                {
                    return new AttemptRecord { FailedCount = 1, FirstFailureTime = now };
                }

                return new AttemptRecord
                {
                    FailedCount = existing.FailedCount + 1,
                    FirstFailureTime = existing.FirstFailureTime
                };
            });
    }

    private void PruneIfNeeded(DateTime now)
    {
        if (_records.Count < _maxTrackedEntries)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _pruningInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // 1. Evict expired entries
            foreach (var kvp in _records)
            {
                if (now - kvp.Value.FirstFailureTime >= _lockoutDuration)
                {
                    _records.TryRemove(kvp.Key, out _);
                }
            }

            // 2. If still over capacity, evict oldest entries down to 80% capacity
            if (_records.Count >= _maxTrackedEntries)
            {
                var targetCount = (int)(_maxTrackedEntries * 0.8);
                var toEvictCount = _records.Count - targetCount;
                if (toEvictCount > 0)
                {
                    var oldest = _records
                        .OrderBy(kvp => kvp.Value.FirstFailureTime)
                        .Take(toEvictCount)
                        .ToList();

                    foreach (var kvp in oldest)
                    {
                        _records.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }
        catch
        {
            // Pruning is opportunistic, never fail tracking operation
        }
        finally
        {
            Volatile.Write(ref _pruningInProgress, 0);
        }
    }

    public void RecordSuccess(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _records.TryRemove(id, out _);
    }

    public void ResetAll()
    {
        _records.Clear();
    }
}
