using System.Collections.Concurrent;
using Portal.Common.Abstractions;

namespace Portal.Common;

public class AttemptTracker : IAttemptTracker
{
    private class AttemptRecord
    {
        public int FailedCount { get; set; }
        public DateTime FirstFailureTime { get; set; }
    }

    private readonly ConcurrentDictionary<string, AttemptRecord> _records = new();
    private readonly int _maxAttempts;
    private readonly TimeSpan _lockoutDuration;

    public AttemptTracker(int maxAttempts = 5, int lockoutMinutes = 5)
    {
        _maxAttempts = maxAttempts;
        _lockoutDuration = TimeSpan.FromMinutes(lockoutMinutes);
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

    public void RecordFailure(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        var now = DateTime.UtcNow;
        _records.AddOrUpdate(id,
            _ => new AttemptRecord { FailedCount = 1, FirstFailureTime = now },
            (_, record) =>
            {
                // If it's been longer than lockout duration since first failure, reset the counter
                if (now - record.FirstFailureTime >= _lockoutDuration)
                {
                    return new AttemptRecord { FailedCount = 1, FirstFailureTime = now };
                }

                record.FailedCount++;
                return record;
            });
    }

    public void RecordSuccess(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _records.TryRemove(id, out _);
    }
}
