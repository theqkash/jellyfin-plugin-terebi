namespace Jellyfin.Plugin.Terebi.Streaming;

/// <summary>
/// Single-writer, many-reader ring buffer for live transport streams. Readers that fall behind by more
/// than the buffer size are moved forward to the oldest available byte (they see a discontinuity, which
/// a live TS player recovers from at the next key frame).
/// </summary>
public sealed class RingBuffer : IDisposable
{
    private readonly byte[] _data;
    private readonly object _gate = new();
    private long _written;
    private bool _completed;

    public RingBuffer(int capacity)
    {
        _data = new byte[capacity];
    }

    public int Capacity => _data.Length;

    public long Written
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    public bool Completed
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (bytes.Length >= _data.Length)
            {
                bytes = bytes[^_data.Length..];
            }

            var pos = (int)(_written % _data.Length);
            var first = Math.Min(bytes.Length, _data.Length - pos);
            bytes[..first].CopyTo(_data.AsSpan(pos));
            if (first < bytes.Length)
            {
                bytes[first..].CopyTo(_data.AsSpan(0));
            }

            _written += bytes.Length;
            Monitor.PulseAll(_gate);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Copies bytes starting at absolute position <paramref name="position"/> into <paramref name="destination"/>.
    /// Blocks (with timeout) until data is available. Returns 0 at end of stream, and updates the position.
    /// </summary>
    public int Read(ref long position, Span<byte> destination, TimeSpan timeout)
    {
        lock (_gate)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (position >= _written)
            {
                if (_completed)
                {
                    return 0;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining))
                {
                    return -1; // timed out, caller decides
                }
            }

            var oldest = Math.Max(0, _written - _data.Length);
            if (position < oldest)
            {
                position = oldest; // reader fell behind
            }

            var available = (int)Math.Min(_written - position, destination.Length);
            var pos = (int)(position % _data.Length);
            var first = Math.Min(available, _data.Length - pos);
            _data.AsSpan(pos, first).CopyTo(destination);
            if (first < available)
            {
                _data.AsSpan(0, available - first).CopyTo(destination[first..]);
            }

            position += available;
            return available;
        }
    }

    /// <summary>Position a new reader should start from: a little behind the head so the player gets data immediately.</summary>
    public long StartPosition()
    {
        lock (_gate)
        {
            var back = Math.Min(_written, Math.Min(_data.Length / 4, 2 * 1024 * 1024));
            var start = _written - back;
            // Align to a TS packet boundary when possible.
            return start - (start % 188);
        }
    }

    public void Dispose()
    {
        Complete();
    }
}

/// <summary>A read-only view over a <see cref="RingBuffer"/> handed to Jellyfin as the live stream.</summary>
public sealed class RingBufferReadStream : Stream
{
    private readonly RingBuffer _buffer;
    private readonly Func<bool> _isAlive;
    private readonly Action? _onRead;
    private long _position;

    public RingBufferReadStream(RingBuffer buffer, Func<bool> isAlive, Action? onRead = null)
    {
        _buffer = buffer;
        _isAlive = isAlive;
        _onRead = onRead;
        _position = buffer.StartPosition();
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            var n = _buffer.Read(ref _position, buffer, TimeSpan.FromSeconds(2));
            if (n >= 0)
            {
                if (n > 0)
                {
                    _onRead?.Invoke();
                }

                return n;
            }

            if (!_isAlive())
            {
                return 0;
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.Run(() => Read(buffer.AsSpan(offset, count)), cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => Read(buffer.Span), cancellationToken));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
