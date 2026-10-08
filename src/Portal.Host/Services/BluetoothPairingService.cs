using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Portal.Common;
using Portal.Common.Helpers;
using Portal.Host.Models;

namespace Portal.Host.Services;

/// <summary>
/// RFCOMM server for Bluetooth pairing.
/// Advertises a PortalWin service, accepts incoming connections,
/// validates pairing code, and registers new trusted devices.
/// </summary>
public class BluetoothPairingService : IDisposable
{
    /// <summary>
    /// Brute-force protection: Tracks failed PIN attempts per Bluetooth remote address.
    /// Locks out devices after 5 failed attempts for 2 minutes.
    /// </summary>
    private static readonly AttemptTracker _attemptTracker = new(maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(2));

    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private PortalWinConfig? _config;
    private PairingContext? _pairingContext;
    private Action<string>? _statusCallback;
    private TaskCompletionSource<PairingResult?>? _pairingTcs;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Resets brute-force attempt counters for all Bluetooth remotes.
    /// </summary>
    public static void ResetAttemptTracker() => _attemptTracker.ResetAll();



    /// <summary>
    /// Start the RFCOMM listener for pairing.
    /// </summary>
    public async Task StartAsync(PortalWinConfig config, PairingContext pairingContext, Action<string> statusCallback, CancellationToken ct)
    {
        if (IsRunning) return;

        _config = config;
        _pairingContext = pairingContext;
        _statusCallback = statusCallback;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pairingTcs = new TaskCompletionSource<PairingResult?>();

        // Cancel triggers completion
        _cts.Token.Register(() => _pairingTcs.TrySetCanceled());

        try
        {
            _provider = await RfcommServiceProvider.CreateAsync(
                RfcommServiceId.FromUuid(BtProtocol.ServiceUuid));

            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;

            await _listener.BindServiceNameAsync(
                _provider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionWithAuthentication);

            // Set SDP attributes
            InitSdpAttributes(_provider, "pair");

            _provider.StartAdvertising(_listener, true);

            IsRunning = true;
            _statusCallback?.Invoke("Bluetooth pairing service started. Waiting for device...");
            Logger.Log("[BtPairing] RFCOMM listener started and advertising.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[BtPairing] Failed to start RFCOMM listener", ex);
            _statusCallback?.Invoke($"Bluetooth error: {ex.Message}");
            _pairingTcs.TrySetResult(null);
        }
    }

    /// <summary>
    /// Wait for a device to pair or for cancellation.
    /// </summary>
    public Task<PairingResult?> WaitForPairingAsync()
    {
        return _pairingTcs?.Task ?? Task.FromResult<PairingResult?>(null);
    }

