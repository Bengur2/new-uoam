using System.Globalization;
using System.Text;

namespace NewUOAM.Positioning.Relay;

/// <summary>Wire protocol for the multiplayer relay server (see NewUOAM.RelayServer): a UDP relay
/// that lets several app instances see each other's positions on the same map. Reuses the same
/// '|'-delimited line style as the local Orion feed (<see cref="Providers.OrionUdpPositionProvider"/>)
/// rather than inventing a new format; every datagram is UTF-8 (<see cref="WireEncoding"/>) so
/// player names/room passwords can carry diacritics. No encryption - anyone who knows the server
/// address:port can send packets. Isolation between friend groups is via "rooms": a report only
/// reaches other clients that reported into the same room (identified purely by a shared password -
/// see <see cref="ReportTag"/>). Rooms themselves can only be created/listed/deleted by whoever
/// knows the separate admin password (see <see cref="AdminCommandTag"/>) - acceptable for a small
/// friend-group relay (the deployment this was built for); not intended for a public/adversarial
/// server. Room-scoped chat (<see cref="ChatSendTag"/>) rides the same room-password isolation as
/// position reports and is equally un-persisted - the server never writes a chat message
/// anywhere, only relays it live to whoever else is in the room at that moment.
///
/// Presence (who is in the room) is event-driven, not just poll-driven: a client announces itself
/// with <see cref="HelloTag"/> right at connect (before it necessarily has a position), the server
/// answers a brand-new session with the current roster and tells everyone else via
/// <see cref="JoinTag"/>; a client that disconnects cleanly sends <see cref="ByeTag"/> and the
/// server announces <see cref="LeaveTag"/> immediately instead of waiting for the session timeout.
/// The periodic full-roster re-broadcast and timeout sweep still exist as the fallback for lost
/// datagrams and crashed clients.</summary>
public static class RelayProtocol
{
    public const string ReportTag = "UOAMRC1";        // client -> server: "here's my position, in room X"
    public const string BroadcastTag = "UOAMRS1";      // server -> client: "here's a player's position"
    public const string LeaveTag = "UOAMRL1";          // server -> client: "this player is gone"
    public const string AdminCommandTag = "UOAMAC1";   // admin client -> server: manage rooms
    public const string AdminResponseTag = "UOAMAR1";  // server -> admin client: result of an admin command
    public const string KickedTag = "UOAMRK1";         // server -> client: "your room was deleted, you're disconnected"
    public const string ChatSendTag = "UOAMCC1";       // client -> server: room-scoped chat message
    public const string ChatBroadcastTag = "UOAMCS1";  // server -> client: someone's chat message
    public const string HelloTag = "UOAMRH1";          // client -> server: "I'm in room X" (connect / keepalive without a position)
    public const string ByeTag = "UOAMRB1";            // client -> server: "I'm leaving room X" (clean disconnect)
    public const string JoinTag = "UOAMRJ1";           // server -> client: "this player just joined your room"
    public const string AccessQueryTag = "UOAMRQ1";    // client -> server: "may I join room X?" (sent at connect)
    public const string AccessReplyTag = "UOAMRA1";    // server -> client: OK / DENIED
    public const string NameTakenTag = "UOAMRN1";      // server -> client: "that name is already in use in this room"
    public const string SharedMarkerDropTag = "UOAMMD1";   // client -> server: drop the room's shared marker here
    public const string SharedMarkerPickupTag = "UOAMMP1"; // client -> server: pick the room's shared marker up
    public const string SharedMarkerStateTag = "UOAMMS1";  // server -> client: the room's shared marker (or none)
    public const string PanicSetTag = "UOAMPC1";       // client -> server: panic on (mine) / off (the room's)
    public const string PanicStateTag = "UOAMPS1";     // server -> client: the room's panic (or none)
    public const string TrackReportTag = "UOAMTR1";    // client -> server: "I tracked these players here" ("-t")
    public const string TrackBroadcastTag = "UOAMTB1"; // server -> client: someone's track report
    public const string MarkShareTag = "UOAMKA1";      // client -> server: share (or update) one of my markers
    public const string MarkUnshareTag = "UOAMKR1";    // client -> server: stop sharing one of my markers ("*" = all)
    public const string MarkResyncTag = "UOAMKQ1";     // client -> server: send me all of this player's shared markers
    public const string MarkTag = "UOAMKS1";           // server -> client: one shared marker
    public const string MarkRemovedTag = "UOAMKX1";    // server -> client: a shared marker is gone ("*" = all of a player's)
    public const string MarkManifestTag = "UOAMKM1";   // server -> client: a player's shared-marker version + count (sync check)
    public const string MarkResyncBeginTag = "UOAMKB1"; // server -> client: all of a player's markers follow (version + count)

    /// <summary>How long RelayMultiplayerClient.StartAsync waits for an AccessReplyTag before
    /// reporting "server not responding"; the query is resent every AccessQueryRetryMs meanwhile
    /// (UDP may drop any single datagram).</summary>
    public const int AccessReplyTimeoutMs = 3000;
    public const int AccessQueryRetryMs = 1000;

    /// <summary>Encoding of every relay datagram, both directions. UTF-8 is a strict superset of
    /// the ASCII the protocol used originally, so plain-ASCII messages are byte-identical.</summary>
    public static readonly Encoding WireEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Color shown for a player whose client didn't send one (an older build) - the cyan
    /// every remote marker used before per-player colors existed.</summary>
    public const string DefaultPlayerColor = "00FFFF";

