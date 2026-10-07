using System;
using System.Collections.Concurrent;
using Portal.Common.Abstractions;

namespace Portal.Common;

public class AttemptTracker : IAttemptTracker
{
    private sealed class AttemptRecord
    {
        public int FailedCount { get; init; }
        public DateTime FirstFailureTime { get; init; }
    }

    private readonly ConcurrentDictionary<string, AttemptRecord> _records = new();
    private readonly int _maxAttempts;
    private readonly TimeSpan _lockoutDuration;

    public AttemptTracker(int maxAttempts = 5, int lockoutMinutes = 5)
        : this(maxAttempts, TimeSpan.FromMinutes(lockoutMinutes))
    {
    }

    public AttemptTracker(int maxAttempts, TimeSpan lockoutDuration)
    {
        _maxAttempts = maxAttempts;
        _lockoutDuration = lockoutDuration;
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
