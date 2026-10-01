using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NewUOAM.Positioning;
using NewUOAM.Positioning.Relay;

namespace NewUOAM.RelayServer;

/// <summary>Standalone UDP relay: receives each connected client's own position report
/// (RelayProtocol.ReportTag) and broadcasts it to every other client *in the same room*
/// (RelayProtocol.BroadcastTag), so several separate friend groups can share one relay deployment
/// without seeing each other. A room is identified purely by its password - a client that reports
/// with an unknown room password is silently ignored (no session, no broadcast, no error reply -
/// deliberately no oracle for "is this password valid"). No relation to a client's "which
/// map/facet am I on" - it relays whatever (X,Y,Z,Map) it's given verbatim; filtering to "only
/// show players on my current facet" is a client-side rendering concern (see
/// MainWindow.UpdateRemotePlayerOverlay).
///
/// Rooms themselves are managed out-of-band via the admin protocol (RelayProtocol.AdminCommandTag),
/// gated by a separate admin password supplied to this server at startup (see Program.cs
/// --admin-password) - if none was supplied, admin commands are rejected outright rather than
/// silently having no effect. Room definitions (name + password, nothing else - no session/player
/// data) persist to a small JSON file so they survive a service restart; sessions themselves stay
/// purely in-memory, same as before rooms existed.
///
/// Room-scoped chat (RelayProtocol.ChatSendTag) reuses the exact same room-password lookup and
/// per-session presence tracking as position reports (see TouchSession) - deliberately NOT logged
/// or persisted anywhere, not even transiently to a file: HandleChatAsync only ever relays a
/// message live to whoever else is in the room at that instant, then it's gone.</summary>
public sealed class RelayServer
{
    private sealed class Session
    {
        public required IPEndPoint Endpoint;
        public required string Name;
        // Nullable: a session can now exist purely from chat activity, before ever sending a
        // position report (see TouchSession) - nothing to re-broadcast for it yet in that case.
        public PositionUpdate? Last;
        public string Color = RelayProtocol.DefaultPlayerColor;
        public DateTimeOffset LastSeen = DateTimeOffset.UtcNow;
        // Last track report accepted from this player (HandleTrackAsync throttle).
        public DateTimeOffset LastTrack = DateTimeOffset.MinValue;
    }

    private sealed class Room
    {
        public required string Name;
        public required string Password;
        public readonly ConcurrentDictionary<string, Session> Sessions = new();
        // The room's shared marker, memory only (like sessions). Replaced as a whole immutable
        // record, so a plain volatile reference is enough.
        public volatile RelayProtocol.SharedMarker? SharedMarker;
        // Name of the one player in panic, or null. Same "one per room, anyone may clear it"
        // model as the shared marker.
        public volatile string? PanicBy;
        // Markers each player shares with the room, by owner name (see RelayProtocol.MarkShareTag).
        public readonly ConcurrentDictionary<string, OwnerMarks> Marks = new();
    }

    /// <summary>One player's shared markers. Version goes up on every change (the clients' gap
    /// detection, see RelayProtocol). Guarded by locking the instance: the receive loop changes it
    /// while the sweep loop reads it.</summary>
    private sealed class OwnerMarks
    {
        public int Version;
        public readonly Dictionary<string, RelayProtocol.SharedMark> ById = new();
    }

    private sealed record RoomRecord(string Name, string Password);

    private readonly IPAddress _bindAddress;
    private readonly int _port;
    private readonly string? _adminPassword;
    private readonly string? _roomsFilePath;
    private readonly ConcurrentDictionary<string, Room> _roomsByPassword = new();
    private UdpClient? _udp;