    /// <summary>Chat messages are truncated to this many UTF-16 characters before sending -
    /// there's no reason for a live position-relay tool to also carry arbitrarily long text, and
    /// this keeps every chat packet comfortably within a single UDP datagram.</summary>
    public const int MaxChatMessageLength = 400;

    /// <summary>Server drops a session that hasn't sent anything in this long (a crashed client -
    /// a clean disconnect sends <see cref="ByeTag"/> and is announced immediately).</summary>
    public const int SessionTimeoutSeconds = 15;

    /// <summary>How often the server re-broadcasts every session's last-known position to every
    /// other session in the same room. No longer how a late joiner catches up (the server sends a
    /// brand-new session the roster immediately - see <see cref="JoinTag"/>), just the safety net
    /// for datagrams UDP silently lost.</summary>
    public const int FullRosterBroadcastIntervalSeconds = 3;

    /// <summary>Client-side send cadence. This also doubles as the session's keepalive - the
    /// server relies on receiving *something* at roughly this cadence even while the player stands
    /// still. Originally 500ms, then 150ms, now 50ms after live testing kept feeling "laggy" (the
    /// whole point of this project vs. old UOAM) - at this deployment's scale (a handful of
    /// friends, tiny packets) even 50ms/20Hz per player is trivial bandwidth, and it's still well
    /// above the local Orion feed's own ~20ms polling floor, so nothing's wasted sending duplicate
    /// stale data. Kept in step with MainWindow's remote-marker glide duration
    /// (RemoteAnimationDurationMs) - the glide itself adds latency (a marker takes that long to
    /// visually reach a freshly reported position), so lowering the report interval without also
    /// shortening the glide would cancel out most of the gain.</summary>
    public const int ClientReportIntervalMs = 50;

    /// <summary>While a client has no position to report yet (no local provider running), it
    /// sends <see cref="HelloTag"/> at this cadence instead - keeps the session registered (so it
    /// receives positions/chat right away) without spamming 20 identical hellos per second.</summary>
    public const int ClientHelloIntervalMs = 1000;

    /// <summary>How long an admin client waits for a UOAMAR1 reply before giving up. Plain
    /// timeout, not a retry protocol - fine for a manual, occasional admin action over UDP.</summary>
    public const int AdminResponseTimeoutMs = 3000;

    // Player color rides as a trailing "RRGGBB" field on every message that introduces a player's
    // appearance (report/broadcast/hello/join/chat broadcast). Always optional when parsing, so a
    // message from an older build without it still parses - the receiver just falls back to
    // DefaultPlayerColor.

    public static string EncodeReport(string roomPassword, string playerName, PositionUpdate u, string? color = null) =>
        $"{ReportTag}|{Escape(roomPassword)}|{Escape(playerName)}|{u.X}|{u.Y}|{u.Z}|{u.Map}|{ColorOrDefault(color)}";

    public static string EncodeBroadcast(string playerName, PositionUpdate u, string? color = null) =>
        $"{BroadcastTag}|{Escape(playerName)}|{u.X}|{u.Y}|{u.Z}|{u.Map}|{ColorOrDefault(color)}";

    public static string EncodeLeave(string playerName) => $"{LeaveTag}|{Escape(playerName)}";

    public static bool TryParseReport(string line, out string roomPassword, out string playerName, out PositionUpdate update, out string? color)
    {
        roomPassword = "";
        playerName = "";
        update = null!;
        color = null;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 7 || parts[0] != ReportTag) return false;
        if (string.IsNullOrEmpty(parts[1])) return false;
        if (!IsVisibleName(parts[2])) return false;
        if (!int.TryParse(parts[3], out int x)) return false;
        if (!int.TryParse(parts[4], out int y)) return false;
        if (!int.TryParse(parts[5], out int z)) return false;
        if (!int.TryParse(parts[6], out int map)) return false;