    /// <summary>
    /// Stop the RFCOMM listener.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _provider?.StopAdvertising();
        _listener?.Dispose();
        _listener = null;
        _provider = null;
        IsRunning = false;
        Logger.Log("[BtPairing] RFCOMM listener stopped.");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        var socket = args.Socket;
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleConnectionReceivedAsync(socket);
            }
            catch (Exception ex)
            {
                Logger.LogError("[BtPairing] Unhandled error in background connection handler", ex);
                try { socket.Dispose(); } catch { }
            }
        });
    }

    private async Task HandleConnectionReceivedAsync(StreamSocket socket)
    {
        Logger.Log("[BtPairing] Incoming RFCOMM connection.");
        _statusCallback?.Invoke("Device connecting via Bluetooth...");

        try
        {
            using (socket)
            {
                var stream = socket.InputStream.AsStreamForRead();
                var outStream = socket.OutputStream.AsStreamForWrite();
                var combinedStream = new BtDuplexStream(stream, outStream);

                var device = await ValidateCodeAndRegister(combinedStream, socket);
                if (device != null)
                {
                    await BtProtocol.SendMessageAsync(combinedStream,
                        new BtPairResponse { Success = true, ClientId = device.ClientId },
                        _cts?.Token ?? CancellationToken.None);

                    Logger.Log($"[BtPairing] Pairing successful! Device: {device.Name} ({device.ClientId}) via BT");
                    _statusCallback?.Invoke($"Paired: {device.Name}");

                    _pairingTcs?.TrySetResult(new PairingResult { Device = device, Success = true });
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log("[BtPairing] Connection handling cancelled.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[BtPairing] Error handling connection", ex);
            _statusCallback?.Invoke($"Error: {ex.Message}");
        }
    }

    private async Task<Portal.Common.Models.BluetoothDeviceModel?> ValidateCodeAndRegister(BtDuplexStream combinedStream, StreamSocket socket)
    {
        var socketAddr = socket.Information.RemoteHostName?.DisplayName ?? "";
        var trackingKey = !string.IsNullOrWhiteSpace(socketAddr)
            ? PortalWinConfig.NormalizeBluetoothAddress(socketAddr)
            : "unknown_bt_device";

        _statusCallback?.Invoke($"Connected: {socketAddr}. Verifying code...");

        // 1. Check Brute-Force Lockout
        if (_attemptTracker.IsBlocked(trackingKey))
        {
            var remaining = _attemptTracker.GetRemainingLockout(trackingKey);
            var remainingSec = remaining.HasValue ? (int)Math.Max(1, Math.Ceiling(remaining.Value.TotalSeconds)) : 120;
            Logger.LogWarning($"[BtPairing] Brute force block: remote device {socketAddr} (Key={trackingKey}) is locked out for {remainingSec}s.");

            await BtProtocol.SendMessageAsync(combinedStream,
                new BtPairResponse { Success = false, Error = $"Too many failed attempts. Try again in {remainingSec} seconds." },
                _cts?.Token ?? CancellationToken.None);

            _statusCallback?.Invoke($"Device {socketAddr} temporarily blocked (too many attempts).");
            return null;
        }

        var msg = await BtProtocol.ReceiveRawMessageAsync(combinedStream, _cts?.Token ?? CancellationToken.None);

        if (msg is BtPairRequest pairRequest)
        {
            var expectedCode = _pairingContext?.PairingCode ?? string.Empty;
            var providedCode = pairRequest.Code ?? string.Empty;

            var expectedBytes = Encoding.UTF8.GetBytes(expectedCode);
            var providedBytes = Encoding.UTF8.GetBytes(providedCode);

            var isCodeValid = expectedBytes.Length > 0 &&
                              expectedBytes.Length == providedBytes.Length &&
                              CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);

            if (_pairingContext == null || !isCodeValid)
            {
                _attemptTracker.RecordFailure(trackingKey);
                var isNowBlocked = _attemptTracker.IsBlocked(trackingKey);

                Logger.LogWarning($"[BtPairing] Invalid pairing code attempt from {socketAddr} (Key={trackingKey}, Blocked={isNowBlocked})");

                // Thwart rapid automated guessing by introducing an intentional security delay
                try
                {
                    await Task.Delay(1000, _cts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException) { }

                var errorMsg = isNowBlocked
                    ? "Too many failed attempts. Device temporarily blocked."
                    : "Invalid code";

                await BtProtocol.SendMessageAsync(combinedStream,
                    new BtPairResponse { Success = false, Error = errorMsg },
                    _cts?.Token ?? CancellationToken.None);

                _statusCallback?.Invoke(isNowBlocked ? "Device temporarily blocked." : "Invalid pairing code.");
                return null;
            }

            // Successful code validation: clear failure history for this device
            _attemptTracker.RecordSuccess(trackingKey);

            var clientId = Guid.NewGuid().ToString();
            var btAddress = string.IsNullOrEmpty(socketAddr) ? "Unknown" : socketAddr;

            var device = new Portal.Common.Models.BluetoothDeviceModel
            {
                ClientId = clientId,
                Name = $"BT Device {_config!.Devices.Count + 1}",
                CertHash = "", // Not used for BT
                BluetoothAddress = btAddress,
                TransportType = TransportType.Bluetooth,
                PairedAt = DateTime.Now
            };

            if (!string.IsNullOrEmpty(_pairingContext.TargetUsername))
            {
                if (_config!.EnforceUniqueAccountPerTransport &&
                    _config.HasPairedAccountForTransport(_pairingContext.TargetUsername, _pairingContext.TargetDomain, TransportType.Bluetooth))
                {
                    Logger.LogWarning("[BtPairing] Pairing rejected: account already linked to another device.");
                    await BtProtocol.SendMessageAsync(combinedStream,
                        new BtPairResponse { Success = false, Error = "Account already paired" },
                        _cts?.Token ?? CancellationToken.None);
                    _statusCallback?.Invoke("Pairing rejected: account already linked.");
                    return null;
                }
                if (_config.EnforceUniqueAccountPerTransport &&
                    _config.EnforceUniqueAccountAcrossTransports &&
                    _config.HasPairedAccountOnOtherTransport(_pairingContext.TargetUsername, _pairingContext.TargetDomain, TransportType.Bluetooth))
                {
                    Logger.LogWarning("[BtPairing] Pairing rejected: account already linked on another transport.");
                    await BtProtocol.SendMessageAsync(combinedStream,
                        new BtPairResponse { Success = false, Error = "Account already paired on another transport" },
                        _cts?.Token ?? CancellationToken.None);
                    _statusCallback?.Invoke("Pairing rejected: account already linked on another transport.");
                    return null;
                }

                var newAccount = new Portal.Common.Models.DeviceAccount
                {
                    Username = _pairingContext.TargetUsername,
                    Domain = _pairingContext.TargetDomain ?? "",
                    UserSid = IdentityHelper.TryResolveUserSid(_pairingContext.TargetUsername, _pairingContext.TargetDomain)
                };
                newAccount.SetPassword(_pairingContext.TargetPassword);
                device.Accounts.Add(newAccount);
            }

            _config!.Devices.Add(device);
            _config.Save();
            ActivityJournal.Record("pairing", "🤝", "New device paired", $"{device.Name} is ready to unlock this PC via Bluetooth.", deviceName: device.Name, transport: "Bluetooth");

            return device;
        }

        Logger.LogWarning($"[BtPairing] Expected pair_request, got: {msg?.Type}");
        _statusCallback?.Invoke("Unexpected message from device.");
        return null;
    }



    private static void InitSdpAttributes(RfcommServiceProvider provider, string mode)
    {
        // Set SDP service name
        var writer = new DataWriter();
        writer.WriteByte(0x25); // UTF-8 string type
        writer.WriteString(BtProtocol.SdpServiceName);
        provider.SdpRawAttributes.Add(0x100, writer.DetachBuffer()); // ServiceName

        // Set mode attribute
        var modeWriter = new DataWriter();
        modeWriter.WriteByte(0x25);
        modeWriter.WriteString(mode);
        provider.SdpRawAttributes.Add(0x200, modeWriter.DetachBuffer()); // Mode
    }
}
