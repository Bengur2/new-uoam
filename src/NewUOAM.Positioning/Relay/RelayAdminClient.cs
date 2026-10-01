using System.Net.Sockets;
using System.Text;

namespace NewUOAM.Positioning.Relay;

/// <summary>Client side of the relay's admin protocol (see RelayServer/RelayProtocol): one-off
/// request/response calls to create a room or list existing room names, gated by the server's
/// admin password (separate from any room's own password). Deliberately NOT a persistent
/// connection like RelayMultiplayerClient - each call opens a short-lived UdpClient, sends one
/// command, waits up to RelayProtocol.AdminResponseTimeoutMs for the one reply, then closes. Fine
/// for a manual admin action; not meant to be called at any real frequency.</summary>
public static class RelayAdminClient
{
    public sealed record CreateRoomResult(bool Success, string? Error);
    public sealed record ListRoomsResult(bool Success, string? Error, IReadOnlyList<string> RoomNames);
    public sealed record DeleteRoomResult(bool Success, string? Error, int PlayersKicked);

    public static async Task<CreateRoomResult> CreateRoomAsync(string host, int port, string adminPassword, string roomName, string roomPassword)
    {
        var response = await SendAndAwaitAsync(host, port, RelayProtocol.EncodeAdminCreate(adminPassword, roomName, roomPassword));
        if (response is null) return new CreateRoomResult(false, "TIMEOUT");
        return new CreateRoomResult(response.Value.Ok, response.Value.Error);
    }

    /// <summary>Deletes the room (kicks anyone currently connected to it - see RelayServer's
    /// HandleAdminDeleteAsync doc comment). Identified by name, matching what ListRoomsAsync shows
    /// (the password is deliberately never exposed there).</summary>
    public static async Task<DeleteRoomResult> DeleteRoomAsync(string host, int port, string adminPassword, string roomName)
    {
        var response = await SendAndAwaitAsync(host, port, RelayProtocol.EncodeAdminDelete(adminPassword, roomName));
        if (response is null) return new DeleteRoomResult(false, "TIMEOUT", 0);
        if (!response.Value.Ok) return new DeleteRoomResult(false, response.Value.Error, 0);
        int kicked = response.Value.Data.Length > 0 && int.TryParse(response.Value.Data[0], out int k) ? k : 0;
        return new DeleteRoomResult(true, null, kicked);
    }

    public static async Task<ListRoomsResult> ListRoomsAsync(string host, int port, string adminPassword)
    {
        var response = await SendAndAwaitAsync(host, port, RelayProtocol.EncodeAdminList(adminPassword));
        if (response is null) return new ListRoomsResult(false, "TIMEOUT", []);
        if (!response.Value.Ok) return new ListRoomsResult(false, response.Value.Error, []);

        string[] data = response.Value.Data;
        if (data.Length < 1 || !int.TryParse(data[0], out int count) || data.Length < 1 + count)
            return new ListRoomsResult(false, "MALFORMED_RESPONSE", []);

        return new ListRoomsResult(true, null, data[1..(1 + count)]);
    }

    private static async Task<RelayProtocol.AdminResponse?> SendAndAwaitAsync(string host, int port, string command)
    {
        using var udp = new UdpClient();
        udp.Connect(host, port);
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(command);
        await udp.SendAsync(payload, payload.Length);

        using var cts = new CancellationTokenSource(RelayProtocol.AdminResponseTimeoutMs);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                string text = RelayProtocol.WireEncoding.GetString(result.Buffer);
                if (RelayProtocol.TryParseAdminResponse(text, out var response)) return response;
            }
        }
        catch (OperationCanceledException) { return null; }
    }
}
