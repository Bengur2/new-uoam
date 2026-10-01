namespace NewUOAM.Positioning.PacketProxy;

public readonly record struct EnterWorldInfo(uint Serial, int X, int Y, int Z, int MapWidthTiles, int MapHeightTiles);

/// <summary>
/// Stateful protocol-level position tracker fed with already-framed packets (see
/// <see cref="PacketStreamWalker"/>) from both directions of one game-server connection.
///
/// UO's classic movement protocol is CLIENT-AUTHORITATIVE: a normal accepted step
/// (0x02 Move Request -> 0x22 Move Ack) carries no coordinates at all, only a direction and a
/// sequence number - the server trusts the client's own dead reckoning unless it actively
/// rejects the move (0x21, which *does* carry the authoritative snap-back position). So this
/// class has to dead-reckon exactly like a real client would: remember the direction for each
/// in-flight sequence number from 0x02, and when it's acked, step the tracked position by that
/// direction's (dx, dy). 0x1B (Enter World) seeds the initial absolute position; 0x21 and 0x20
/// (when it targets our own serial) resync it.
/// </summary>
public sealed class PositionExtractor
{
    private static readonly (int Dx, int Dy)[] DirectionDeltas =
    {
        (0, -1),  // 0 North
        (1, -1),  // 1 North-East
        (1, 0),   // 2 East
        (1, 1),   // 3 South-East
        (0, 1),   // 4 South
        (-1, 1),  // 5 South-West
        (-1, 0),  // 6 West
        (-1, -1), // 7 North-West
    };

    private readonly Dictionary<byte, int> _pendingMoveDirections = new();

    public uint? PlayerSerial { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Z { get; private set; }
    public bool HasPosition { get; private set; }

    public event Action<EnterWorldInfo>? EnterWorld;
    public event Action<int, int, int>? PositionChanged; // x, y, z

    public void OnServerPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0) return;

        switch (packet[0])
        {
            case UoPacketLengths.EnterWorld when packet.Length >= 0x25:
                HandleEnterWorld(packet);
                break;

            case UoPacketLengths.MovementReject when packet.Length >= 8:
                HandleMovementReject(packet);
                break;

            case UoPacketLengths.MovementAck when packet.Length >= 3:
                HandleMovementAck(packet);
                break;

            case UoPacketLengths.MobileUpdate when packet.Length >= 0x13:
                HandleMobileUpdate(packet);
                break;
        }
    }

    public void OnClientPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length >= 7 && packet[0] == UoPacketLengths.MovementRequest)
        {
            byte direction = (byte)(packet[1] & 0x07); // low 3 bits; high bits are run-flag etc.
            byte sequence = packet[2];
            _pendingMoveDirections[sequence] = direction;
        }
    }

    private void HandleEnterWorld(ReadOnlySpan<byte> p)
    {
        // Layout (ServUO LoginConfirm / ClassicUO 0x1B, 37 bytes total):
        // [0] id, [1..4] serial, [5..8] unused, [9..10] body, [11..12] X, [13..14] Y, [15..16] Z,
        // [17] direction, [18] unused, [19..22] -1, [23..24] unused, [25..26] unused,
        // [27..28] map width, [29..30] map height, then padding to 37.
        // NOTE: unlike 0x20/0x21 below, Z here is a full 2-byte big-endian short, not a single
        // sbyte (ServUO writes `(short)m.Z` for this packet specifically).
        uint serial = ReadUInt32BE(p[1..5]);
        int x = ReadInt16BE(p[11..13]);
        int y = ReadInt16BE(p[13..15]);
        int z = ReadInt16BE(p[15..17]);
        int mapWidth = ReadInt16BE(p[27..29]);
        int mapHeight = ReadInt16BE(p[29..31]);

        PlayerSerial = serial;
        X = x; Y = y; Z = z;
        HasPosition = true;
        _pendingMoveDirections.Clear();

        EnterWorld?.Invoke(new EnterWorldInfo(serial, x, y, z, mapWidth, mapHeight));
        PositionChanged?.Invoke(x, y, z);
    }

    private void HandleMovementReject(ReadOnlySpan<byte> p)
    {
        // [0] id, [1] seq, [2..3] X, [4..5] Y, [6] direction, [7] Z (sbyte)
        if (!HasPosition) return;
        X = ReadInt16BE(p[2..4]);
        Y = ReadInt16BE(p[4..6]);
        Z = (sbyte)p[7];
        PositionChanged?.Invoke(X, Y, Z);
    }

    private void HandleMovementAck(ReadOnlySpan<byte> p)
    {
        // [0] id, [1] seq, [2] notoriety - no coordinates; dead-reckon from the matching 0x02.
        if (!HasPosition) return;
        byte seq = p[1];
        if (!_pendingMoveDirections.Remove(seq, out int direction)) return;

        var (dx, dy) = DirectionDeltas[direction];
        X += dx; Y += dy;
        PositionChanged?.Invoke(X, Y, Z);
    }

    private void HandleMobileUpdate(ReadOnlySpan<byte> p)
    {
        // [0] id, [1..4] serial, [5..6] body, [7] unused, [8..9] hue, [10] flags,
        // [11..12] X, [13..14] Y, [15..16] unused, [17] direction, [18] Z (sbyte)
        if (PlayerSerial is null) return;
        uint serial = ReadUInt32BE(p[1..5]);
        if (serial != PlayerSerial.Value) return; // some other mobile, not us

        X = ReadInt16BE(p[11..13]);
        Y = ReadInt16BE(p[13..15]);
        Z = (sbyte)p[18];
        PositionChanged?.Invoke(X, Y, Z);
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> b) => (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    private static int ReadInt16BE(ReadOnlySpan<byte> b) => (short)((b[0] << 8) | b[1]);
}
