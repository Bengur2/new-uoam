namespace NewUOAM.Positioning.PacketProxy;

/// <summary>
/// Accumulates raw bytes observed on one direction of a TCP connection and yields complete,
/// correctly-framed packets using <see cref="UoPacketLengths"/>.
///
/// Purely observational in intent - callers that just want a read-only side-channel (position
/// extraction on the game hop) forward the original raw bytes themselves and ignore what this
/// returns entirely. Callers that need to reconstruct the stream from what <see cref="Feed"/>
/// returns (the login hop's one-packet rewrite) can safely do so: every byte ever fed in is
/// guaranteed to come back out through some call's returned list, in order, either as a properly
/// framed packet or - once an unknown packet ID makes further framing unsafe - as an opaque
/// "everything from here on, verbatim" chunk, so concatenating <c>Feed</c>'s outputs always
/// reproduces the exact input stream. Framing failure only ever costs *this walker's own*
/// packet-level visibility, never bytes.
/// </summary>
public sealed class PacketStreamWalker
{
    private readonly List<byte> _buffer = new();

    public bool GaveUp { get; private set; }

    /// <summary>The packet ID that made framing impossible (either genuinely unknown, or a
    /// variable-length packet with a nonsensical declared length) - null if never given up, or if
    /// this instance simply never received any data. Diagnostic only.</summary>
    public byte? GaveUpOnPacketId { get; private set; }

    /// <summary>How many packets this instance has successfully framed so far. Diagnostic only -
    /// e.g. "0 after several seconds of traffic" points at framing never getting off the ground,
    /// vs. "framed a handful then gave up" pointing at one specific unexpected packet.</summary>
    public int FramedPacketCount { get; private set; }

    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        var packets = new List<byte[]>();

        if (GaveUp)
        {
            if (data.Length > 0) packets.Add(data.ToArray());
            return packets;
        }

        _buffer.AddRange(data.ToArray());

        while (true)
        {
            if (_buffer.Count < 1) break;
            byte id = _buffer[0];
            int declaredLen = UoPacketLengths.Lengths[id];

            int totalLen;
            if (declaredLen > 0)
            {
                totalLen = declaredLen;
            }
            else if (declaredLen < 0)
            {
                if (_buffer.Count < 3) break; // need the 2-byte big-endian length prefix
                totalLen = (_buffer[1] << 8) | _buffer[2];
                if (totalLen < 3)
                {
                    GaveUpOnPacketId = id;
                    GiveUp(packets);
                    break;
                }
            }
            else
            {
                GaveUpOnPacketId = id;
                GiveUp(packets); // unknown packet ID - can't safely determine its length
                break;
            }

            if (_buffer.Count < totalLen) break; // wait for more bytes

            var packet = _buffer.GetRange(0, totalLen).ToArray();
            _buffer.RemoveRange(0, totalLen);
            packets.Add(packet);
            FramedPacketCount++;
        }

        return packets;
    }

    /// <summary>Flushes whatever is still buffered (unparseable from here on) as one final
    /// pass-through entry, then disables framing for all future <see cref="Feed"/> calls.</summary>
    private void GiveUp(List<byte[]> packets)
    {
        GaveUp = true;
        if (_buffer.Count > 0) packets.Add(_buffer.ToArray());
        _buffer.Clear();
    }
}
