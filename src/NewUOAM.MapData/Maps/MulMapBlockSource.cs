namespace NewUOAM.MapData.Maps;

/// <summary>Reads land blocks straight out of a flat legacy mapN.mul file.</summary>
public sealed class MulMapBlockSource : IMapBlockSource
{
    public const int BlockRecordSize = 196; // 4-byte header + 64 * (2-byte tile id + 1-byte Z)

    private readonly FileStream _stream;

    public long BlockCount { get; }

    public MulMapBlockSource(string mulPath)
    {
        _stream = new FileStream(mulPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        BlockCount = _stream.Length / BlockRecordSize;
    }

    public void ReadBlockRaw(long blockIndex, Span<byte> destination)
    {
        if (destination.Length < BlockRecordSize)
            throw new ArgumentException($"Destination must be at least {BlockRecordSize} bytes.");

        long offset = blockIndex * BlockRecordSize;
        lock (_stream)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.ReadExactly(destination[..BlockRecordSize]);
        }
    }

    public void Dispose() => _stream.Dispose();
}
