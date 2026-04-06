using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Portal.Common;

namespace Portal.CredentialProvider;

public class BluetoothUnlockService : IDisposable
{
    private sealed class PendingApprovalContext
    {
        public required string RequestId { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public required TaskCompletionSource<string?> Completion { get; init; }
    }

    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private CancellationTokenSource? _cts;

    private readonly ConcurrentDictionary<string, (Stream stream, StreamSocket socket)> _persistentConnections = new();
    private readonly ConcurrentDictionary<string, PendingApprovalContext> _pendingApprovals = new();
    private readonly AttemptTracker _attemptTracker = new();

    public bool IsRunning { get; private set; }
    public string? StartupError { get; private set; }

    public event Action<string, SecureString?, string>? UnlockRequested;
    public event Action<string, bool>? BtConnectionChanged;
    public event Action? StatusChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            _provider = await RfcommServiceProvider.CreateAsync(
                RfcommServiceId.FromUuid(BtProtocol.ServiceUuid));
            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;

            await _listener.BindServiceNameAsync(
                _provider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionWithAuthentication);

            InitSdpAttributes(_provider, "locked");
            _provider.StartAdvertising(_listener, true);

            IsRunning = true;
            StartupError = null;
            Logger.Log("[BtUnlock] RFCOMM unlock service started and advertising.");
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
            Logger.LogError("[BtUnlock] Failed to start RFCOMM unlock service", ex);
            StatusChanged?.Invoke();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _provider?.StopAdvertising();
        _listener?.Dispose();
        _listener = null;
        _provider = null;

        DisconnectAllClients("Bluetooth service stop");

        IsRunning = false;
        StatusChanged?.Invoke();
        Logger.Log("[BtUnlock] RFCOMM unlock service stopped.");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    public bool IsClientConnected(string clientId)
    {
        return _persistentConnections.ContainsKey(clientId);
    }

    public int GetConnectedClientCount()
    {
        return _persistentConnections.Count;
    }

    public void ResetPendingApprovals(string reason)
    {
        var snapshot = _pendingApprovals.ToArray();
        foreach (var (clientId, tcs) in snapshot)
        {
            if (_pendingApprovals.TryRemove(clientId, out var removed))
            {
                removed.Completion.TrySetResult(null);
            }
        }

        if (snapshot.Length > 0)
        {
            Logger.Log($"[BtUnlock] Pending approvals reset: {snapshot.Length}. Reason: {reason}");
        }
    }

    public void DisconnectAllClients(string reason)
    {
        ResetPendingApprovals(reason);

        var snapshot = _persistentConnections.ToArray();
        foreach (var (clientId, connection) in snapshot)
        {
            _persistentConnections.TryRemove(clientId, out _);
            try { connection.socket.Dispose(); } catch { }
            BtConnectionChanged?.Invoke(clientId, false);
            StatusChanged?.Invoke();
            Logger.Log($"[BtUnlock] BT client disconnected (global): {clientId}. Reason: {reason}");
        }
    }

