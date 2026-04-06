using System.IO;

namespace Portal.Common;

/// <summary>
/// Wraps separate read/write streams from StreamSocket into a single duplex Stream
/// for use with BtProtocol (which expects a single Stream).
/// </summary>
public class BtDuplexStream : Stream
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;

    public BtDuplexStream(Stream readStream, Stream writeStream)
    {
        _readStream = readStream;
        _writeStream = writeStream;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        _readStream.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _readStream.ReadAsync(buffer, offset, count, ct);

    public override void Write(byte[] buffer, int offset, int count) =>
        _writeStream.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _writeStream.WriteAsync(buffer, offset, count, ct);

    public override void Flush() => _writeStream.Flush();
    public override Task FlushAsync(CancellationToken ct) => _writeStream.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _readStream.Dispose();
            _writeStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
