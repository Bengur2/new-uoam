using System.Collections.Concurrent;
using System.Net.Sockets;

namespace NewUOAM.Positioning.Relay;

public enum RelayConnectResult
{
    Connected,
    /// <summary>The server answered, but not with access to the room (unknown room password).</summary>
    Denied,
    /// <summary>Another active player in this room already uses this name (names are unique per
    /// room, not across rooms).</summary>
    NameTaken,
    /// <summary>No answer within RelayProtocol.AccessReplyTimeoutMs (server down, wrong address,
    /// firewall, or an old server build that doesn't know the access query).</summary>
    NoResponse,
}

/// <summary>Client side of the multiplayer relay (see NewUOAM.RelayServer / RelayProtocol):
/// forwards this player's own position to a central relay server and surfaces other connected
/// players' positions. Deliberately NOT an IPositionProvider - it doesn't produce THIS player's
/// position (that still comes from whichever local provider is active, e.g.
/// OrionUdpPositionProvider), it relays it onward and reports back what everyone else is doing.
/// </summary>
public sealed class RelayMultiplayerClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _roomPassword;
    private readonly string? _displayName;
    private volatile string _color;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _sendLoop;
    private PositionUpdate? _pendingLocal;
    // The name the server currently knows this client by (last one actually sent) - what a clean
    // disconnect's ByeTag has to name, even if CurrentPlayerName would resolve differently by now.
    private string? _registeredName;

    public event EventHandler<(string Name, PositionUpdate Update, string Color)>? RemotePlayerUpdated;
    public event EventHandler<(string Name, string Color)>? RemotePlayerJoined;
    public event EventHandler<string>? RemotePlayerLeft;
    public event EventHandler<string>? StatusMessage;
    /// <summary>The server removed us (room deleted by the admin). Carries a user-facing message.
    /// The owner should tear this client down (StopAsync + Dispose) - it keeps running otherwise,
    /// reporting into a room that no longer exists.</summary>
    public event EventHandler<string>? Kicked;
    /// <summary>The server refused our name: another player in this room is already using it
    /// (detected after connecting - e.g. when the character name arrived later). Carries the
    /// name. The owner should tear this client down, same as for Kicked.</summary>
    public event EventHandler<string>? NameTaken;
    /// <summary>A chat message: someone else's, or our own echoed locally (see SendChatMessage).
    /// Private = a private message; To is its recipient for our own echo, null when it was sent
    /// to us.</summary>
    public event EventHandler<ChatMessage>? ChatMessageReceived;
    /// <summary>The room's panic as the server holds it (one per room). Notify is true only for a
    /// live change (or the answer to our own set); false for the periodic re-sync, a newcomer's
    /// catch-up and a panicking player leaving.</summary>
    public event EventHandler<RelayProtocol.PanicState>? PanicChanged;

    /// <summary>Someone in the room (us included) reported a track ("-t"). Raised on the receive
    /// thread.</summary>
    public event EventHandler<RelayProtocol.TrackReport>? TrackReported;

    public sealed record ChatMessage(string Name, string Message, string Color, bool Private = false, string? To = null);

    /// <summary>The name this client reports under, or null while it doesn't know it yet.</summary>
    public string? PlayerName => CurrentPlayerName;
    /// <summary>The room's shared marker as the server holds it (null = none). Raised for every
    /// state message, including the periodic re-sync - Notify is true only for a live drop/pickup
    /// (Actor did it), which is when the in-game "Shared Marker" text should appear.</summary>
    public event EventHandler<(RelayProtocol.SharedMarker? Marker, string Actor, bool Notify)>? SharedMarkerChanged;

    /// <summary>This client's own color ("RRGGBB") - what the server stamps onto its reports and
    /// chat messages for everyone else. Can be changed while connected: every report/hello carries
    /// it and the server updates the session's color from each one, so others see the change
    /// within one report interval (or one hello interval while there's no position).</summary>
    public string Color
    {
        get => _color;
        set => _color = RelayProtocol.NormalizeColor(value) ?? RelayProtocol.DefaultPlayerColor;
    }

    /// <summary>The name this client currently reports under, or null while it has no name at all
    /// yet (no display name set and no position - hence no character name - received so far).
    /// Nothing is sent to the server until this is known: registering as a placeholder like
    /// "Player" and then switching to the real character name a moment later would look like two
    /// different people joining (and one of them "leaving" 15s later).</summary>
    private string? CurrentPlayerName
    {
        get
        {
            if (_displayName is not null) return _displayName;
            if (_pendingLocal is null) return null;
            string? characterName = _pendingLocal.CharacterName;
            return RelayProtocol.IsVisibleName(characterName) ? characterName!.Trim() : "Player";
        }
    }

    /// <param name="roomPassword">Which room to join on the relay - the server only relays
    /// reports between clients that share the same room password (see RelayServer). Required:
    /// a room must already exist on the server (created via the admin panel) or every report is
    /// silently dropped.</param>
    /// <param name="displayName">Overrides the name reported to the relay (and shown on other
    /// players' maps) instead of the local position source's own character name - e.g. so someone
    /// can show up as "GG" without renaming their actual UO character. Null/blank falls back to
    /// the character name (see CurrentPlayerName). The caller is expected to have rejected an
    /// invisible-but-non-empty name already (RelayProtocol.IsVisibleName) - here it's just treated
    /// as "not set".</param>
    /// <param name="color">This player's marker/chat color as "RRGGBB"; invalid/null falls back to
    /// RelayProtocol.DefaultPlayerColor.</param>
    public RelayMultiplayerClient(string host, int port, string roomPassword, string? displayName = null, string? color = null)
    {
        _host = host;
        _port = port;
        _roomPassword = roomPassword;
        _displayName = RelayProtocol.IsVisibleName(displayName) ? displayName!.Trim() : null;
        _color = RelayProtocol.NormalizeColor(color) ?? RelayProtocol.DefaultPlayerColor;
    }

    /// <summary>Opens the socket and asks the server whether the room password is valid
    /// (RelayProtocol.AccessQueryTag) before doing anything else. Only on <see
    /// cref="RelayConnectResult.Connected"/> does the client announce itself and start reporting;
    /// on any other result it has already shut itself down again - just Dispose it. Tip: call
    /// ReportLocalPosition with the latest known local position BEFORE this, so the very first
    /// hello already carries the character name and the first report goes out right away, even if
    /// the player is standing still (position providers only push on change/heartbeat).</summary>
    public async Task<RelayConnectResult> StartAsync()
    {
        _udp = new UdpClient();
        // A newcomer's shared-marker catch-up (or a big shared category) arrives as a burst of
        // datagrams; the OS default buffer is small enough to drop some of them.
        _udp.Client.ReceiveBufferSize = 1024 * 1024;
        // Connect (not just remembering host/port ourselves) resolves DNS once up front instead
        // of on every send, and restricts ReceiveAsync to datagrams actually from this endpoint.
        _udp.Connect(_host, _port);

        _cts = new CancellationTokenSource();
        _accessReply = new TaskCompletionSource<RelayProtocol.AccessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        RelayProtocol.AccessResult? access = await QueryAccessAsync();
        if (access != RelayProtocol.AccessResult.Granted)
        {
            await _cts.CancelAsync();
            _udp.Close();
            try { await _receiveLoop; } catch (OperationCanceledException) { }
            _udp = null;
            return access switch
            {
                null => RelayConnectResult.NoResponse,
                RelayProtocol.AccessResult.NameTaken => RelayConnectResult.NameTaken,
                _ => RelayConnectResult.Denied,
            };
        }

        // Announce ourselves right away (if we already have a name) rather than waiting for the
        // first send-loop tick/position - the server answers a new session with the room's
        // roster immediately, which is what makes other players show up without a delay.
        SendHello();
        _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token));
        StatusMessage?.Invoke(this, CurrentPlayerName is null
            ? $"Multiplayer: připojeno k {_host}:{_port}. Čekám na jméno postavy - spusť sledování pozice, nebo vyplň zobrazované jméno."
            : $"Multiplayer: připojeno k {_host}:{_port}.");
        return RelayConnectResult.Connected;
    }

    private TaskCompletionSource<RelayProtocol.AccessResult>? _accessReply;

    /// <summary>Sends the access query (resent every AccessQueryRetryMs, since UDP may drop it)
    /// and waits up to AccessReplyTimeoutMs. Null = no reply at all.</summary>
    private async Task<RelayProtocol.AccessResult?> QueryAccessAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < RelayProtocol.AccessReplyTimeoutMs)
        {
            // With the name when already known, so a taken name is refused right here at connect.
            Send(RelayProtocol.EncodeAccessQuery(_roomPassword, CurrentPlayerName));
            int wait = (int)Math.Min(RelayProtocol.AccessQueryRetryMs, RelayProtocol.AccessReplyTimeoutMs - sw.ElapsedMilliseconds);
            if (wait <= 0) break;
            var finished = await Task.WhenAny(_accessReply!.Task, Task.Delay(wait));
            if (finished == _accessReply.Task) return _accessReply.Task.Result;
        }
        return _accessReply!.Task.IsCompleted ? _accessReply.Task.Result : null;
    }

    /// <summary>Synchronous best-effort leave notice for app shutdown, where there's no time to
    /// await StopAsync - without it, closing the app with the relay connected left this player's
    /// marker frozen on everyone else's map until the 15s session timeout.</summary>
    public void SendByeNow()
    {
        if (_udp is not null && _registeredName is not null)
            Send(RelayProtocol.EncodeBye(_roomPassword, _registeredName));
    }

    public async Task StopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_sendLoop is not null) { try { await _sendLoop; } catch (OperationCanceledException) { } }

        // Clean leave: lets the server tell everyone else right now, instead of them seeing our
        // marker frozen in place until the session times out (RelayProtocol.SessionTimeoutSeconds).
        // Sent after the send loop has stopped so no stray report can re-register us afterwards.
        if (_udp is not null && _registeredName is not null)
        {
            Send(RelayProtocol.EncodeBye(_roomPassword, _registeredName));
        }

        _udp?.Close();
        if (_receiveLoop is not null) { try { await _receiveLoop; } catch (OperationCanceledException) { } }
    }

    /// <summary>Call whenever the local player's position changes. Doesn't send immediately -
    /// just records the latest value for SendLoopAsync to pick up at RelayProtocol's fixed
    /// cadence (see its doc comment for why: throttling AND session keepalive in one).</summary>
    public void ReportLocalPosition(PositionUpdate update) => _pendingLocal = update;

    private async Task SendLoopAsync(CancellationToken ct)
    {
        long lastHelloTicks = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(RelayProtocol.ClientReportIntervalMs, ct); }
            catch (OperationCanceledException) { break; }

            if (_udp is null) continue;
            if (_pendingLocal is { } update)
            {
                string? name = CurrentPlayerName;
                if (name is null) continue;
                Send(RelayProtocol.EncodeReport(_roomPassword, name, update, _color));
                _registeredName = name;
            }
            else if (Environment.TickCount64 - lastHelloTicks >= RelayProtocol.ClientHelloIntervalMs)
            {
                // No position yet (no local provider running) - still keep the session alive so
                // chat and other players' positions keep flowing to us.
                lastHelloTicks = Environment.TickCount64;
                SendHello();
            }

            SendQueuedMarkChanges();
            FlushMarkAnnouncements();
        }
    }

    private void SendHello()
    {
        string? name = CurrentPlayerName;
        if (_udp is null || name is null) return;
        Send(RelayProtocol.EncodeHello(_roomPassword, name, _color));
        _registeredName = name;
    }

    private bool Send(string line)
    {
        if (_udp is null) return false;
        byte[] payload = RelayProtocol.WireEncoding.GetBytes(line);
        try
        {
            _udp.Send(payload, payload.Length);
            return true;
        }
        catch (SocketException ex)
        {
            StatusMessage?.Invoke(this, $"Multiplayer relay send error: {ex.Message}");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Sends a chat message into the current room immediately (not throttled like
    /// position reports - there's no keepalive reason to delay it). The server never echoes a
    /// sender's own message back (see RelayServer.BroadcastPayloadAsync's excludeName), so this
    /// raises ChatMessageReceived locally right away instead of waiting on a round trip.</summary>
    /// <param name="toPlayer">Private message to this one player of the room (exact name).</param>
    public void SendChatMessage(string message, string? toPlayer = null)
    {
        message = message.Trim();
        if (string.IsNullOrEmpty(message) || _udp is null) return;
        if (message.Length > RelayProtocol.MaxChatMessageLength) message = message[..RelayProtocol.MaxChatMessageLength];

        string? name = CurrentPlayerName;
        if (name is null)
        {
            StatusMessage?.Invoke(this, "Multiplayer: zprávu nelze odeslat - zatím neznám tvoje jméno (vyplň zobrazované jméno nebo spusť sledování pozice).");
            return;
        }
        // Make sure the server knows our color before the chat message it will stamp it onto.
        if (_registeredName != name) SendHello();
        if (!Send(RelayProtocol.EncodeChatSend(_roomPassword, name, message, toPlayer))) return;
        _registeredName = name;
        ChatMessageReceived?.Invoke(this, new ChatMessage(name, message, _color, toPlayer is not null, toPlayer));
    }

    /// <summary>On = this player is now the room's panic (replacing anyone else's); off = clears
    /// the room's panic, whoever's it is. Like the shared marker, nothing changes locally until the
    /// server's state comes back (PanicChanged).</summary>
    public bool SetPanic(bool on) =>
        SendAsNamed(name => RelayProtocol.EncodePanicSet(_roomPassword, name, on));

    /// <summary>"-t": the players a Tracking use found around (x, y). The report comes back to us
    /// from the server like to everyone else (TrackReported). False while our name is unknown.</summary>
    public bool ReportTrack(int x, int y, int map, IReadOnlyList<string> names) =>
        SendAsNamed(name => RelayProtocol.EncodeTrackReport(_roomPassword, name, x, y, map, names));

    /// <summary>Drops the room's shared marker at (x, y, map) - replacing any existing one. Nothing
    /// changes locally until the server's state message comes back (SharedMarkerChanged), so every
    /// client shows exactly the server's state.</summary>
    public bool DropSharedMarker(int x, int y, int map) =>
        SendAsNamed(name => RelayProtocol.EncodeSharedMarkerDrop(_roomPassword, name, x, y, map));

    public bool PickupSharedMarker() =>
        SendAsNamed(name => RelayProtocol.EncodeSharedMarkerPickup(_roomPassword, name));

    private bool SendAsNamed(Func<string, string> encode)
    {
        if (_udp is null) return false;
        string? name = CurrentPlayerName;
        if (name is null)
        {
            StatusMessage?.Invoke(this, "Multiplayer: zatím neznám tvoje jméno (vyplň zobrazované jméno nebo spusť sledování pozice).");
            return false;
        }
        if (_registeredName != name) SendHello();
        if (!Send(encode(name))) return false;
        _registeredName = name;
        return true;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        if (_udp is null) return;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                StatusMessage?.Invoke(this, $"Multiplayer relay receive error: {ex.Message}");
                continue;
            }

            string text = RelayProtocol.WireEncoding.GetString(result.Buffer);
            if (RelayProtocol.TryParseAccessReply(text, out var access))
                _accessReply?.TrySetResult(access);
            else if (RelayProtocol.TryParseNameTaken(text, out string takenName))
                NameTaken?.Invoke(this, takenName);
            else if (RelayProtocol.TryParseBroadcast(text, out string name, out var update, out string? color))
                RemotePlayerUpdated?.Invoke(this, (name, update, color ?? RelayProtocol.DefaultPlayerColor));
            else if (RelayProtocol.TryParseJoin(text, out string joinedName, out string? joinedColor))
                RemotePlayerJoined?.Invoke(this, (joinedName, joinedColor ?? RelayProtocol.DefaultPlayerColor));
            else if (RelayProtocol.TryParseLeave(text, out string leftName))
            {
                ForgetOwnerMarks(leftName); // their shared markers leave with them
                RemotePlayerLeft?.Invoke(this, leftName);
            }
            else if (RelayProtocol.TryParseMark(text, out string markOwner, out int markVersion, out var mark, out bool markNotify))
                OnMark(markOwner, markVersion, mark, markNotify);
            else if (RelayProtocol.TryParseMarkRemoved(text, out string removedOwner, out int removedVersion, out string removedId))
                OnMarkRemoved(removedOwner, removedVersion, removedId);
            else if (RelayProtocol.TryParseMarkManifest(text, out string manifestOwner, out int manifestVersion, out int manifestCount))
                OnMarkManifest(manifestOwner, manifestVersion, manifestCount);
            else if (RelayProtocol.TryParseMarkResyncBegin(text, out string beginOwner, out int beginVersion, out int beginCount))
                OnMarkResyncBegin(beginOwner, beginVersion, beginCount);
            else if (RelayProtocol.TryParseKicked(text, out string reason))
                Kicked?.Invoke(this, $"Multiplayer: byl(a) jsi odpojen(a) administrátorem - místnost byla smazána ({reason}).");
            else if (RelayProtocol.TryParseChatBroadcast(text, out string chatFrom, out string chatMessage, out string? chatColor, out bool chatPrivate))
                ChatMessageReceived?.Invoke(this, new ChatMessage(chatFrom, chatMessage, chatColor ?? RelayProtocol.DefaultPlayerColor, chatPrivate));
            else if (RelayProtocol.TryParseSharedMarkerState(text, out var marker, out string actor, out bool notify))
                SharedMarkerChanged?.Invoke(this, (marker, actor, notify));
            else if (RelayProtocol.TryParsePanicState(text, out var panic))
                PanicChanged?.Invoke(this, panic);
            else if (RelayProtocol.TryParseTrackBroadcast(text, out var track))
                TrackReported?.Invoke(this, track);
        }
    }

    // ---- Shared markers (see RelayProtocol.MarkShareTag for the protocol and its sync scheme).
    // Outgoing changes are queued and sent at most MarkSendBatch per send-loop tick (50ms), so
    // sharing a category of hundreds of markers is paced instead of one burst. Incoming sets are
    // kept per owner with the server's version; a gap or a mismatching manifest triggers a resync
    // request (at most one per owner per MarkResyncThrottleMs). ----

    private const int MarkSendBatch = 25;
    private const int MarkResyncThrottleMs = 1000;
    // Live shares are announced once the owner has been quiet this long, so a whole category
    // becomes one "X sdílí 46 markerů" note, not 46.
    private const int MarkAnnounceQuietMs = 700;

    private sealed class OwnerMarkState
    {
        public int Version;
        public Dictionary<string, RelayProtocol.SharedMark> Marks = new();
        public int PendingVersion;
        public int PendingCount;
        public Dictionary<string, RelayProtocol.SharedMark>? Pending;
        // Not long.MinValue: "now - MinValue" overflows to a negative number, which made the
        // throttle swallow every resync request (caught by the loopback test).
        public long ResyncRequestedAt = long.MinValue / 2;
    }

    private readonly Dictionary<string, OwnerMarkState> _ownerMarks = new(); // lock: itself
    private readonly ConcurrentDictionary<string, RelayProtocol.SharedMark> _myShared = new();
    private readonly ConcurrentQueue<Func<string, string>> _markOutbox = new();
    private readonly Dictionary<string, (int Count, long LastTicks)> _markAnnouncements = new(); // lock: _ownerMarks

    /// <summary>Someone's shared markers changed (added, removed, resynced, or they left). Carries
    /// the owner; read the current state with GetSharedMarks.</summary>
    public event EventHandler<string>? SharedMarksChanged;
    /// <summary>Someone just shared markers live - batched per owner (see MarkAnnounceQuietMs).</summary>
    public event EventHandler<(string Owner, int Count)>? SharedMarksAnnounced;
    /// <summary>The set of markers this client shares changed.</summary>
    public event EventHandler? MySharedMarksChanged;

    /// <summary>Everyone else's shared markers as this client currently knows them.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RelayProtocol.SharedMark>> GetSharedMarks()
    {
        lock (_ownerMarks)
            return _ownerMarks.Where(kv => kv.Value.Marks.Count > 0)
                .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<RelayProtocol.SharedMark>)kv.Value.Marks.Values.ToList());
    }

    public bool IsMarkShared(string id) => _myShared.ContainsKey(id);
    public int MySharedMarkCount => _myShared.Count;

    public void ShareMarks(IEnumerable<RelayProtocol.SharedMark> marks)
    {
        bool any = false;
        foreach (var mark in marks)
        {
            _myShared[mark.Id] = mark;
            var captured = mark;
            _markOutbox.Enqueue(name => RelayProtocol.EncodeMarkShare(_roomPassword, name, captured));
            any = true;
        }
        if (any) MySharedMarksChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnshareMarks(IEnumerable<string> ids)
    {
        bool any = false;
        foreach (string id in ids)
        {
            if (!_myShared.TryRemove(id, out _)) continue;
            _markOutbox.Enqueue(name => RelayProtocol.EncodeMarkUnshare(_roomPassword, name, id));
            any = true;
        }
        if (any) MySharedMarksChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnshareAllMarks()
    {
        if (_myShared.IsEmpty) return;
        _myShared.Clear();
        _markOutbox.Enqueue(name => RelayProtocol.EncodeMarkUnshare(_roomPassword, name, RelayProtocol.AllMarks));
        MySharedMarksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SendQueuedMarkChanges()
    {
        if (_markOutbox.IsEmpty || CurrentPlayerName is null) return;
        for (int i = 0; i < MarkSendBatch && _markOutbox.TryDequeue(out var encode); i++)
            SendAsNamed(encode);
    }

    private void FlushMarkAnnouncements()
    {
        List<(string, int)> due;
        lock (_ownerMarks)
        {
            if (_markAnnouncements.Count == 0) return;
            long now = Environment.TickCount64;
            due = _markAnnouncements.Where(kv => now - kv.Value.LastTicks >= MarkAnnounceQuietMs).Select(kv => (kv.Key, kv.Value.Count)).ToList();
            foreach (var (owner, _) in due) _markAnnouncements.Remove(owner);
        }
        foreach (var announcement in due) SharedMarksAnnounced?.Invoke(this, announcement);
    }

    private void OnMark(string owner, int version, RelayProtocol.SharedMark mark, bool notify)
    {
        bool changed = false, resync = false;
        lock (_ownerMarks)
        {
            var state = OwnerState(owner);
            if (state.Pending is not null && !notify && version == state.PendingVersion)
            {
                state.Pending[mark.Id] = mark;
                if (state.Pending.Count >= state.PendingCount) changed = CommitPending(state);
            }
            else if (version == state.Version + 1)
            {
                state.Marks[mark.Id] = mark;
                state.Version = version;
                changed = true;
                if (notify)
                {
                    _markAnnouncements.TryGetValue(owner, out var a);
                    _markAnnouncements[owner] = (a.Count + 1, Environment.TickCount64);
                }
            }
            else if (version == state.Version && !notify)
            {
                state.Marks[mark.Id] = mark; // a late copy from a resync that already completed
                changed = true;
            }
            else if (version > state.Version)
            {
                resync = true; // missed something
            }
        }
        if (changed) SharedMarksChanged?.Invoke(this, owner);
        if (resync) RequestMarkResync(owner);
    }

    private void OnMarkRemoved(string owner, int version, string id)
    {
        bool changed = false, resync = false;
        lock (_ownerMarks)
        {
            var state = OwnerState(owner);
            if (id == RelayProtocol.AllMarks)
            {
                // "All gone" is a complete state by itself - no need to be in sequence.
                changed = state.Marks.Count > 0;
                state.Marks.Clear();
                state.Version = Math.Max(state.Version, version);
                state.Pending = null;
            }
            else if (version == state.Version + 1)
            {
                changed = state.Marks.Remove(id);
                state.Version = version;
            }
            else if (version > state.Version)
            {
                resync = true;
            }
        }
        if (changed) SharedMarksChanged?.Invoke(this, owner);
        if (resync) RequestMarkResync(owner);
    }

    private void OnMarkManifest(string owner, int version, int count)
    {
        bool resync;
        lock (_ownerMarks)
        {
            _ownerMarks.TryGetValue(owner, out var state);
            int myVersion = state?.Version ?? 0, myCount = state?.Marks.Count ?? 0;
            bool waitingForIt = state?.Pending is not null && state.PendingVersion == version;
            resync = (myVersion != version || myCount != count) && !waitingForIt;
        }
        if (resync) RequestMarkResync(owner);
    }

    private void OnMarkResyncBegin(string owner, int version, int count)
    {
        bool changed = false;
        lock (_ownerMarks)
        {
            var state = OwnerState(owner);
            state.Pending = new Dictionary<string, RelayProtocol.SharedMark>();
            state.PendingVersion = version;
            state.PendingCount = count;
            if (count == 0) changed = CommitPending(state);
        }
        if (changed) SharedMarksChanged?.Invoke(this, owner);
    }

    /// <summary>Replaces the owner's set with a completed resync - unless a live change has
    /// already moved past it (the next manifest then asks again).</summary>
    private static bool CommitPending(OwnerMarkState state)
    {
        var pending = state.Pending!;
        state.Pending = null;
        if (state.PendingVersion < state.Version) return false;
        state.Marks = pending;
        state.Version = state.PendingVersion;
        return true;
    }

    private OwnerMarkState OwnerState(string owner)
    {
        if (!_ownerMarks.TryGetValue(owner, out var state)) _ownerMarks[owner] = state = new OwnerMarkState();
        return state;
    }

    private void ForgetOwnerMarks(string owner)
    {
        bool had;
        lock (_ownerMarks)
        {
            had = _ownerMarks.Remove(owner, out var state) && state.Marks.Count > 0;
            _markAnnouncements.Remove(owner);
        }
        if (had) SharedMarksChanged?.Invoke(this, owner);
    }

    private void RequestMarkResync(string owner)
    {
        lock (_ownerMarks)
        {
            var state = OwnerState(owner);
            long now = Environment.TickCount64;
            if (now - state.ResyncRequestedAt < MarkResyncThrottleMs) return;
            state.ResyncRequestedAt = now;
        }
        SendAsNamed(name => RelayProtocol.EncodeMarkResync(_roomPassword, name, owner));
    }

    public void Dispose()
    {
        _udp?.Dispose();
        _cts?.Dispose();
    }
}
