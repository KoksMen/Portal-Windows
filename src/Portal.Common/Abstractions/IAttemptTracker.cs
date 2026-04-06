namespace Portal.Common.Abstractions;

/// <summary>
/// Tracks failed authentication attempts and enforces lockout.
/// </summary>
public interface IAttemptTracker
{
    bool IsBlocked(string id);
    void RecordFailure(string id);
    void RecordSuccess(string id);
}