    public void UpdateState(string state)
    {
        if (_provider == null) return;
        try
        {
            _provider.StopAdvertising();
            InitSdpAttributes(_provider, state);
            _provider.StartAdvertising(_listener, true);
            Logger.Log($"[BtUnlock] SDP state updated: {state}");

            var msg = new { type = "state_update", state = state };
            foreach (var conn in _persistentConnections.Values)
            {
                _ = BtProtocol.SendMessageAsync(conn.stream, msg, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[BtUnlock] Failed to update SDP state: {ex.Message}");
        }
    }

    public async Task<string?> RequestUnlockFromClientAsync(string clientId, string? requestId, bool correlationEnabled, CancellationToken ct)
    {
        var configuredDevice = PortalWinConfig.Load().FindDeviceByClientId(clientId);
        if (configuredDevice != null && !configuredDevice.IsEnabled)
        {
            Logger.LogWarning($"[BtUnlock] Host-initiated unlock request blocked for disabled device: {configuredDevice.IdsSafe()}");
            return null;
        }

        if (!_persistentConnections.TryGetValue(clientId, out var conn))
        {
            Logger.LogWarning($"[BtUnlock] No persistent BT connection for client: {clientId}");
            return null;
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
        _pendingApprovals[clientId] = pending;

        try
        {
            var request = new BtHostUnlockRequest
            {
                ClientId = clientId,
                RequestId = effectiveRequestId
            };
            await BtProtocol.SendMessageAsync(conn.stream, request, ct);
            Logger.Log($"[BtUnlock] Sent host-initiated unlock request to client: {clientId} requestId='{effectiveRequestId ?? "none"}' correlationEnabled={correlationEnabled}");

            using (ct.Register(() => pending.Completion.TrySetResult(null)))
            {
                var responseStatus = await pending.Completion.Task;
                if (responseStatus == "ok")
                {
                    Logger.Log($"[BtUnlock] Host-Initiated unlock approved by client: {clientId} requestId='{effectiveRequestId ?? "none"}'");
                    return "ok";
                }
                else if (responseStatus == "rejected" || responseStatus == "forbidden")
                {
                    Logger.LogWarning($"[BtUnlock] Host-Initiated unlock rejected by client: {clientId} requestId='{effectiveRequestId ?? "none"}'");
                    return "rejected";
                }
                else
                {
                    Logger.LogWarning($"[BtUnlock] Host-Initiated unlock unknown or null status from client: {clientId} requestId='{effectiveRequestId ?? "none"}'");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[BtUnlock] Error requesting unlock from client: {clientId}", ex);
            _persistentConnections.TryRemove(clientId, out _);
            try { conn.socket.Dispose(); } catch { }
            return null;
        }
        finally
        {
            _pendingApprovals.TryRemove(clientId, out _);
        }
    }

    private void UnregisterPersistentConnections(IEnumerable<string> clientIds, StreamSocket socket)
    {
        var anyChanged = false;
        foreach (var clientId in clientIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_persistentConnections.TryGetValue(clientId, out var conn) && conn.socket == socket)
            {
                _persistentConnections.TryRemove(clientId, out _);
                BtConnectionChanged?.Invoke(clientId, false);
                anyChanged = true;
                Logger.Log($"[BtUnlock] BT connection unregistered: {clientId}");
            }
        }

        if (anyChanged)
        {
            StatusChanged?.Invoke();
        }
    }

    private async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        Logger.Log("[BtUnlock] Incoming RFCOMM connection.");
        var rawBtAddr = args.Socket.Information.RemoteHostName?.DisplayName ?? "Unknown BT";

        if (_attemptTracker.IsBlocked(rawBtAddr))
        {
            Logger.LogWarning($"[BtUnlock] Dropping connection from {rawBtAddr}: Too many failed attempts.");
            args.Socket.Dispose();
            return;
        }

        try
        {
            var socket = args.Socket;
            var readStream = socket.InputStream.AsStreamForRead();
            var writeStream = socket.OutputStream.AsStreamForWrite();
            var stream = new BtDuplexStream(readStream, writeStream);

            var config = PortalWinConfig.Load();
            var matchedDevices = config.Devices
                .OfType<Portal.Common.Models.BluetoothDeviceModel>()
                .Where(d =>
                    d.IsEnabled &&
                    string.Equals(
                        PortalWinConfig.NormalizeBluetoothAddress(d.BluetoothAddress),
                        PortalWinConfig.NormalizeBluetoothAddress(rawBtAddr),
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchedDevices.Count == 0)
            {
                Logger.LogWarning($"[BtUnlock] Unknown device MAC: {rawBtAddr}. Closing silently.");
                socket.Dispose();
                return;
            }

            var primaryDevice = matchedDevices[0];
            var mappedClientIds = matchedDevices
                .Select(d => d.ClientId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _attemptTracker.RecordSuccess(rawBtAddr);
            Logger.Log($"[BtUnlock] Trusted BT device connected: mac={rawBtAddr}, mappedClientIds={string.Join(", ", mappedClientIds)}");

            BtMessage? firstMsg = null;
            try
            {
                using var idCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
                idCts.CancelAfter(TimeSpan.FromSeconds(2));
                firstMsg = await BtProtocol.ReceiveRawMessageAsync(stream, idCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.LogWarning($"[BtUnlock] ID error from {primaryDevice.Name}: {ex.Message}"); }

            if (firstMsg is BtUnlockRequest unlockReq)
            {
                var unlockDevice = ResolveDeviceForUnlockRequest(matchedDevices, unlockReq.ClientId);
                Logger.Log($"[BtUnlock] Client-Initiated unlock from: {unlockDevice.IdsSafe()}");
                var account = unlockDevice.Accounts.FirstOrDefault();
                using var securePassword = account?.GetDecryptedSecurePassword();

                if (account != null && securePassword != null && securePassword.Length > 0)
                {
                    await BtProtocol.SendMessageAsync(stream,
                        new BtUnlockResponse { Success = true }, _cts?.Token ?? CancellationToken.None);
                    UnlockRequested?.Invoke(account.Username, securePassword, account.Domain);
                }
                else
                {
                    await BtProtocol.SendMessageAsync(stream,
                        new BtUnlockResponse { Success = false, Error = "No credentials found" },
                        _cts?.Token ?? CancellationToken.None);
                }
                socket.Dispose();
            }
            else
            {
                foreach (var clientId in mappedClientIds)
                {
                    _persistentConnections[clientId] = (stream, socket);
                    BtConnectionChanged?.Invoke(clientId, true);
                }
                StatusChanged?.Invoke();
                Logger.Log($"[BtUnlock] Registered BT connection aliases: {string.Join(", ", mappedClientIds)}");

                try
                {
                    if (firstMsg is BtHostUnlockResponse initialResp)
                    {
                        TryAcceptHostUnlockResponseForAliases(mappedClientIds, initialResp);
                    }

                    while (!_cts?.IsCancellationRequested ?? true)
                    {
                        var incomingMsg = await BtProtocol.ReceiveRawMessageAsync(stream, _cts?.Token ?? CancellationToken.None);
                        if (incomingMsg == null) break;

                        if (incomingMsg is BtUnlockRequest incomingUnlockReq)
                        {
                            var unlockDevice = ResolveDeviceForUnlockRequest(matchedDevices, incomingUnlockReq.ClientId);
                            Logger.Log($"[BtUnlock] Client-Initiated unlock from persistent connection: {unlockDevice.IdsSafe()}");
                            var account = unlockDevice.Accounts.FirstOrDefault();
                            using var securePassword = account?.GetDecryptedSecurePassword();

                            if (account != null && securePassword != null && securePassword.Length > 0)
                            {
                                await BtProtocol.SendMessageAsync(stream,
                                    new BtUnlockResponse { Success = true }, _cts?.Token ?? CancellationToken.None);
                                UnlockRequested?.Invoke(account.Username, securePassword, account.Domain);
                            }
                            else
                            {
                                await BtProtocol.SendMessageAsync(stream,
                                    new BtUnlockResponse { Success = false, Error = "No credentials found" },
                                    _cts?.Token ?? CancellationToken.None);
                            }
                        }
                        else if (incomingMsg is BtHostUnlockResponse incomingResp)
                        {
                            TryAcceptHostUnlockResponseForAliases(mappedClientIds, incomingResp);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.LogError($"[BtUnlock] Persistent BT connection loop error for aliases: {string.Join(", ", mappedClientIds)}", ex);
                }
                finally
                {
                    UnregisterPersistentConnections(mappedClientIds, socket);
                    try { socket.Dispose(); } catch { }
                    Logger.Log($"[BtUnlock] BT connection closed for aliases: {string.Join(", ", mappedClientIds)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log("[BtUnlock] Connection handling cancelled.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[BtUnlock] Error handling connection", ex);
        }
    }

    private static void InitSdpAttributes(RfcommServiceProvider provider, string mode)
    {
        provider.SdpRawAttributes.Remove(0x100);
        provider.SdpRawAttributes.Remove(0x200);

        var writer = new DataWriter();
        writer.WriteByte(0x25);
        writer.WriteString(BtProtocol.SdpServiceName);
        provider.SdpRawAttributes.Add(0x100, writer.DetachBuffer());

        var modeWriter = new DataWriter();
        modeWriter.WriteByte(0x25);
        modeWriter.WriteString(mode);
        provider.SdpRawAttributes.Add(0x200, modeWriter.DetachBuffer());
    }

    private void TryAcceptHostUnlockResponse(string clientId, BtHostUnlockResponse response)
    {
        if (!_pendingApprovals.TryGetValue(clientId, out var pending))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.RequestId) && !string.IsNullOrWhiteSpace(response.RequestId))
        {
            if (!string.Equals(response.RequestId, pending.RequestId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning($"[BtUnlock] Ignored stale host_unlock_response for {clientId}: requestId mismatch. expected='{pending.RequestId}' got='{response.RequestId}'.");
                return;
            }

            pending.Completion.TrySetResult(response.Status);
            Logger.Log($"[BtUnlock] host_unlock_response accepted for {clientId} by requestId='{pending.RequestId}'.");
            return;
        }

        if (string.IsNullOrWhiteSpace(pending.RequestId))
        {
            pending.Completion.TrySetResult(response.Status);
            Logger.Log($"[BtUnlock] host_unlock_response accepted for {clientId} in legacy mode without correlation.");
            return;
        }

        var ageMs = (DateTime.UtcNow - pending.CreatedAtUtc).TotalMilliseconds;
        if (ageMs < 400)
        {
            Logger.LogWarning($"[BtUnlock] Ignored legacy host_unlock_response without requestId for {clientId}: too early ({ageMs:0}ms), probable stale tail.");
            return;
        }

        pending.Completion.TrySetResult(response.Status);
        Logger.LogWarning($"[BtUnlock] Accepted legacy host_unlock_response without requestId for {clientId}. expectedRequestId='{pending.RequestId}', ageMs={ageMs:0}.");
    }

    private static Portal.Common.Models.BluetoothDeviceModel ResolveDeviceForUnlockRequest(
        IReadOnlyList<Portal.Common.Models.BluetoothDeviceModel> candidates,
        string? requestedClientId)
    {
        if (!string.IsNullOrWhiteSpace(requestedClientId))
        {
            var exact = candidates.FirstOrDefault(d =>
                string.Equals(d.ClientId, requestedClientId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }
        }

        return candidates[0];
    }

    private void TryAcceptHostUnlockResponseForAliases(
        IReadOnlyList<string> aliasClientIds,
        BtHostUnlockResponse response)
    {
        foreach (var clientId in aliasClientIds)
        {
            TryAcceptHostUnlockResponse(clientId, response);
        }
    }
}
