using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Portal.Common;

namespace Portal.CredentialProvider.Services;

public class WebSocketConnectionManager
{
    private sealed class PendingApprovalContext
    {
        public required string RequestId { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public required TaskCompletionSource<string?> Completion { get; init; }
    }

    private readonly ConcurrentDictionary<string, WebSocket> _connectedClients = new();
    private readonly ConcurrentDictionary<string, PendingApprovalContext> _pendingApprovals = new();

    public void RegisterClient(string clientId, WebSocket ws)
    {
        if (_connectedClients.TryGetValue(clientId, out var existing) && !ReferenceEquals(existing, ws))
        {
            _pendingApprovals.TryRemove(clientId, out var replacedPending);
            replacedPending?.Completion.TrySetResult(null);
            _ = CloseSocketFastAsync(existing, WebSocketCloseStatus.PolicyViolation, "Replaced by newer connection", CancellationToken.None);
            Logger.Log($"[WebSocketManager] Replaced active socket for client: {clientId}");
        }

        _connectedClients[clientId] = ws;
        Logger.Log($"[WebSocketManager] Client registered: {clientId}");
    }

    public bool IsClientConnected(string clientId)
    {
        return _connectedClients.TryGetValue(clientId, out var ws) && ws.State == WebSocketState.Open;
    }

    public int GetConnectedClientCount()
    {
        return _connectedClients.Count(x => x.Value.State == WebSocketState.Open);
    }

    public async Task<IReadOnlyList<string>> DisconnectClientsExceptAsync(
        HashSet<string> allowedClientIds,
        CancellationToken ct,
        string reason = "Blocked by selected tile policy")
    {
        var disconnected = new List<string>();
        var snapshot = _connectedClients.ToArray();

        foreach (var (clientId, socket) in snapshot)
        {
            if (allowedClientIds.Contains(clientId))
            {
                continue;
            }

            if (!_connectedClients.TryRemove(clientId, out var ws))
            {
                continue;
            }

            disconnected.Add(clientId);
            _pendingApprovals.TryRemove(clientId, out var pendingTcs);
            pendingTcs?.Completion.TrySetResult(null);

            await CloseSocketFastAsync(ws, WebSocketCloseStatus.PolicyViolation, reason, ct);

            Logger.Log($"[WebSocketManager] Client disconnected by WS policy: {clientId}");
        }

        return disconnected;
    }

    public async Task<IReadOnlyList<string>> DisconnectAllClientsAsync(
        string reason,
        CancellationToken ct)
    {
        var disconnected = new List<string>();
        var snapshot = _connectedClients.ToArray();

        foreach (var (clientId, _) in snapshot)
        {
            if (!_connectedClients.TryRemove(clientId, out var ws))
            {
                continue;
            }

            disconnected.Add(clientId);
            _pendingApprovals.TryRemove(clientId, out var pendingTcs);
            pendingTcs?.Completion.TrySetResult(null);

            await CloseSocketFastAsync(ws, WebSocketCloseStatus.NormalClosure, reason, ct);
            Logger.Log($"[WebSocketManager] Client disconnected (global): {clientId}. Reason: {reason}");
        }

        return disconnected;
    }

    public void ResetPendingApprovals(string reason)
    {
        var snapshot = _pendingApprovals.ToArray();
        foreach (var (clientId, pending) in snapshot)
        {
            if (_pendingApprovals.TryRemove(clientId, out var removed))
            {
                removed.Completion.TrySetResult(null);
            }
        }

        if (snapshot.Length > 0)
        {
            Logger.Log($"[WebSocketManager] Pending approvals reset: {snapshot.Length}. Reason: {reason}");
        }
    }

    public void UnregisterClient(string clientId, WebSocket ws)
    {
        if (_connectedClients.TryGetValue(clientId, out var existing) && existing == ws)
        {
            _connectedClients.TryRemove(clientId, out _);
            Logger.Log($"[WebSocketManager] Client unregistered: {clientId}");
        }
        try { ws.Dispose(); } catch { }
    }

    private static async Task CloseSocketFastAsync(
        WebSocket ws,
        WebSocketCloseStatus closeStatus,
        string reason,
        CancellationToken ct)
    {
        _ = closeStatus;
        _ = reason;
        _ = ct;

        try
        {
            if (ws.State == WebSocketState.Open ||
                ws.State == WebSocketState.CloseReceived ||
                ws.State == WebSocketState.CloseSent)
            {
                ws.Abort();
            }
        }
        catch
        {
            // Best-effort hard disconnect.
        }
        finally
        {
            try { ws.Dispose(); } catch { }
        }

        await Task.CompletedTask;
    }

