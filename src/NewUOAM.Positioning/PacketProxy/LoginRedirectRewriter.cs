using System.Net;

namespace NewUOAM.Positioning.PacketProxy;

/// <summary>
/// Watches the login hop's server-&gt;client bytes for the 0x8C (Play Server Ack) redirect
/// packet and rewrites its embedded game-server IP/port to point back at our own local listener.
///
/// Forwards every packet as soon as <see cref="PacketStreamWalker"/> reports it complete - it
/// does NOT hold back earlier packets (e.g. 0xA8 Server List) waiting for 0x8C to show up later.
/// An earlier version buffered everything until 0x8C was found, reasoning that 0x8C could arrive
/// split across two TCP reads and there's no way to un-send bytes - true, but irrelevant, because
/// the walker already never returns a packet until it's fully assembled, so that hazard doesn't
/// exist to begin with. What buffering-until-0x8C actually caused was a deadlock: 0x8C is only
/// sent by the server *after* the client answers an earlier packet (0xA8) that was also being
/// held back - the client can't answer what it never received, so the server never sends 0x8C,
/// so the proxy waits forever. (Found via a real login attempt hanging at "Verifying Account".)
/// </summary>
public sealed class LoginRedirectRewriter
{
    private readonly PacketStreamWalker _walker = new();

    public event Action<string, int>? RedirectSeen; // real game-server host, port
    public event Action<byte[]>? PacketObserved; // every packet this frames, post-rewrite - diagnostics/side-channel extraction

    public PacketStreamWalker Walker => _walker; // exposed for diagnostics (GaveUp, FramedPacketCount)

    /// <summary>Feed newly-received server-&gt;client bytes; returns what should be forwarded to
    /// the client right now (every packet that just became complete, 0x8C rewritten if present -
    /// possibly empty if this call only completed a partial packet).</summary>
    public byte[] Feed(ReadOnlySpan<byte> data, IPAddress proxyAddress, int proxyPort)
    {
        var packets = _walker.Feed(data);
        if (packets.Count == 0) return Array.Empty<byte>();

        foreach (var packet in packets)
        {
            if (packet.Length >= 11 && packet[0] == UoPacketLengths.PlayServerRedirect)
            {
                string realIp = $"{packet[1]}.{packet[2]}.{packet[3]}.{packet[4]}";
                int realPort = (packet[5] << 8) | packet[6];
                RedirectSeen?.Invoke(realIp, realPort);

                byte[] proxyIpBytes = proxyAddress.GetAddressBytes();
                Array.Copy(proxyIpBytes, 0, packet, 1, 4);
                packet[5] = (byte)(proxyPort >> 8);
                packet[6] = (byte)proxyPort;
                // Bytes 7-10 (the auth token) are left untouched - the client must present it
                // unchanged to whatever it connects to next.
            }

            PacketObserved?.Invoke(packet);
        }

        int total = 0;
        foreach (var p in packets) total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (var p in packets) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }
}
