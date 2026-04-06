using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace Portal.Common;

/// <summary>
/// Bluetooth RFCOMM protocol: length-prefixed JSON messages over a Stream.
/// Designed to be transport-agnostic — works with any Stream (RFCOMM, SslStream, etc.)
/// </summary>
public static class BtProtocol
{
    /// <summary>PortalWin RFCOMM Service UUID (shared across all projects).</summary>
    public static readonly Guid ServiceUuid = new("E0CBF06C-CD8B-4647-BB8A-263B43F0F974");

    /// <summary>SDP Service Name advertised via Bluetooth.</summary>
    public const string SdpServiceName = "PortalWin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ConditionalWeakTable<Stream, SemaphoreSlim> _writeLocks = new();

    public static async Task SendMessageAsync(Stream stream, object message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, message.GetType(), JsonOptions);
        var payload = Encoding.UTF8.GetBytes(json);
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));

        var lck = _writeLocks.GetValue(stream, _ => new SemaphoreSlim(1, 1));
        await lck.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(lengthBytes, 0, 4, ct);
            await stream.WriteAsync(payload, 0, payload.Length, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            lck.Release();
        }
    }

    public static async Task<T?> ReceiveMessageAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes, 4, ct);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes, 0));

        if (length <= 0 || length > 1024 * 64)
            throw new InvalidDataException($"Invalid message length: {length}");

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, length, ct);
        var json = Encoding.UTF8.GetString(payload);

        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public static async Task<BtMessage?> ReceiveRawMessageAsync(Stream stream, CancellationToken ct = default)
    {
        var lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes, 4, ct);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes, 0));

        if (length <= 0 || length > 1024 * 64)
            throw new InvalidDataException($"Invalid message length: {length}");

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, length, ct);
        var json = Encoding.UTF8.GetString(payload);

        var raw = JsonSerializer.Deserialize<BtMessage>(json, JsonOptions);
        if (raw == null) return null;

        return raw.Type switch
        {
            "pair_request" => JsonSerializer.Deserialize<BtPairRequest>(json, JsonOptions),
            "pair_response" => JsonSerializer.Deserialize<BtPairResponse>(json, JsonOptions),
            "register" => JsonSerializer.Deserialize<BtRegisterMessage>(json, JsonOptions),
            "unlock_request" => JsonSerializer.Deserialize<BtUnlockRequest>(json, JsonOptions),
            "unlock_response" => JsonSerializer.Deserialize<BtUnlockResponse>(json, JsonOptions),
            "host_unlock_request" => JsonSerializer.Deserialize<BtHostUnlockRequest>(json, JsonOptions),
            "host_unlock_response" => JsonSerializer.Deserialize<BtHostUnlockResponse>(json, JsonOptions),
            _ => raw
        };
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer, offset, count - offset, ct);
            if (read == 0) throw new IOException("Connection closed during read.");
            offset += read;
        }
    }
}
