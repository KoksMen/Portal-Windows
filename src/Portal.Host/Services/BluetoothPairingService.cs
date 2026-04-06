using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Portal.Common;
using Portal.Host.Models;

namespace Portal.Host.Services;

/// <summary>
/// RFCOMM server for Bluetooth pairing.
/// Advertises a PortalWin service, accepts incoming connections,
/// validates pairing code, and registers new trusted devices.
/// </summary>
public class BluetoothPairingService : IDisposable
{
    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private PortalWinConfig? _config;
    private PairingContext? _pairingContext;
    private Action<string>? _statusCallback;
    private TaskCompletionSource<PairingResult?>? _pairingTcs;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }



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

    private async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        Logger.Log("[BtPairing] Incoming RFCOMM connection.");
        _statusCallback?.Invoke("Device connecting via Bluetooth...");

        try
        {
            using var socket = args.Socket;
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
        _statusCallback?.Invoke($"Connected: {socketAddr}. Verifying code...");
        var msg = await BtProtocol.ReceiveRawMessageAsync(combinedStream, _cts?.Token ?? CancellationToken.None);

        if (msg is BtPairRequest pairRequest)
        {
            if (_pairingContext == null || pairRequest.Code != _pairingContext.PairingCode)
            {
                Logger.LogWarning($"[BtPairing] Invalid pairing code: {pairRequest.Code}");
                await BtProtocol.SendMessageAsync(combinedStream,
                    new BtPairResponse { Success = false, Error = "Invalid code" },
                    _cts?.Token ?? CancellationToken.None);
                _statusCallback?.Invoke("Invalid pairing code.");
                return null;
            }

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
                    Domain = _pairingContext.TargetDomain ?? ""
                };
                newAccount.SetPassword(_pairingContext.TargetPassword);
                device.Accounts.Add(newAccount);
            }

            _config!.Devices.Add(device);
            _config.Save();

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
