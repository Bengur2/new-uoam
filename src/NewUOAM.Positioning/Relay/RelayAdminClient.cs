using System.Net.Sockets;
using System.Security.Cryptography;

namespace NewUOAM.Positioning.Relay;

/// <summary>Client side of the relay's admin protocol (see RelayServer/RelayProtocol): one-off
/// request/response calls to create, list and delete rooms and switch player room creation,
/// gated by the server's admin password (separate from any room's own password). Also the
/// player-side "create a room" request, which needs no admin password. Deliberately NOT a
/// persistent connection like RelayMultiplayerClient - each call opens a short-lived UdpClient,
/// sends one command, waits up to RelayProtocol.AdminResponseTimeoutMs for the one reply, then
/// closes. Fine for a manual action; not meant to be called at any real frequency.</summary>
public static class RelayAdminClient
{
    public sealed record CreateRoomResult(bool Success, string? Error);
    public sealed record ListRoomsResult(bool Success, string? Error, IReadOnlyList<string> RoomNames);
    public sealed record DeleteRoomResult(bool Success, string? Error, int PlayersKicked);
    public sealed record RoomListResult(bool Success, string? Error, IReadOnlyList<RelayProtocol.AdminRoomInfo> Rooms);
    public sealed record DeleteRoomsResult(bool Success, string? Error, int RoomsDeleted, int PlayersKicked);
    public sealed record CreationSettings(bool Success, string? Error, bool PlayersMayCreate, int PlayerRooms, int MaxPlayerRooms);
    public sealed record PlayerCreateResult(bool Success, string? Error, string? Password);

    public static async Task<CreateRoomResult> CreateRoomAsync(string host, int port, string adminPassword, string roomName, string roomPassword)
    {
        var response = await SendAdminAsync(host, port, RelayProtocol.EncodeAdminCreate(adminPassword, roomName, roomPassword));
        if (response is null) return new CreateRoomResult(false, "TIMEOUT");
        return new CreateRoomResult(response.Value.Ok, response.Value.Error);
    }

    /// <summary>Deletes the room (kicks anyone currently connected to it - see RelayServer's
    /// HandleAdminDeleteAsync doc comment). Identified by name, matching what ListRoomsAsync shows
    /// (the password is deliberately never exposed there).</summary>
    public static async Task<DeleteRoomResult> DeleteRoomAsync(string host, int port, string adminPassword, string roomName)
    {
        var response = await SendAdminAsync(host, port, RelayProtocol.EncodeAdminDelete(adminPassword, roomName));
        if (response is null) return new DeleteRoomResult(false, "TIMEOUT", 0);
        if (!response.Value.Ok) return new DeleteRoomResult(false, response.Value.Error, 0);
        int kicked = response.Value.Data.Length > 0 && int.TryParse(response.Value.Data[0], out int k) ? k : 0;
        return new DeleteRoomResult(true, null, kicked);
    }

    public static async Task<ListRoomsResult> ListRoomsAsync(string host, int port, string adminPassword)
    {
        var response = await SendAdminAsync(host, port, RelayProtocol.EncodeAdminList(adminPassword));
        if (response is null) return new ListRoomsResult(false, "TIMEOUT", []);
        if (!response.Value.Ok) return new ListRoomsResult(false, response.Value.Error, []);

        string[] data = response.Value.Data;
        if (data.Length < 1 || !int.TryParse(data[0], out int count) || data.Length < 1 + count)
            return new ListRoomsResult(false, "MALFORMED_RESPONSE", []);

        return new ListRoomsResult(true, null, data[1..(1 + count)]);
    }