    public RelayServer(IPAddress bindAddress, int port, string? adminPassword, string? roomsFilePath)
    {
        _bindAddress = bindAddress;
        _port = port;
        _adminPassword = string.IsNullOrWhiteSpace(adminPassword) ? null : adminPassword;
        _roomsFilePath = roomsFilePath;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        LoadRooms();

        _udp = new UdpClient(new IPEndPoint(_bindAddress, _port));
        // Sharing a marker category arrives as a burst of datagrams; a bigger buffer than the OS
        // default keeps a burst from being dropped before the receive loop gets to it.
        _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        Console.WriteLine($"NewUOAM relay server listening on {_bindAddress}:{_port}");
        Console.WriteLine(_adminPassword is null
            ? "Admin rozhraní VYPNUTO (spuštěno bez --admin-password)."
            : $"Admin rozhraní zapnuto, {_roomsByPassword.Count} místnost(í) načteno.");

        try
        {
            var sweepTask = SweepLoopAsync(ct);
            await ReceiveLoopAsync(ct);
            await sweepTask;
        }
        finally
        {
            // Without this, the OS keeps the port bound until the process actually exits (not
            // just until RunAsync returns) - harmless for the real deployment (the process really
            // does exit), but it means a caller that starts a second RelayServer on the same port
            // right after cancelling this one (e.g. a restart, or a test) gets a "address already
            // in use" SocketException even though RunAsync already returned.
            _udp?.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                Console.WriteLine($"Receive error: {ex.Message}");
                continue;
            }

            string text = RelayProtocol.WireEncoding.GetString(result.Buffer);

            if (RelayProtocol.TryParseAdminCommand(text, out var adminCommand))
            {
                await HandleAdminCommandAsync(adminCommand, result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseChatSend(text, out string chatRoomPassword, out string chatName, out string chatMessage, out string? chatTo))
            {
                await HandleChatAsync(chatRoomPassword, chatName, chatMessage, chatTo, result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParsePanicSet(text, out string panicRoomPassword, out string panicName, out bool panicOn))
            {
                await HandlePanicAsync(panicRoomPassword, panicName, panicOn, result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseTrackReport(text, out string trackRoomPassword, out string trackName,
                    out int trackX, out int trackY, out int trackMap, out var trackNames))
            {
                await HandleTrackAsync(trackRoomPassword, trackName, trackX, trackY, trackMap, trackNames, result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseAccessQuery(text, out string queryRoomPassword, out string? queryName))
            {
                var access = RelayProtocol.AccessResult.Granted;
                if (!_roomsByPassword.TryGetValue(queryRoomPassword, out var queryRoom))
                {
                    access = RelayProtocol.AccessResult.Denied;
                    Console.WriteLine($"[!] access denied (unknown room password) from {result.RemoteEndPoint}");
                }
                else if (queryName is not null && IsNameTaken(queryRoom, queryName, result.RemoteEndPoint))
                {
                    access = RelayProtocol.AccessResult.NameTaken;
                    Console.WriteLine($"[!] name '{queryName}' already in use in room '{queryRoom.Name}' - refused {result.RemoteEndPoint}");
                }
                await SendAsync(RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeAccessReply(access)), result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseHello(text, out string helloRoomPassword, out string helloName, out string? helloColor))
            {
                if (!_roomsByPassword.TryGetValue(helloRoomPassword, out var helloRoom)) continue; // unknown room password: ignore silently
                var (helloSession, isNewHello) = await TouchSessionAsync(helloRoom, helloName, result.RemoteEndPoint, helloColor);
                if (isNewHello) await AnnounceJoinAsync(helloRoom, helloSession!);
                continue;
            }

            if (RelayProtocol.TryParseSharedMarkerDrop(text, out string dropRoomPassword, out string dropName, out int dropX, out int dropY, out int dropMap))
            {
                await HandleSharedMarkerAsync(dropRoomPassword, dropName, new RelayProtocol.SharedMarker(dropX, dropY, dropMap, dropName), result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseSharedMarkerPickup(text, out string pickupRoomPassword, out string pickupName))
            {
                await HandleSharedMarkerAsync(pickupRoomPassword, pickupName, null, result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseMarkShare(text, out string shareRoomPassword, out string shareName, out var sharedMark))
            {
                await HandleMarkShareAsync(shareRoomPassword, shareName, sharedMark, result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseMarkUnshare(text, out string unshareRoomPassword, out string unshareName, out string unshareId))
            {
                await HandleMarkUnshareAsync(unshareRoomPassword, unshareName, unshareId, result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseMarkResync(text, out string resyncRoomPassword, out string resyncName, out string resyncOwner))
            {
                await HandleMarkResyncAsync(resyncRoomPassword, resyncName, resyncOwner, result.RemoteEndPoint);
                continue;
            }

            if (RelayProtocol.TryParseBye(text, out string byeRoomPassword, out string byeName))
            {
                await HandleByeAsync(byeRoomPassword, byeName, result.RemoteEndPoint);
                continue;
            }

            if (!RelayProtocol.TryParseReport(text, out string roomPassword, out string name, out var update, out string? color)) continue;
            if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) continue; // unknown room password: ignore silently

            var (session, isNew) = await TouchSessionAsync(room, name, result.RemoteEndPoint, color);
            if (session is null) continue; // name taken by someone else in this room
            session.Last = update;
            if (isNew) await AnnounceJoinAsync(room, session); // already includes this position for everyone else
            else await BroadcastAsync(room, session, excludeName: name);
        }
    }

    /// <summary>True if another client (a different endpoint) is actively using this name in this
    /// room. Per room only - the same name in a different room is fine. A session that has gone
    /// silent past the timeout (crashed client not yet swept) doesn't block the name.</summary>
    private static bool IsNameTaken(Room room, string name, IPEndPoint endpoint) =>
        room.Sessions.TryGetValue(name, out var existing)
        && !existing.Endpoint.Equals(endpoint)
        && (DateTimeOffset.UtcNow - existing.LastSeen).TotalSeconds <= RelayProtocol.SessionTimeoutSeconds;

    // Endpoint -> when it was last told NameTakenTag. An older client that doesn't understand the
    // notice keeps sending ~20 reports/s; answer it at most once per second.
    private readonly ConcurrentDictionary<IPEndPoint, DateTimeOffset> _nameTakenSentAt = new();

    /// <summary>TouchSession, but refuses a name another client is using in this room: the message
    /// is dropped (returns a null session) and the sender gets NameTakenTag. Without this the
    /// second client silently took over the session's endpoint on every message, so both clients
    /// only got room traffic intermittently.</summary>
    private async Task<(Session? Session, bool IsNew)> TouchSessionAsync(Room room, string name, IPEndPoint endpoint, string? color)
    {
        if (!IsNameTaken(room, name, endpoint)) return TouchSession(room, name, endpoint, color);

        var now = DateTimeOffset.UtcNow;
        if (!_nameTakenSentAt.TryGetValue(endpoint, out var last) || (now - last).TotalSeconds >= 1)
        {
            _nameTakenSentAt[endpoint] = now;
            if (_nameTakenSentAt.Count > 1000) _nameTakenSentAt.Clear(); // never grows unbounded
            Console.WriteLine($"[!] name '{name}' already in use in room '{room.Name}' - refused {endpoint}");
            await SendAsync(RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeNameTaken(name)), endpoint);
        }
        return (null, false);
    }

    /// <summary>Registers a new session or refreshes an existing one's endpoint/keepalive/color -
    /// shared by hello, position reports and chat messages, since any of them is equally valid
    /// proof a client is still present. Does NOT touch Last - a session that hasn't reported a
    /// position yet simply has nothing to re-broadcast in SweepLoopAsync. The caller is
    /// responsible for AnnounceJoinAsync when isNew (after filling in Last, if it has one).
    /// Call through TouchSessionAsync, which refuses a name that's taken in the room.</summary>
    private (Session Session, bool IsNew) TouchSession(Room room, string name, IPEndPoint endpoint, string? color)
    {
        if (room.Sessions.TryGetValue(name, out var existing))
        {
            existing.Endpoint = endpoint;
            existing.LastSeen = DateTimeOffset.UtcNow;
            if (color is not null) existing.Color = color;
            return (existing, false);
        }

        var created = new Session { Endpoint = endpoint, Name = name, Color = color ?? RelayProtocol.DefaultPlayerColor };
        room.Sessions[name] = created;
        Console.WriteLine($"[+] {name} joined room '{room.Name}' from {endpoint}");
        return (created, true);
    }

    /// <summary>Event-driven presence refresh for a brand-new session, scoped strictly to its own
    /// room: tells everyone else in the room it joined (JoinTag, plus its position right away if
    /// it already has one), and sends the newcomer every other member's last known position so
    /// they appear on its map immediately - instead of both sides waiting for the next periodic
    /// full-roster re-broadcast (up to FullRosterBroadcastIntervalSeconds). Other rooms are never
    /// touched.</summary>
    private async Task AnnounceJoinAsync(Room room, Session newcomer)
    {
        byte[] joinPayload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeJoin(newcomer.Name, newcomer.Color));
        await BroadcastPayloadAsync(room, joinPayload, excludeName: newcomer.Name);
        if (newcomer.Last is not null)
            await BroadcastAsync(room, newcomer, excludeName: newcomer.Name);

        foreach (var other in room.Sessions.Values)
        {
            if (other.Name == newcomer.Name) continue;
            if (other.Last is { } last)
                await SendAsync(RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeBroadcast(other.Name, last, other.Color)), newcomer.Endpoint);
        }

        if (room.PanicBy is { } panicking)
            await SendAsync(PanicPayload(new RelayProtocol.PanicState(panicking, Notify: false)), newcomer.Endpoint);

        if (room.SharedMarker is { } marker)
            await SendAsync(SharedMarkerStatePayload(marker, marker.DroppedBy, notify: false), newcomer.Endpoint);

        foreach (var (owner, marks) in room.Marks)
            if (owner != newcomer.Name) SendMarksResync(owner, marks, newcomer.Endpoint);
    }

    // ---- Shared markers (see RelayProtocol.MarkShareTag). ----

    private async Task HandleMarkShareAsync(string roomPassword, string playerName, RelayProtocol.SharedMark mark, IPEndPoint from)
    {
        if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) return;
        var (session, isNew) = await TouchSessionAsync(room, playerName, from, color: null);
        if (session is null) return; // name taken by someone else in this room
        if (isNew) await AnnounceJoinAsync(room, session);

        var marks = room.Marks.GetOrAdd(playerName, _ => new OwnerMarks());
        int version;
        lock (marks)
        {
            if (marks.ById.TryGetValue(mark.Id, out var existing) && existing == mark) return; // re-share of the same thing
            if (!marks.ById.ContainsKey(mark.Id) && marks.ById.Count >= RelayProtocol.MaxSharedMarksPerPlayer) return; // cap
            marks.ById[mark.Id] = mark;
            version = ++marks.Version;
        }
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeMark(playerName, version, mark, notify: true));
        await BroadcastPayloadAsync(room, payload, excludeName: playerName);
    }

    private async Task HandleMarkUnshareAsync(string roomPassword, string playerName, string id, IPEndPoint from)
    {
        if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) return;
        var (session, isNew) = await TouchSessionAsync(room, playerName, from, color: null);
        if (session is null) return;
        if (isNew) await AnnounceJoinAsync(room, session);
        if (!room.Marks.TryGetValue(playerName, out var marks)) return;

        int version;
        lock (marks)
        {
            bool changed = id == RelayProtocol.AllMarks ? marks.ById.Count > 0 : marks.ById.ContainsKey(id);
            if (!changed) return;
            if (id == RelayProtocol.AllMarks) marks.ById.Clear(); else marks.ById.Remove(id);
            version = ++marks.Version;
        }
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeMarkRemoved(playerName, version, id));
        await BroadcastPayloadAsync(room, payload, excludeName: playerName);
    }

    /// <summary>A client noticed it's out of sync with <paramref name="owner"/>'s markers (a gap in
    /// versions or a manifest mismatch): send it the whole set again.</summary>
    private async Task HandleMarkResyncAsync(string roomPassword, string playerName, string owner, IPEndPoint from)
    {
        if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) return;
        var (session, isNew) = await TouchSessionAsync(room, playerName, from, color: null);
        if (session is null) return;
        if (isNew) await AnnounceJoinAsync(room, session);

        if (room.Marks.TryGetValue(owner, out var marks))
            SendMarksResync(owner, marks, session.Endpoint);
        else // the owner is gone or never shared anything: an empty set clears the client's copy
            await SendAsync(RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeMarkResyncBegin(owner, 0, 0)), session.Endpoint);
    }

    /// <summary>Begin + every marker of one owner, to one client. Runs in the background and
    /// paced, so a big set neither stalls the receive loop nor overflows the client's buffer.</summary>
    private void SendMarksResync(string owner, OwnerMarks marks, IPEndPoint to)
    {
        List<byte[]> payloads;
        lock (marks)
        {
            payloads = new List<byte[]>(marks.ById.Count + 1)
            {
                RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeMarkResyncBegin(owner, marks.Version, marks.ById.Count)),
            };
            foreach (var mark in marks.ById.Values)
                payloads.Add(RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeMark(owner, marks.Version, mark, notify: false)));
        }
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < payloads.Count; i++)
            {
                await SendAsync(payloads[i], to);
                if (i % MarkResyncBurst == MarkResyncBurst - 1) await Task.Delay(MarkResyncPauseMs);
            }
        });
    }

    private const int MarkResyncBurst = 50;
    private const int MarkResyncPauseMs = 10;

    /// <summary>A player who leaves takes their shared markers with them (user's call).</summary>
    private async Task ClearMarksOfLeaverAsync(Room room, string playerName)
    {
        if (!room.Marks.TryRemove(playerName, out var marks)) return;
        int version;
        lock (marks) version = marks.Version + 1;
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeMarkRemoved(playerName, version, RelayProtocol.AllMarks));
        await BroadcastPayloadAsync(room, payload, excludeName: playerName);
    }

    /// <summary>Drop (<paramref name="marker"/> set) or pick up (null) the room's shared marker -
    /// anyone in the room may do either. The new state goes to the whole room INCLUDING the
    /// sender, so every client just renders what the server holds. A pickup with no marker is a
    /// no-op (e.g. two players picking it up at once).</summary>
    private async Task HandleSharedMarkerAsync(string roomPassword, string playerName, RelayProtocol.SharedMarker? marker, IPEndPoint from)
    {
        if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) return; // unknown room password: same silent-drop rule as reports
        var (session, isNew) = await TouchSessionAsync(room, playerName, from, color: null);
        if (session is null) return; // name taken by someone else in this room
        if (isNew) await AnnounceJoinAsync(room, session);
        if (marker is null && room.SharedMarker is null) return;

        room.SharedMarker = marker;
        Console.WriteLine(marker is null
            ? $"[m] {playerName} picked up the shared marker (room '{room.Name}')"
            : $"[m] {playerName} dropped the shared marker at {marker.X},{marker.Y} map {marker.Map} (room '{room.Name}')");
        await BroadcastPayloadAsync(room, SharedMarkerStatePayload(marker, playerName, notify: true), excludeName: "");
    }

    private static byte[] SharedMarkerStatePayload(RelayProtocol.SharedMarker? marker, string actor, bool notify) =>
        RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeSharedMarkerState(marker, actor, notify));

    /// <summary>Clean disconnect: removes the session and tells the rest of its room right away
    /// (LeaveTag), rather than everyone seeing a frozen marker until the timeout sweep. Only
    /// honored from the endpoint the session is currently registered at - a stray/late bye from an
    /// old socket (e.g. the same name already reconnected from a new one) mustn't kick the live
    /// session.</summary>
    private async Task HandleByeAsync(string roomPassword, string playerName, IPEndPoint from)
    {
        if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) return;
        if (!room.Sessions.TryGetValue(playerName, out var session) || !session.Endpoint.Equals(from)) return;
        if (!room.Sessions.TryRemove(playerName, out _)) return;

        Console.WriteLine($"[-] {playerName} left room '{room.Name}'");
        await AnnounceLeaveAsync(room, playerName);
        await ClearPanicOfLeaverAsync(room, playerName);
        await ClearMarksOfLeaverAsync(room, playerName);
    }

    private async Task AnnounceLeaveAsync(Room room, string playerName)
    {
        byte[] leavePayload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeLeave(playerName));
        foreach (var session in room.Sessions.Values)
            await SendAsync(leavePayload, session.Endpoint);
    }

    /// <param name="toPlayer">Private message: deliver to this one player of the room only (dropped
    /// if they're not in it). Null = the whole room.</param>
    private async Task HandleChatAsync(string roomPassword, string playerName, string message, string? toPlayer, IPEndPoint from)
    {
        if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) return; // unknown room password: same silent-drop rule as reports
        var (session, isNew) = await TouchSessionAsync(room, playerName, from, color: null);
        if (session is null) return; // name taken by someone else in this room
        if (isNew) await AnnounceJoinAsync(room, session);
        if (toPlayer is null)
        {
            byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeChatBroadcast(playerName, message, session.Color));
            await BroadcastPayloadAsync(room, payload, excludeName: playerName);
        }
        else if (toPlayer != playerName && room.Sessions.TryGetValue(toPlayer, out var target))
        {
            byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeChatBroadcast(playerName, message, session.Color, isPrivate: true));
            await SendAsync(payload, target.Endpoint);
        }
    }

    /// <summary>Panic! for the room: on = the sender is now the one in panic (replacing anyone
    /// else's), off = clear the room's panic, whoever set it. A change goes to the whole room
    /// including the sender (Notify = true); a set that changes nothing is answered to the sender
    /// only, so "-unpanic" with no panic still gets a response without bothering everyone.</summary>
    private async Task HandlePanicAsync(string roomPassword, string playerName, bool on, IPEndPoint from)
    {
        if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) return;
        var (session, isNew) = await TouchSessionAsync(room, playerName, from, color: null);
        if (session is null) return; // name taken by someone else in this room
        if (isNew) await AnnounceJoinAsync(room, session);

        string? previous = room.PanicBy;
        string? next = on ? playerName : null;
        if (previous == next)
        {
            var unchanged = on ? new RelayProtocol.PanicState(playerName, Notify: true)
                               : new RelayProtocol.PanicState(null, Notify: true, playerName, Previous: null);
            await SendAsync(PanicPayload(unchanged), session.Endpoint);
            return;
        }
        room.PanicBy = next;
        Console.WriteLine(on
            ? $"[p] {playerName} panic ON (room '{room.Name}')"
            : $"[p] {playerName} turned off {previous}'s panic (room '{room.Name}')");
        var state = on ? new RelayProtocol.PanicState(playerName, Notify: true)
                       : new RelayProtocol.PanicState(null, Notify: true, playerName, previous);
        await BroadcastPayloadAsync(room, PanicPayload(state), excludeName: "");
    }

    /// <summary>A track report ("-t"): passed to the whole room including the reporter, with the
    /// reporter's color; nothing is kept. At most one per second per player, so a looping script
    /// can't flood the room.</summary>
    private async Task HandleTrackAsync(string roomPassword, string playerName, int x, int y, int map, List<string> names, IPEndPoint from)
    {
        if (!_roomsByPassword.TryGetValue(roomPassword, out var room)) return;
        var (session, isNew) = await TouchSessionAsync(room, playerName, from, color: null);
        if (session is null) return; // name taken by someone else in this room
        if (isNew) await AnnounceJoinAsync(room, session);

        var now = DateTimeOffset.UtcNow;
        if (now - session.LastTrack < TimeSpan.FromSeconds(1)) return;
        session.LastTrack = now;

        Console.WriteLine($"[t] {playerName} tracked {names.Count} at {x},{y} map {map} (room '{room.Name}')");
        var report = new RelayProtocol.TrackReport(playerName, x, y, map, names, session.Color);
        await BroadcastPayloadAsync(room, RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeTrackBroadcast(report)), excludeName: "");
    }

    private static byte[] PanicPayload(RelayProtocol.PanicState state) =>
        RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodePanicState(state));

    /// <summary>A panicking player who leaves (bye or timeout) takes the panic with them - nobody
    /// can help someone who's no longer on the map. Quiet (Notify = false): the leave notice
    /// already says they're gone.</summary>
    private async Task ClearPanicOfLeaverAsync(Room room, string playerName)
    {
        if (room.PanicBy != playerName) return;
        room.PanicBy = null;
        await BroadcastPayloadAsync(room, PanicPayload(new RelayProtocol.PanicState(null, Notify: false, playerName, playerName)), excludeName: "");
    }

    private async Task HandleAdminCommandAsync(RelayProtocol.AdminCommand command, IPEndPoint replyTo)
    {
        if (_adminPassword is null)
        {
            await SendAdminErrAsync(command.Command, "ADMIN_DISABLED", replyTo);
            return;
        }
        if (command.AdminPassword != _adminPassword)
        {
            await SendAdminErrAsync(command.Command, "BAD_ADMIN_PASSWORD", replyTo);
            return;
        }

        switch (command.Command)
        {
            case "CREATE":
                await HandleAdminCreateAsync(command.Args, replyTo);
                break;
            case "LIST":
                await HandleAdminListAsync(replyTo);
                break;
            case "DELETE":
                await HandleAdminDeleteAsync(command.Args, replyTo);
                break;
            default:
                await SendAdminErrAsync(command.Command, "UNKNOWN_COMMAND", replyTo);
                break;
        }
    }

    private async Task HandleAdminCreateAsync(string[] args, IPEndPoint replyTo)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            await SendAdminErrAsync("CREATE", "INVALID_ARGS", replyTo);
            return;
        }
        string roomName = args[0];
        string roomPassword = args[1];

        var room = new Room { Name = roomName, Password = roomPassword };
        if (!_roomsByPassword.TryAdd(roomPassword, room))
        {
            await SendAdminErrAsync("CREATE", "DUPLICATE_PASSWORD", replyTo);
            return;
        }

        SaveRooms();
        Console.WriteLine($"[admin] room '{roomName}' created from {replyTo}");
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeAdminOk("CREATE"));
        await SendAsync(payload, replyTo);
    }

    private async Task HandleAdminListAsync(IPEndPoint replyTo)
    {
        string[] names = _roomsByPassword.Values.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] data = [names.Length.ToString(), .. names];
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeAdminOk("LIST", data));
        await SendAsync(payload, replyTo);
    }

    /// <summary>Deletes every room whose name exactly matches (names aren't enforced unique at
    /// CREATE time - if an admin somehow made duplicates, DELETE cleans up all of them at once
    /// rather than leaving the rest as orphans with no way to address them individually, since the
    /// admin never sees passwords to disambiguate by). Per the user's own explicit call on how to
    /// handle currently-connected players: just kick them - send each one a KickedTag notice (so
    /// their client can tell the user why, instead of silently going quiet) and remove the room,
    /// no "are you sure people are connected" gate beyond the confirmation the admin UI itself asks.</summary>
    private async Task HandleAdminDeleteAsync(string[] args, IPEndPoint replyTo)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            await SendAdminErrAsync("DELETE", "INVALID_ARGS", replyTo);
            return;
        }
        string roomName = args[0];

        var matches = _roomsByPassword.Values.Where(r => r.Name == roomName).ToList();
        if (matches.Count == 0)
        {
            await SendAdminErrAsync("DELETE", "NOT_FOUND", replyTo);
            return;
        }

        int kicked = 0;
        byte[] kickedPayload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeKicked("room deleted"));
        foreach (var room in matches)
        {
            _roomsByPassword.TryRemove(room.Password, out _);
            foreach (var session in room.Sessions.Values)
            {
                await SendAsync(kickedPayload, session.Endpoint);
                kicked++;
            }
        }

        SaveRooms();
        Console.WriteLine($"[admin] room '{roomName}' deleted ({matches.Count} room(s), {kicked} player(s) kicked) from {replyTo}");
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeAdminOk("DELETE", kicked.ToString()));
        await SendAsync(payload, replyTo);
    }

    private async Task SendAdminErrAsync(string command, string reason, IPEndPoint replyTo)
    {
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeAdminErr(command, reason));
        await SendAsync(payload, replyTo);
    }

    /// <summary>Every FullRosterBroadcastIntervalSeconds, per room: drops sessions that haven't
    /// reported in SessionTimeoutSeconds (announcing each as a leave to the rest of that room),
    /// then re-broadcasts every remaining session's last-known position to everyone else in the
    /// same room - see RelayProtocol's doc comment for why (late joiners, dropped packets).
    /// Broadcasts never cross rooms.</summary>
    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(RelayProtocol.FullRosterBroadcastIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }

            var now = DateTimeOffset.UtcNow;
            foreach (var room in _roomsByPassword.Values)
            {
                foreach (var kv in room.Sessions)
                {
                    if ((now - kv.Value.LastSeen).TotalSeconds <= RelayProtocol.SessionTimeoutSeconds) continue;
                    if (!room.Sessions.TryRemove(kv.Key, out _)) continue;

                    Console.WriteLine($"[-] {kv.Key} timed out (room '{room.Name}')");
                    await AnnounceLeaveAsync(room, kv.Key);
                    await ClearPanicOfLeaverAsync(room, kv.Key);
                    await ClearMarksOfLeaverAsync(room, kv.Key);
                }

                // Shared markers: just version + count per owner (the full sets are only re-sent
                // to a client that finds it's out of sync and asks - see RelayProtocol).
                foreach (var (owner, marks) in room.Marks)
                {
                    int version, count;
                    lock (marks) { version = marks.Version; count = marks.ById.Count; }
                    byte[] manifest = RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeMarkManifest(owner, version, count));
                    await BroadcastPayloadAsync(room, manifest, excludeName: owner);
                }

                foreach (var subject in room.Sessions.Values)
                {
                    // A session that never sent a position report (hello/chat only) has nothing
                    // to re-broadcast here yet - see Session.Last's doc comment.
                    if (subject.Last is not null)
                        await BroadcastAsync(room, subject, excludeName: subject.Name);
                }

                // Shared marker state, also when there is none: a lost pickup datagram must not
                // leave a stale marker on someone's map for good.
                if (!room.Sessions.IsEmpty)
                {
                    var marker = room.SharedMarker;
                    await BroadcastPayloadAsync(room, SharedMarkerStatePayload(marker, marker?.DroppedBy ?? "", notify: false), excludeName: "");
                    // Same for the panic, "none" included: a lost "off" datagram must not leave
                    // anyone's map flashing for good.
                    await BroadcastPayloadAsync(room, PanicPayload(new RelayProtocol.PanicState(room.PanicBy, Notify: false)), excludeName: "");
                }
            }
        }
    }

    private async Task BroadcastAsync(Room room, Session subject, string excludeName) =>
        await BroadcastPayloadAsync(room, RelayProtocol.WireEncoding.GetBytes(RelayProtocol.EncodeBroadcast(subject.Name, subject.Last!, subject.Color)), excludeName);

    private async Task BroadcastPayloadAsync(Room room, byte[] payload, string excludeName)
    {
        foreach (var session in room.Sessions.Values)
        {
            if (session.Name == excludeName) continue;
            await SendAsync(payload, session.Endpoint);
        }
    }

    private async Task SendAsync(byte[] payload, IPEndPoint to)
    {
        try { await _udp!.SendAsync(payload, to); }
        catch (Exception ex) { Console.WriteLine($"Send to {to} failed ({ex.GetType().Name}): {ex.Message}"); }
    }

    private void LoadRooms()
    {
        if (_roomsFilePath is null || !File.Exists(_roomsFilePath)) return;
        try
        {
            var records = JsonSerializer.Deserialize<RoomRecord[]>(File.ReadAllText(_roomsFilePath)) ?? [];
            foreach (var r in records)
                _roomsByPassword[r.Password] = new Room { Name = r.Name, Password = r.Password };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Nepodařilo se načíst {_roomsFilePath}: {ex.Message} (pokračuji bez místností)");
        }
    }

    private void SaveRooms()
    {
        if (_roomsFilePath is null) return;
        try
        {
            var records = _roomsByPassword.Values.Select(r => new RoomRecord(r.Name, r.Password)).ToArray();
            File.WriteAllText(_roomsFilePath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Nepodařilo se uložit {_roomsFilePath}: {ex.Message}");
        }
    }
}