        roomPassword = parts[1];
        playerName = parts[2];
        update = PositionUpdate.Now(x, y, z, map, playerName);
        color = parts.Length > 7 ? NormalizeColor(parts[7]) : null;
        return true;
    }

    public static bool TryParseBroadcast(string line, out string playerName, out PositionUpdate update, out string? color)
    {
        playerName = "";
        update = null!;
        color = null;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 6 || parts[0] != BroadcastTag) return false;
        if (string.IsNullOrEmpty(parts[1])) return false;
        if (!int.TryParse(parts[2], out int x)) return false;
        if (!int.TryParse(parts[3], out int y)) return false;
        if (!int.TryParse(parts[4], out int z)) return false;
        if (!int.TryParse(parts[5], out int map)) return false;

        playerName = parts[1];
        update = PositionUpdate.Now(x, y, z, map, playerName);
        color = parts.Length > 6 ? NormalizeColor(parts[6]) : null;
        return true;
    }

    public static bool TryParseLeave(string line, out string playerName)
    {
        playerName = "";
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 2 || parts[0] != LeaveTag) return false;
        playerName = parts[1];
        return !string.IsNullOrEmpty(playerName);
    }

    public static string EncodeKicked(string reason) => $"{KickedTag}|{Escape(reason)}";

    public static bool TryParseKicked(string line, out string reason)
    {
        reason = "";
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 2 || parts[0] != KickedTag) return false;
        reason = parts[1];
        return true;
    }

    // ---- Presence: hello (client announces itself), bye (clean disconnect), join (server tells
    // the rest of the room). See the class doc comment for how these fit together. ----

    public static string EncodeHello(string roomPassword, string playerName, string? color) =>
        $"{HelloTag}|{Escape(roomPassword)}|{Escape(playerName)}|{ColorOrDefault(color)}";

    public static bool TryParseHello(string line, out string roomPassword, out string playerName, out string? color)
    {
        roomPassword = "";
        playerName = "";
        color = null;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 3 || parts[0] != HelloTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2])) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        color = parts.Length > 3 ? NormalizeColor(parts[3]) : null;
        return true;
    }

    public static string EncodeBye(string roomPassword, string playerName) =>
        $"{ByeTag}|{Escape(roomPassword)}|{Escape(playerName)}";

    public static bool TryParseBye(string line, out string roomPassword, out string playerName)
    {
        roomPassword = "";
        playerName = "";
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 3 || parts[0] != ByeTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || string.IsNullOrEmpty(parts[2])) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        return true;
    }

    public static string EncodeJoin(string playerName, string? color) =>
        $"{JoinTag}|{Escape(playerName)}|{ColorOrDefault(color)}";

    public static bool TryParseJoin(string line, out string playerName, out string? color)
    {
        playerName = "";
        color = null;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 2 || parts[0] != JoinTag || string.IsNullOrEmpty(parts[1])) return false;
        playerName = parts[1];
        color = parts.Length > 2 ? NormalizeColor(parts[2]) : null;
        return true;
    }

    // ---- Room access check. Deliberately a trade-off the user asked for: an unknown room
    // password now gets an explicit DENIED instead of silence, so the app can tell the player
    // their connection didn't work. This does make "is this password valid" answerable by
    // anyone who can send UDP packets - acceptable for this small friend-group deployment. The
    // app itself never says the *password* is wrong, only "contact the map admin" (error 105).
    // Name-independent on purpose: the check works even before the client knows its own name
    // (no display name set and no position/character name received yet). ----

    // Optional trailing player name (when the client already knows it): lets the server refuse a
    // name that's already taken in that room right at connect (AccessResult.NameTaken). An older
    // server ignores the extra field.
    public static string EncodeAccessQuery(string roomPassword, string? playerName = null) =>
        playerName is null ? $"{AccessQueryTag}|{Escape(roomPassword)}" : $"{AccessQueryTag}|{Escape(roomPassword)}|{Escape(playerName)}";

    public static bool TryParseAccessQuery(string line, out string roomPassword, out string? playerName)
    {
        roomPassword = "";
        playerName = null;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 2 || parts[0] != AccessQueryTag || string.IsNullOrEmpty(parts[1])) return false;
        roomPassword = parts[1];
        if (parts.Length > 2 && IsVisibleName(parts[2])) playerName = parts[2];
        return true;
    }

    public enum AccessResult { Granted, Denied, NameTaken }

    public static string EncodeAccessReply(AccessResult result) => $"{AccessReplyTag}|" + result switch
    {
        AccessResult.Granted => "OK",
        AccessResult.NameTaken => "NAME_TAKEN",
        _ => "DENIED",
    };

    public static bool TryParseAccessReply(string line, out AccessResult result)
    {
        result = AccessResult.Denied;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 2 || parts[0] != AccessReplyTag) return false;
        result = parts[1] switch { "OK" => AccessResult.Granted, "NAME_TAKEN" => AccessResult.NameTaken, _ => AccessResult.Denied };
        return true;
    }

    // ---- Name uniqueness, per room: the server keys sessions by name, so a second client using
    // a name that's already active in the same room would silently take over that session's
    // endpoint (both clients then get each other's traffic only intermittently - the real bug
    // behind "the shared marker shows up 2-3s late for the other map"). The server drops such a
    // client's messages and tells it with NameTakenTag instead. Other rooms are unaffected. ----

    public static string EncodeNameTaken(string playerName) => $"{NameTakenTag}|{Escape(playerName)}";

    public static bool TryParseNameTaken(string line, out string playerName)
    {
        playerName = "";
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 2 || parts[0] != NameTakenTag) return false;
        playerName = parts[1];
        return true;
    }

    // ---- Chat: room-scoped, fire-and-forget like report/broadcast (no delivery confirmation,
    // no persistence anywhere - see RelayServer's doc comment). Message text is base64'd rather
    // than pipe-escaped like names/passwords, so it can carry arbitrary Unicode (Czech diacritics,
    // emoji, literal '|' characters) without corruption. The sender's color isn't part of
    // ChatSend - the server already knows it from the session's hello/report and stamps it onto
    // the broadcast. ----

    // Private messages (old UOAM's "--name>text", here "-c name>text" typed in the game): an
    // optional trailing target name on ChatSend; the server then delivers it to that one player
    // only, and the broadcast carries a trailing "P" so the receiver can show it as private.
    // NOTE: an older server ignores the target field and would relay a private message to the
    // whole room - deploy the server before shipping a client that sends one.

    public static string EncodeChatSend(string roomPassword, string playerName, string message, string? toPlayer = null) =>
        $"{ChatSendTag}|{Escape(roomPassword)}|{Escape(playerName)}|{EncodeText(message)}" +
        (toPlayer is null ? "" : $"|{Escape(toPlayer)}");

    public static bool TryParseChatSend(string line, out string roomPassword, out string playerName, out string message, out string? toPlayer)
    {
        roomPassword = "";
        playerName = "";
        message = "";
        toPlayer = null;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 4 || parts[0] != ChatSendTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2])) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        message = DecodeText(parts[3]);
        if (parts.Length > 4 && IsVisibleName(parts[4])) toPlayer = parts[4];
        return !string.IsNullOrEmpty(message);
    }

    public static string EncodeChatBroadcast(string playerName, string message, string? color = null, bool isPrivate = false) =>
        $"{ChatBroadcastTag}|{Escape(playerName)}|{EncodeText(message)}|{ColorOrDefault(color)}" + (isPrivate ? "|P" : "");

    public static bool TryParseChatBroadcast(string line, out string playerName, out string message, out string? color, out bool isPrivate)
    {
        playerName = "";
        message = "";
        color = null;
        isPrivate = false;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 3 || parts[0] != ChatBroadcastTag) return false;
        if (string.IsNullOrEmpty(parts[1])) return false;
        playerName = parts[1];
        message = DecodeText(parts[2]);
        color = parts.Length > 3 ? NormalizeColor(parts[3]) : null;
        isPrivate = parts.Length > 4 && parts[4] == "P";
        return !string.IsNullOrEmpty(message);
    }

    // ---- Panic! mode (old UOAM's "-panic"/"-unpanic"), held per ROOM like the shared marker
    // (user's call, 2026-09-25): at most one panic at a time, and anyone in the room can turn it
    // off (people forget to). "On" makes the sender the room's panicking player, replacing
    // someone else's panic; "off" clears the room's panic whoever set it. Memory only. The server
    // sends a change to the WHOLE room including the sender (every app mirrors the server);
    // a set that changes nothing is answered to the sender only. Newcomers get the current
    // state, the periodic sweep re-sends it (Notify = false, also "none"), and it is cleared
    // when the panicking player leaves. ----

    /// <param name="Panicker">Who is in panic, or null for none.</param>
    /// <param name="Actor">For "none": who turned it off (the leaving player when they left).</param>
    /// <param name="Previous">For "none": whose panic was turned off (null = there was none).</param>
    public sealed record PanicState(string? Panicker, bool Notify, string Actor = "", string? Previous = null);

    public static string EncodePanicSet(string roomPassword, string playerName, bool on) =>
        $"{PanicSetTag}|{Escape(roomPassword)}|{Escape(playerName)}|{(on ? 1 : 0)}";

    public static bool TryParsePanicSet(string line, out string roomPassword, out string playerName, out bool on)
    {
        roomPassword = ""; playerName = ""; on = false;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 4 || parts[0] != PanicSetTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2])) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        on = parts[3] == "1";
        return true;
    }

    public static string EncodePanicState(PanicState state) =>
        state.Panicker is { } name
            ? $"{PanicStateTag}|SET|{Escape(name)}|{(state.Notify ? 1 : 0)}"
            : $"{PanicStateTag}|NONE|{(state.Notify ? 1 : 0)}|{Escape(state.Actor)}|{Escape(state.Previous ?? "")}";

    public static bool TryParsePanicState(string line, out PanicState state)
    {
        state = null!;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 4 || parts[0] != PanicStateTag) return false;
        if (parts[1] == "SET" && !string.IsNullOrEmpty(parts[2]))
        {
            state = new PanicState(parts[2], parts[3] == "1", parts[2]);
            return true;
        }
        if (parts[1] == "NONE" && parts.Length >= 5)
        {
            state = new PanicState(null, parts[2] == "1", parts[3], parts[4].Length > 0 ? parts[4] : null);
            return true;
        }
        return false;
    }

    // ---- Track reports ("-t name, name" in the game, user's request 2026-09-30): the players a
    // Tracking skill use found, and where the reporter stood (tracking finds people around you).
    // Nothing is kept: the server stamps the reporter's color on and passes the report to the
    // whole room INCLUDING the reporter (same as panic - every app shows what the server sent).
    // Names travel base64'd and newline-separated: they may contain spaces ("Sir Lancelot") and
    // anything else; both sides clean them (TrackNames). ----

    public const int MaxTrackNames = 20;
    public const int MaxTrackNameLength = 40;

    public sealed record TrackReport(string Reporter, int X, int Y, int Map, IReadOnlyList<string> Names, string Color);

    /// <summary>Trimmed, control characters replaced, cut to MaxTrackNameLength, invisible ones and
    /// duplicates (ignoring case) dropped, at most MaxTrackNames.</summary>
    public static List<string> TrackNames(IEnumerable<string> names)
    {
        var result = new List<string>();
        foreach (string raw in names)
        {
            string? clean = SanitizeMarkName(raw);
            if (clean is null) continue;
            if (clean.Length > MaxTrackNameLength) clean = clean[..MaxTrackNameLength].TrimEnd();
            if (!IsVisibleName(clean) || result.Contains(clean, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(clean);
            if (result.Count == MaxTrackNames) break;
        }
        return result;
    }

    private static bool IsValidTrackPosition(int x, int y, int map) => x is >= 0 and <= 65535 && y is >= 0 and <= 65535 && map is >= 0 and <= 255;

    public static string EncodeTrackReport(string roomPassword, string playerName, int x, int y, int map, IReadOnlyList<string> names) =>
        $"{TrackReportTag}|{Escape(roomPassword)}|{Escape(playerName)}|{x}|{y}|{map}|{EncodeText(string.Join('\n', names))}";

    public static bool TryParseTrackReport(string line, out string roomPassword, out string playerName, out int x, out int y, out int map, out List<string> names)
    {
        roomPassword = ""; playerName = ""; x = y = map = 0; names = [];
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 7 || parts[0] != TrackReportTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2])) return false;
        if (!int.TryParse(parts[3], out x) || !int.TryParse(parts[4], out y) || !int.TryParse(parts[5], out map) || !IsValidTrackPosition(x, y, map)) return false;
        names = TrackNames(DecodeText(parts[6]).Split('\n'));
        if (names.Count == 0) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        return true;
    }

    public static string EncodeTrackBroadcast(TrackReport r) =>
        $"{TrackBroadcastTag}|{Escape(r.Reporter)}|{r.X}|{r.Y}|{r.Map}|{EncodeText(string.Join('\n', r.Names))}|{ColorOrDefault(r.Color)}";

    public static bool TryParseTrackBroadcast(string line, out TrackReport report)
    {
        report = null!;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 6 || parts[0] != TrackBroadcastTag || !IsVisibleName(parts[1])) return false;
        if (!int.TryParse(parts[2], out int x) || !int.TryParse(parts[3], out int y) || !int.TryParse(parts[4], out int map) || !IsValidTrackPosition(x, y, map)) return false;
        var names = TrackNames(DecodeText(parts[5]).Split('\n'));
        if (names.Count == 0) return false;
        report = new TrackReport(parts[1], x, y, map, names, ColorOrDefault(parts.Length > 6 ? parts[6] : null));
        return true;
    }

    // ---- Shared marker (old UOAM's "Drop or Pick Up Shared Marker"): at most one per room,
    // anyone in the room can drop or pick it up, held only in the server's memory. The server
    // answers every drop/pickup with a state message to the WHOLE room, sender included, so every
    // client (incl. the one that dropped it) renders exactly the server's state. The state is also
    // re-sent in the periodic sweep and to newcomers (Notify = false), so a lost datagram can't
    // leave anyone with a stale marker; Notify = true only for the live drop/pickup itself (what
    // triggers the in-game "Shared Marker" + direction text). ----

    public sealed record SharedMarker(int X, int Y, int Map, string DroppedBy);

    public static string EncodeSharedMarkerDrop(string roomPassword, string playerName, int x, int y, int map) =>
        $"{SharedMarkerDropTag}|{Escape(roomPassword)}|{Escape(playerName)}|{x}|{y}|{map}";

    public static bool TryParseSharedMarkerDrop(string line, out string roomPassword, out string playerName, out int x, out int y, out int map)
    {
        roomPassword = ""; playerName = ""; x = y = map = 0;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 6 || parts[0] != SharedMarkerDropTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2])) return false;
        if (!int.TryParse(parts[3], out x) || !int.TryParse(parts[4], out y) || !int.TryParse(parts[5], out map)) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        return true;
    }

    public static string EncodeSharedMarkerPickup(string roomPassword, string playerName) =>
        $"{SharedMarkerPickupTag}|{Escape(roomPassword)}|{Escape(playerName)}";

    public static bool TryParseSharedMarkerPickup(string line, out string roomPassword, out string playerName)
    {
        roomPassword = ""; playerName = "";
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 3 || parts[0] != SharedMarkerPickupTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2])) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        return true;
    }

    /// <param name="marker">The room's current marker, or null for "none".</param>
    /// <param name="actor">Who dropped/picked it up (for a pickup, the marker is already null).</param>
    public static string EncodeSharedMarkerState(SharedMarker? marker, string actor, bool notify) =>
        marker is null
            ? $"{SharedMarkerStateTag}|NONE|{Escape(actor)}|{(notify ? 1 : 0)}"
            : $"{SharedMarkerStateTag}|SET|{marker.X}|{marker.Y}|{marker.Map}|{Escape(marker.DroppedBy)}|{(notify ? 1 : 0)}";

    public static bool TryParseSharedMarkerState(string line, out SharedMarker? marker, out string actor, out bool notify)
    {
        marker = null; actor = ""; notify = false;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 2 || parts[0] != SharedMarkerStateTag) return false;
        if (parts[1] == "NONE" && parts.Length >= 4)
        {
            actor = parts[2];
            notify = parts[3] == "1";
            return true;
        }
        if (parts[1] == "SET" && parts.Length >= 7
            && int.TryParse(parts[2], out int x) && int.TryParse(parts[3], out int y) && int.TryParse(parts[4], out int map))
        {
            actor = parts[5];
            marker = new SharedMarker(x, y, map, actor);
            notify = parts[6] == "1";
            return true;
        }
        return false;
    }

    // ---- Shared markers (user's request 2026-09-29): a player shares some of their own map
    // markers (one, or a whole category) with the room. Memory only on the server, per room and
    // per owner; they disappear when the owner stops sharing them or leaves. Receivers see them
    // hidden by default and may save them into their own shared_markers.map.
    //
    // Sync over lossy UDP: every change to an owner's set bumps that set's version on the server,
    // and each live MarkTag / MarkRemovedTag carries the new version. A client applies a live
    // change only if it's exactly the next version; any gap (a lost datagram) - or a periodic
    // MarkManifestTag whose version/count doesn't match - makes it ask for a full resync
    // (MarkResyncTag), which the server answers with MarkResyncBeginTag + every marker at the
    // current version. Newcomers get that same resync for every owner right away.
    //
    // Nothing from the wire is trusted: ids, coordinates, icon and name are validated on both the
    // server and the client (SanitizeMarkName strips control characters - a line break in a name
    // would otherwise inject extra lines into a .map file on save), and the server caps each
    // player's set at MaxSharedMarksPerPlayer. ----

    public const int MaxSharedMarksPerPlayer = 1000;
    public const int MaxSharedMarkNameLength = 80;
    public const int MaxSharedMarkIconLength = 40;
    public const string AllMarks = "*";

    public sealed record SharedMark(string Id, int X, int Y, int Map, string Icon, string Name);

    /// <summary>A marker's id: derived from its content, so sharing the same marker again is an
    /// idempotent update rather than a duplicate, and every client computes the same id.</summary>
    public static string SharedMarkId(int x, int y, int map, string icon, string name)
    {
        byte[] hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes($"{x}|{y}|{map}|{icon}|{name}"));
        return Convert.ToHexString(hash, 0, 6);
    }

    /// <summary>A marker as sent/received, or null if anything is out of range. Name and icon are
    /// cleaned (see SanitizeMarkName/SanitizeMarkIcon), not rejected, when merely odd.</summary>
    public static SharedMark? CreateSharedMark(string id, int x, int y, int map, string? icon, string? name)
    {
        if (!IsValidMarkId(id) || x is < 0 or > 65535 || y is < 0 or > 65535 || map is < 0 or > 255) return null;
        string? cleanName = SanitizeMarkName(name);
        return cleanName is null ? null : new SharedMark(id, x, y, map, SanitizeMarkIcon(icon), cleanName);
    }

    public static bool IsValidMarkId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 16 && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>Control/format characters (line breaks above all) become spaces; trimmed and
    /// truncated. Null for a name that would be invisible.</summary>
    public static string? SanitizeMarkName(string? name)
    {
        if (name is null) return null;
        var sb = new StringBuilder(Math.Min(name.Length, MaxSharedMarkNameLength));
        foreach (char ch in name)
        {
            bool unsafeChar = char.IsControl(ch) || char.GetUnicodeCategory(ch) is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            sb.Append(unsafeChar ? ' ' : ch);
        }
        string clean = sb.ToString().Trim();
        if (clean.Length > MaxSharedMarkNameLength) clean = clean[..MaxSharedMarkNameLength].TrimEnd();
        return IsVisibleName(clean) ? clean : null;
    }

    /// <summary>Letters, digits, space, '-' and '_' only (an icon name ends at ':' in a .map line).</summary>
    public static string SanitizeMarkIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return "";
        string clean = new string(icon.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_').ToArray()).Trim();
        return clean.Length > MaxSharedMarkIconLength ? clean[..MaxSharedMarkIconLength].TrimEnd() : clean;
    }

    public static string EncodeMarkShare(string roomPassword, string playerName, SharedMark m) =>
        $"{MarkShareTag}|{Escape(roomPassword)}|{Escape(playerName)}|{m.Id}|{m.X}|{m.Y}|{m.Map}|{Escape(m.Icon)}|{EncodeText(m.Name)}";

    public static bool TryParseMarkShare(string line, out string roomPassword, out string playerName, out SharedMark mark)
    {
        roomPassword = ""; playerName = ""; mark = null!;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 9 || parts[0] != MarkShareTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2])) return false;
        if (!int.TryParse(parts[4], out int x) || !int.TryParse(parts[5], out int y) || !int.TryParse(parts[6], out int map)) return false;
        if (CreateSharedMark(parts[3], x, y, map, parts[7], DecodeText(parts[8])) is not { } parsed) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        mark = parsed;
        return true;
    }

    public static string EncodeMarkUnshare(string roomPassword, string playerName, string id) =>
        $"{MarkUnshareTag}|{Escape(roomPassword)}|{Escape(playerName)}|{id}";

    public static bool TryParseMarkUnshare(string line, out string roomPassword, out string playerName, out string id)
    {
        roomPassword = ""; playerName = ""; id = "";
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 4 || parts[0] != MarkUnshareTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2])) return false;
        if (parts[3] != AllMarks && !IsValidMarkId(parts[3])) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        id = parts[3];
        return true;
    }

    public static string EncodeMarkResync(string roomPassword, string playerName, string owner) =>
        $"{MarkResyncTag}|{Escape(roomPassword)}|{Escape(playerName)}|{Escape(owner)}";

    public static bool TryParseMarkResync(string line, out string roomPassword, out string playerName, out string owner)
    {
        roomPassword = ""; playerName = ""; owner = "";
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 4 || parts[0] != MarkResyncTag) return false;
        if (string.IsNullOrEmpty(parts[1]) || !IsVisibleName(parts[2]) || !IsVisibleName(parts[3])) return false;
        roomPassword = parts[1];
        playerName = parts[2];
        owner = parts[3];
        return true;
    }

    /// <param name="notify">True for a live share (receivers get a "X sdílí N markerů" note);
    /// false for a resync.</param>
    public static string EncodeMark(string owner, int version, SharedMark m, bool notify) =>
        $"{MarkTag}|{Escape(owner)}|{version}|{m.Id}|{m.X}|{m.Y}|{m.Map}|{Escape(m.Icon)}|{EncodeText(m.Name)}|{(notify ? 1 : 0)}";

    public static bool TryParseMark(string line, out string owner, out int version, out SharedMark mark, out bool notify)
    {
        owner = ""; version = 0; mark = null!; notify = false;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 10 || parts[0] != MarkTag || !IsVisibleName(parts[1])) return false;
        if (!int.TryParse(parts[2], out version)) return false;
        if (!int.TryParse(parts[4], out int x) || !int.TryParse(parts[5], out int y) || !int.TryParse(parts[6], out int map)) return false;
        if (CreateSharedMark(parts[3], x, y, map, parts[7], DecodeText(parts[8])) is not { } parsed) return false;
        owner = parts[1];
        mark = parsed;
        notify = parts[9] == "1";
        return true;
    }

    public static string EncodeMarkRemoved(string owner, int version, string id) =>
        $"{MarkRemovedTag}|{Escape(owner)}|{version}|{id}";

    public static bool TryParseMarkRemoved(string line, out string owner, out int version, out string id)
    {
        owner = ""; version = 0; id = "";
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 4 || parts[0] != MarkRemovedTag || !IsVisibleName(parts[1])) return false;
        if (!int.TryParse(parts[2], out version)) return false;
        if (parts[3] != AllMarks && !IsValidMarkId(parts[3])) return false;
        owner = parts[1];
        id = parts[3];
        return true;
    }

    public static string EncodeMarkManifest(string owner, int version, int count) =>
        $"{MarkManifestTag}|{Escape(owner)}|{version}|{count}";

    public static bool TryParseMarkManifest(string line, out string owner, out int version, out int count) =>
        TryParseOwnerVersionCount(line, MarkManifestTag, out owner, out version, out count);

    public static string EncodeMarkResyncBegin(string owner, int version, int count) =>
        $"{MarkResyncBeginTag}|{Escape(owner)}|{version}|{count}";

    public static bool TryParseMarkResyncBegin(string line, out string owner, out int version, out int count) =>
        TryParseOwnerVersionCount(line, MarkResyncBeginTag, out owner, out version, out count);

    private static bool TryParseOwnerVersionCount(string line, string tag, out string owner, out int version, out int count)
    {
        owner = ""; version = 0; count = 0;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 4 || parts[0] != tag || !IsVisibleName(parts[1])) return false;
        if (!int.TryParse(parts[2], out version) || !int.TryParse(parts[3], out count) || count < 0) return false;
        owner = parts[1];
        return true;
    }

    private static string EncodeText(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string DecodeText(string encoded)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch (FormatException) { return ""; }
    }

    // ---- Admin protocol: create/list rooms. Separate from the report/broadcast/leave messages
    // above because it's request/response (needs a result), not fire-and-forget. ----

    public static string EncodeAdminCreate(string adminPassword, string roomName, string roomPassword) =>
        $"{AdminCommandTag}|{Escape(adminPassword)}|CREATE|{Escape(roomName)}|{Escape(roomPassword)}";

    public static string EncodeAdminList(string adminPassword) =>
        $"{AdminCommandTag}|{Escape(adminPassword)}|LIST";

    public static string EncodeAdminDelete(string adminPassword, string roomName) =>
        $"{AdminCommandTag}|{Escape(adminPassword)}|DELETE|{Escape(roomName)}";

    // Room management v2 (2026-10-01): rooms have an id, a source (admin / player) and dates, and
    // players can create rooms themselves (RoomCreateTag). The admin sees the list page by page
    // (a whole list could outgrow one datagram), deletes by id (names aren't unique) and switches
    // player room creation on/off. The old CREATE/LIST/DELETE stay for older app builds.

    /// <summary>Rooms per ROOMS reply page; each room is <see cref="AdminRoomFields"/> fields.</summary>
    public const int AdminRoomsPageSize = 40;
    public const int AdminRoomFields = 5;

    public static string EncodeAdminRooms(string adminPassword, int offset) =>
        $"{AdminCommandTag}|{Escape(adminPassword)}|ROOMS|{offset}";

    public static string EncodeAdminDeleteIds(string adminPassword, IEnumerable<string> ids) =>
        $"{AdminCommandTag}|{Escape(adminPassword)}|DELETEID|{string.Join(',', ids.Select(Escape))}";

    public static string EncodeAdminGetSettings(string adminPassword) =>
        $"{AdminCommandTag}|{Escape(adminPassword)}|SETTINGS";

    public static string EncodeAdminSetCreation(string adminPassword, bool enabled) =>
        $"{AdminCommandTag}|{Escape(adminPassword)}|SETCREATE|{(enabled ? 1 : 0)}";

    /// <summary>One room in the admin list: never its password or players.</summary>
    public sealed record AdminRoomInfo(string Id, string Name, bool CreatedByPlayer, DateTimeOffset CreatedUtc, DateTimeOffset LastUsedUtc);

    public static string[] EncodeAdminRoomFields(AdminRoomInfo r) =>
        [r.Id, r.Name, r.CreatedByPlayer ? "player" : "admin", r.CreatedUtc.ToUnixTimeSeconds().ToString(), r.LastUsedUtc.ToUnixTimeSeconds().ToString()];

    public static AdminRoomInfo? TryDecodeAdminRoomFields(ReadOnlySpan<string> f)
    {
        if (f.Length < AdminRoomFields || !long.TryParse(f[3], out long created) || !long.TryParse(f[4], out long used)) return null;
        return new AdminRoomInfo(f[0], f[1], f[2] == "player",
            DateTimeOffset.FromUnixTimeSeconds(created), DateTimeOffset.FromUnixTimeSeconds(used));
    }

    // ---- Player room creation: request/response like the admin protocol, but no admin password.
    // The server picks the password (a player-chosen one would leak whether a password is taken,
    // i.e. let anyone probe for other rooms), rate-limits per IP and can be switched off. ----

    public const string RoomCreateTag = "UOAMNC1";      // client -> server: "create a room named X"
    public const string RoomCreateReplyTag = "UOAMNR1"; // server -> client: OK + password, or ERR + reason

    public const int MaxRoomNameLength = 40;

    /// <summary>Player-created rooms nobody joined for this long are deleted by the server.</summary>
    public const int PlayerRoomExpiryDays = 90;

    public static string EncodeRoomCreate(string requestId, string roomName) =>
        $"{RoomCreateTag}|{Escape(requestId)}|{EncodeText(roomName)}";

    public static bool TryParseRoomCreate(string line, out string requestId, out string roomName)
    {
        requestId = ""; roomName = "";
        string[] p = line.Trim().Split('|');
        if (p.Length < 3 || p[0] != RoomCreateTag || p[1].Length is 0 or > 32) return false;
        requestId = p[1];
        roomName = DecodeText(p[2]);
        return true;
    }

    public static string EncodeRoomCreateOk(string requestId, string password) =>
        $"{RoomCreateReplyTag}|{Escape(requestId)}|OK|{Escape(password)}";

    public static string EncodeRoomCreateErr(string requestId, string reason) =>
        $"{RoomCreateReplyTag}|{Escape(requestId)}|ERR|{Escape(reason)}";

    public readonly record struct RoomCreateReply(string RequestId, bool Ok, string? Password, string? Error);

    public static bool TryParseRoomCreateReply(string line, out RoomCreateReply reply)
    {
        reply = default;
        string[] p = line.Trim().Split('|');
        if (p.Length < 4 || p[0] != RoomCreateReplyTag) return false;
        reply = p[2] == "OK" ? new RoomCreateReply(p[1], true, p[3], null) : new RoomCreateReply(p[1], false, null, p[3]);
        return true;
    }

    /// <summary>A room name as the server stores it: trimmed, control characters dropped, at most
    /// <see cref="MaxRoomNameLength"/> characters; null if nothing visible is left.</summary>
    public static string? CleanRoomName(string? name)
    {
        if (name is null) return null;
        string cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Replace('|', '_').Trim();
        if (cleaned.Length > MaxRoomNameLength) cleaned = cleaned[..MaxRoomNameLength].Trim();
        return IsVisibleName(cleaned) ? cleaned : null;
    }

    public readonly record struct AdminCommand(string AdminPassword, string Command, string[] Args);

    public static bool TryParseAdminCommand(string line, out AdminCommand command)
    {
        command = default;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 3 || parts[0] != AdminCommandTag) return false;
        command = new AdminCommand(parts[1], parts[2], parts.Length > 3 ? parts[3..] : []);
        return true;
    }

    public static string EncodeAdminOk(string command, params string[] data) =>
        $"{AdminResponseTag}|{command}|OK" + (data.Length > 0 ? "|" + string.Join('|', data.Select(Escape)) : "");

    public static string EncodeAdminErr(string command, string reason) =>
        $"{AdminResponseTag}|{command}|ERR|{Escape(reason)}";

    public readonly record struct AdminResponse(string Command, bool Ok, string? Error, string[] Data);

    public static bool TryParseAdminResponse(string line, out AdminResponse response)
    {
        response = default;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 3 || parts[0] != AdminResponseTag) return false;
        string command = parts[1];
        string status = parts[2];
        if (status == "OK")
        {
            response = new AdminResponse(command, true, null, parts.Length > 3 ? parts[3..] : []);
            return true;
        }
        if (status == "ERR")
        {
            response = new AdminResponse(command, false, parts.Length > 3 ? parts[3] : "UNKNOWN", []);
            return true;
        }
        return false;
    }

    /// <summary>Parses a player color as 6 hex digits ("RRGGBB", optional leading '#'), returning
    /// it upper-cased without the '#', or null if it isn't one. A malformed color is treated as
    /// "no color" rather than an error - it should never cost a position update.</summary>
    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        string c = color.Trim().TrimStart('#');
        if (c.Length != 6 || !c.All(Uri.IsHexDigit)) return null;
        return c.ToUpperInvariant();
    }

    private static string ColorOrDefault(string? color) => NormalizeColor(color) ?? DefaultPlayerColor;

    /// <summary>False for a name that would render as nothing on the map/in chat: empty, or made
    /// only of whitespace (space, tab, NBSP, ...), control/format characters (zero-width space,
    /// zero-width joiner, BOM, ...), combining marks with nothing to combine with, or the
    /// well-known blank-looking "filler" letters used to fake an empty name in games (Hangul
    /// fillers, Braille blank, Mongolian vowel separator). The client refuses to connect with such
    /// a name (MainWindow's error 103) and the server drops any hello/report/chat carrying one
    /// anyway, so an invisible player can't end up in a room either way.</summary>
    public static bool IsVisibleName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (char ch in name)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) continue;
            if (ch is 'ᅟ' or 'ᅠ' or 'ㅤ' or 'ﾠ' or '⠀' or '᠎') continue;
            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            return true;
        }
        return false;
    }

    // Names/passwords flow through a '|'-delimited line - reject the delimiter outright rather
    // than trying to escape it (simplest safe choice, these are just cosmetic/shared-secret
    // fields anyway, never parsed structurally beyond the split above).
    private static string Escape(string value) => value.Replace('|', '_');
}
