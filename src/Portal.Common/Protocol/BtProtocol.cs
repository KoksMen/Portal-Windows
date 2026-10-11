using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace Portal.Common;

/// <summary>
/// Bluetooth RFCOMM protocol: length-prefixed JSON messages over a Stream.
/// Optimized with ArrayPool and ReadOnlySpan for zero-allocation parsing.
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
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), JsonOptions);
        byte[] lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, payload.Length);

        var lck = _writeLocks.GetValue(stream, _ => new SemaphoreSlim(1, 1));
        await lck.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(lengthBytes.AsMemory(0, 4), ct);
            await stream.WriteAsync(payload.AsMemory(), ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            lck.Release();
        }
    }

    public static async Task<T?> ReceiveMessageAsync<T>(Stream stream, CancellationToken ct = default)
    {
        byte[] lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes.AsMemory(0, 4), ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

        if (length <= 0 || length > 1024 * 64)
            throw new InvalidDataException($"Invalid message length: {length}");

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactAsync(stream, rented.AsMemory(0, length), ct);
            return JsonSerializer.Deserialize<T>(rented.AsSpan(0, length), JsonOptions);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static async Task<BtMessage?> ReceiveRawMessageAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes.AsMemory(0, 4), ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

        if (length <= 0 || length > 1024 * 64)
            throw new InvalidDataException($"Invalid message length: {length}");

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactAsync(stream, rented.AsMemory(0, length), ct);
            ReadOnlySpan<byte> span = rented.AsSpan(0, length);

            var raw = JsonSerializer.Deserialize<BtMessage>(span, JsonOptions);
            if (raw == null) return null;

            return raw.Type switch
            {
                "pair_request" => JsonSerializer.Deserialize<BtPairRequest>(span, JsonOptions),
                "pair_response" => JsonSerializer.Deserialize<BtPairResponse>(span, JsonOptions),
                "register" => JsonSerializer.Deserialize<BtRegisterMessage>(span, JsonOptions),
                "unlock_request" => JsonSerializer.Deserialize<BtUnlockRequest>(span, JsonOptions),
                "unlock_response" => JsonSerializer.Deserialize<BtUnlockResponse>(span, JsonOptions),
                "host_unlock_request" => JsonSerializer.Deserialize<BtHostUnlockRequest>(span, JsonOptions),
                "host_unlock_response" => JsonSerializer.Deserialize<BtHostUnlockResponse>(span, JsonOptions),
                _ => raw
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        while (buffer.Length > 0)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) throw new IOException("Connection closed during read.");
            buffer = buffer.Slice(read);
        }
    }
}
