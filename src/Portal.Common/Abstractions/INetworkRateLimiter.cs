using System;

namespace Portal.Common.Abstractions;

/// <summary>
/// Enforces dual-layer rate limiting (IP-level and Client-Certificate-level)
/// to prevent network flooding, DoS, and authentication brute-force attacks.
/// </summary>
public interface INetworkRateLimiter
{
    /// <summary>
    /// Attempts to acquire a request token for the given IP address.
    /// </summary>
    bool TryAcquireIp(string? ip, out int retryAfterSeconds);

    /// <summary>
    /// Checks whether the specified IP is currently banned due to excessive authentication failures.
    /// </summary>
    bool IsIpBanned(string? ip, out int retryAfterSeconds);

    /// <summary>
    /// Records an authentication failure (e.g. missing or unknown certificate) for the specified IP.
    /// </summary>
    void RecordAuthFailure(string? ip);

    /// <summary>
    /// Records a successful authentication, resetting failure counters for the specified IP.
    /// </summary>
    void RecordAuthSuccess(string? ip);

    /// <summary>
    /// Attempts to acquire an action token (e.g. "unlock", "ws_connect") for the specified client certificate hash.
    /// </summary>
    bool TryAcquireCertificate(string? certHash, string action, out int retryAfterSeconds);

    /// <summary>
    /// Resets tracking data for a specific IP or certificate hash.
    /// </summary>
    void Reset(string key);

    /// <summary>
    /// Resets all tracked rate limiting and ban entries.
    /// </summary>
    void ResetAll();

    /// <summary>
    /// Returns the total count of currently tracked entries across all rate-limit buckets.
    /// </summary>
    int TrackedEntriesCount { get; }
}
