namespace Process;

public static class StreamExtensions
{
    public static Stream CombineWrite(this Stream target1, Stream target2)
    {
        if (!target1.CanWrite || !target2.CanWrite)
        {
            throw new ArgumentException("Streams need to be writeable to combine them");
        }

        return new CombineWriteStream(target1, target2);
    }

    public static Stream InterceptStream(this Stream readStream, Stream track)
    {
        if (!readStream.CanRead || !track.CanWrite)
        {
            throw new ArgumentException(
                "track Stream need to be writeable and readStream readable to intercept the readStream.");
        }

        return new InterceptStream(readStream, track);
    }
}

public class InterceptStream : Stream
{
    private readonly Stream _readStream;
    private readonly Stream _track;

    public InterceptStream(Stream readStream, Stream track)
    {
        _readStream = readStream;
        _track = track;
    }
    
    public override void Flush()
    {
        _readStream.Flush(); _track.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _readStream.FlushAsync(cancellationToken);
        await _track.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _readStream.Read(buffer, offset, count);
        _track.Write(buffer, offset, read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _readStream.Read(buffer);
        _track.Write(buffer.Slice(0, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = new CancellationToken())
    {
        var read = await _readStream.ReadAsync(buffer, cancellationToken);
        await _track.WriteAsync(buffer.Slice(0, read), cancellationToken);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _readStream.ReadAsync(buffer, offset, count, cancellationToken);
        await _track.WriteAsync(buffer, offset, read, cancellationToken);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _readStream.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _readStream.DisposeAsync();
    }

    public override long Seek(long offset, SeekOrigin origin) => _readStream.Seek(offset, origin);

    public override void SetLength(long value) => _readStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _readStream.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _readStream.Write(buffer);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _readStream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = new CancellationToken())
    {
        await _readStream.WriteAsync(buffer, cancellationToken);
    }
    
    public override bool CanRead => true;
    public override bool CanSeek => _readStream.CanSeek;
    public override bool CanTimeout => _readStream.CanTimeout || _track.CanTimeout;
    
    public override bool CanWrite => _readStream.CanWrite;
    public override long Length => _readStream.Length;
    public override long Position { get => _readStream.Position; set => _readStream.Position = value; }

}

public class CombineWriteStream : Stream
{
    private readonly Stream _target1;
    private readonly Stream _target2;

    public CombineWriteStream(Stream target1, Stream target2)
    {
        _target1 = target1;
        _target2 = target2;
    }
    
    private Exception NotSupported() => new NotSupportedException("operation not supported");
    
    public override void Flush()
    {
        _target1.Flush(); _target2.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _target1.FlushAsync(cancellationToken);
        await _target2.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw NotSupported();

    public override long Seek(long offset, SeekOrigin origin) => throw NotSupported();

    public override void SetLength(long value) => throw NotSupported();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _target1.Write(buffer, offset, count);
        _target2.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _target1.Write(buffer);
        _target2.Write(buffer);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var start1 = _target1.WriteAsync(buffer, offset, count, cancellationToken);
        var start2 = _target2.WriteAsync(buffer, offset, count, cancellationToken);
        await start1;
        await start2;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = new CancellationToken())
    {
        var start1 = _target1.WriteAsync(buffer, cancellationToken);
        var start2 = _target2.WriteAsync(buffer, cancellationToken);
        await start1;
        await start2;
    }
    
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _target1.Length;

    public override long Position
    {
        get => _target1.Position;
        set => throw NotSupported();
    }
}

internal static class InternalStreams
{
    // https://github.com/fsprojects/FAKE/blob/release/next/src/app/Fake.Core.Process/InternalStreams.fs#L93
}