    public async Task<string?> RequestUnlockFromClientAsync(
        Portal.Common.Models.DeviceModel device,
        string? requestId,
        bool correlationEnabled,
        CancellationToken ct)
    {
        if (!_connectedClients.TryGetValue(device.ClientId, out var ws) || ws.State != WebSocketState.Open)
        {
            Logger.Log($"[WebSocketManager] Client not connected via WebSocket: {device.Name}");
            return null;
        }

        if (_pendingApprovals.TryGetValue(device.ClientId, out var existingPending))
        {
            Logger.LogWarning($"[WebSocketManager] unlock_request already pending for {device.Name}; waiting for existing response.");
            return await WaitForPendingApprovalAsync(existingPending.Completion, ct);
        }

        var effectiveRequestId = correlationEnabled
            ? (string.IsNullOrWhiteSpace(requestId)
                ? Guid.NewGuid().ToString("N")
                : requestId.Trim())
            : null;
        var pending = new PendingApprovalContext
        {
            RequestId = effectiveRequestId ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow,
            Completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        _pendingApprovals[device.ClientId] = pending;

        try
        {
            var requestMsg = new WsMessage("unlock_request", device.ClientId, null, effectiveRequestId);
            var json = JsonSerializer.Serialize(requestMsg);
            var buffer = Encoding.UTF8.GetBytes(json);

            await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, ct);
            Logger.Log($"[WebSocketManager] Sent unlock_request to {device.Name} requestId='{effectiveRequestId ?? "none"}' correlationEnabled={correlationEnabled}");

            return await WaitForPendingApprovalAsync(pending.Completion, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogWarning($"[WebSocketManager] unlock_request cancelled for {device.Name}. requestId='{effectiveRequestId ?? "none"}' correlationEnabled={correlationEnabled}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[WebSocketManager] RequestUnlock failed for {device.Name}", ex);
            return null;
        }
        finally
        {
            _pendingApprovals.TryRemove(device.ClientId, out _);
        }
    }

    private static async Task<string?> WaitForPendingApprovalAsync(
        TaskCompletionSource<string?> tcs,
        CancellationToken ct)
    {
        try
        {
            return await tcs.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task BroadcastStateUpdate(string state)
    {
        var msg = new { Type = "state_update", State = state };
        var json = JsonSerializer.Serialize(msg);
        var buffer = Encoding.UTF8.GetBytes(json);

        foreach (var ws in _connectedClients.Values)
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            }
        }
    }

    public async Task HandleWebSocketAsync(WebSocket ws, string clientId)
    {
        while (ws.State == WebSocketState.Open)
        {
            try
            {
                var (messageType, json) = await ReceiveTextMessageAsync(ws, CancellationToken.None);
                if (messageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                    break;
                }

                if (messageType == WebSocketMessageType.Text && !string.IsNullOrWhiteSpace(json))
                {
                    var msg = JsonSerializer.Deserialize<WsMessage>(json);
                    Logger.Log($"[WebSocketManager] Received WS message from {clientId}: type='{msg?.Type ?? "null"}' status='{msg?.Status ?? "null"}' requestId='{msg?.RequestId ?? "null"}'");

                    if (msg?.Type == "unlock_response")
                    {
                        if (_pendingApprovals.TryGetValue(clientId, out var pending))
                        {
                            if (!string.IsNullOrWhiteSpace(pending.RequestId) && !string.IsNullOrWhiteSpace(msg.RequestId))
                            {
                                if (!string.Equals(msg.RequestId, pending.RequestId, StringComparison.OrdinalIgnoreCase))
                                {
                                    Logger.LogWarning($"[WebSocketManager] Ignored stale unlock_response for {clientId}: requestId mismatch. expected='{pending.RequestId}' got='{msg.RequestId}'.");
                                    continue;
                                }

                                pending.Completion.TrySetResult(msg.Status);
                                Logger.Log($"[WebSocketManager] unlock_response accepted for {clientId} by requestId='{pending.RequestId}'.");
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(pending.RequestId))
                            {
                                pending.Completion.TrySetResult(msg.Status);
                                Logger.Log($"[WebSocketManager] unlock_response accepted for {clientId} in legacy mode without correlation.");
                                continue;
                            }

                            var ageMs = (DateTime.UtcNow - pending.CreatedAtUtc).TotalMilliseconds;
                            if (ageMs < 400)
                            {
                                Logger.LogWarning($"[WebSocketManager] Ignored legacy unlock_response without requestId for {clientId}: too early ({ageMs:0}ms), probable stale tail.");
                                continue;
                            }

                            pending.Completion.TrySetResult(msg.Status);
                            Logger.LogWarning($"[WebSocketManager] Accepted legacy unlock_response without requestId for {clientId}. expectedRequestId='{pending.RequestId}', ageMs={ageMs:0}.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[WebSocketManager] Connection lost for {clientId}: {ex.Message}");
                break;
            }
        }

        if (_pendingApprovals.TryGetValue(clientId, out var pendingTcs))
        {
            pendingTcs.Completion.TrySetResult(null);
        }
    }

    private static async Task<(WebSocketMessageType MessageType, string? Text)> ReceiveTextMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, null);

            if (result.Count > 0)
                ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Text)
                    return (result.MessageType, null);

                var payload = Encoding.UTF8.GetString(ms.ToArray());
                return (WebSocketMessageType.Text, payload);
            }
        }
    }
}