    /// <summary>All rooms with id, source and dates (newest first), fetched page by page.</summary>
    public static async Task<RoomListResult> ListRoomDetailsAsync(string host, int port, string adminPassword)
    {
        var rooms = new List<RelayProtocol.AdminRoomInfo>();
        while (true)
        {
            var response = await SendAdminAsync(host, port, RelayProtocol.EncodeAdminRooms(adminPassword, rooms.Count));
            if (response is null) return new RoomListResult(false, "TIMEOUT", []);
            if (!response.Value.Ok) return new RoomListResult(false, response.Value.Error, []);

            string[] d = response.Value.Data;
            if (d.Length < 2 || !int.TryParse(d[0], out int total) || !int.TryParse(d[1], out int count)
                || d.Length < 2 + count * RelayProtocol.AdminRoomFields)
                return new RoomListResult(false, "MALFORMED_RESPONSE", []);
            for (int i = 0; i < count; i++)
            {
                var room = RelayProtocol.TryDecodeAdminRoomFields(d.AsSpan(2 + i * RelayProtocol.AdminRoomFields, RelayProtocol.AdminRoomFields));
                if (room is null) return new RoomListResult(false, "MALFORMED_RESPONSE", []);
                rooms.Add(room);
            }
            if (count == 0 || rooms.Count >= total) return new RoomListResult(true, null, rooms);
        }
    }

    public static async Task<DeleteRoomsResult> DeleteRoomsAsync(string host, int port, string adminPassword, IEnumerable<string> ids)
    {
        var response = await SendAdminAsync(host, port, RelayProtocol.EncodeAdminDeleteIds(adminPassword, ids));
        if (response is null) return new DeleteRoomsResult(false, "TIMEOUT", 0, 0);
        if (!response.Value.Ok) return new DeleteRoomsResult(false, response.Value.Error, 0, 0);
        string[] d = response.Value.Data;
        int deleted = d.Length > 0 && int.TryParse(d[0], out int n) ? n : 0;
        int kicked = d.Length > 1 && int.TryParse(d[1], out int k) ? k : 0;
        return new DeleteRoomsResult(true, null, deleted, kicked);
    }

    public static Task<CreationSettings> GetCreationSettingsAsync(string host, int port, string adminPassword) =>
        SettingsCallAsync(host, port, RelayProtocol.EncodeAdminGetSettings(adminPassword));

    public static Task<CreationSettings> SetPlayerCreationAsync(string host, int port, string adminPassword, bool enabled) =>
        SettingsCallAsync(host, port, RelayProtocol.EncodeAdminSetCreation(adminPassword, enabled));

    private static async Task<CreationSettings> SettingsCallAsync(string host, int port, string command)
    {
        var response = await SendAdminAsync(host, port, command);
        if (response is null) return new CreationSettings(false, "TIMEOUT", false, 0, 0);
        if (!response.Value.Ok) return new CreationSettings(false, response.Value.Error, false, 0, 0);
        string[] d = response.Value.Data;
        if (d.Length < 3 || !int.TryParse(d[1], out int rooms) || !int.TryParse(d[2], out int max))
            return new CreationSettings(false, "UNSUPPORTED", false, 0, 0); // a server from before 2026-10-01
        return new CreationSettings(true, null, d[0] == "1", rooms, max);
    }

    /// <summary>Player side: asks the server for a new room; on success the reply carries the
    /// password the server generated.</summary>
    public static async Task<PlayerCreateResult> CreatePlayerRoomAsync(string host, int port, string roomName)
    {
        string requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var reply = await SendAndAwaitAsync<RelayProtocol.RoomCreateReply>(host, port, RelayProtocol.EncodeRoomCreate(requestId, roomName),
            text => RelayProtocol.TryParseRoomCreateReply(text, out var r) && r.RequestId == requestId ? r : null);
        if (reply is null) return new PlayerCreateResult(false, "TIMEOUT", null);
        return reply.Value.Ok ? new PlayerCreateResult(true, null, reply.Value.Password) : new PlayerCreateResult(false, reply.Value.Error, null);
    }

    private static Task<RelayProtocol.AdminResponse?> SendAdminAsync(string host, int port, string command) =>
        SendAndAwaitAsync<RelayProtocol.AdminResponse>(host, port, command,
            text => RelayProtocol.TryParseAdminResponse(text, out var response) ? response : null);

    private static async Task<T?> SendAndAwaitAsync<T>(string host, int port, string command, Func<string, T?> parse) where T : struct
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
                if (parse(RelayProtocol.WireEncoding.GetString(result.Buffer)) is { } parsed) return parsed;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; } // e.g. ICMP port unreachable: no server there
    }
}
