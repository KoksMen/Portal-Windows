using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;

namespace Portal.Common.Helpers;

/// <summary>
/// High-throughput non-blocking asynchronous log sink built on System.Threading.Channels.
/// Acts as a ring-buffer: if disk I/O hangs or queue exceeds capacity, oldest events are dropped
/// without ever blocking or starving caller threads (LogonUI / unlock handlers).
/// </summary>
public sealed class ChannelRingBufferSink : ILogEventSink, IDisposable
{
    private readonly ILogEventSink _targetSink;
    private readonly Channel<LogEvent> _channel;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _isDisposed;

    public ChannelRingBufferSink(ILogEventSink targetSink, int boundedCapacity = 10_000)
    {
        _targetSink = targetSink ?? throw new ArgumentNullException(nameof(targetSink));
        var options = new BoundedChannelOptions(boundedCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<LogEvent>(options);
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null || _isDisposed) return;
        // Non-blocking write: if channel is full, BoundedChannelFullMode.DropOldest drops the oldest entry.
        _channel.Writer.TryWrite(logEvent);
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var logEvent))
                {
                    try
                    {
                        _targetSink.Emit(logEvent);
                    }
                    catch
                    {
                        // Avoid crashing background logger thread if underlying sink fails
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Drain remaining buffered events during cancellation/shutdown
            while (reader.TryRead(out var logEvent))
            {
                try
                {
                    _targetSink.Emit(logEvent);
                }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }

        _cts.Dispose();
        if (_targetSink is IDisposable disposableTarget)
        {
            try
            {
                disposableTarget.Dispose();
            }
            catch { }
        }
    }
}
