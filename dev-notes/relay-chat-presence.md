# Room chat, presence, colors, auto-connect, chat history

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

### Room chat (live text chat, no persistence anywhere)

Requested verbatim: chat for people in the same room, no logging of who-said-what, opens
automatically when the user clicks into the map (giving it focus) and starts typing, in a
resizable window whose elements scale with it down to some sane minimum.

- **Protocol** (`RelayProtocol.ChatSendTag`/`ChatBroadcastTag`, `UOAMCC1`/`UOAMCS1`): rides the
  exact same room-password isolation as position reports - a chat message into an unknown room
  password is silently dropped, same rule, same reason. The message body is **base64'd, not
  pipe-escaped** like names/passwords - chat text needs to carry arbitrary Unicode (Czech
  diacritics) and literal `|` characters without corruption, which the existing `Escape()` (just
  replacing `|` with `_`) would have mangled. `RelayProtocol.MaxChatMessageLength` (400 chars)
  truncates client-side before sending - no reason a position-relay tool's chat needs to carry
  more than that, and it keeps every packet comfortably inside one UDP datagram.
- **`Session.Last` is now nullable** (`PositionUpdate?`) - a session can be created purely by
  chat activity now, before the client has ever sent a position report (`RelayServer.TouchSession`,
  shared by both the report and chat code paths - registers/refreshes a session's endpoint and
  keepalive either way). `SweepLoopAsync`'s periodic full-roster re-broadcast skips any session
  whose `Last` is still null (nothing to re-broadcast yet).
- **A real, narrow, documented limitation, not a bug**: a client only starts *receiving* room
  broadcasts (position OR chat) once it has sent the server at least one packet itself - a
  freshly-connected client that hasn't sent anything yet isn't a registered `Session`, so an
  incoming broadcast has nowhere to deliver to. In this app's real usage this window is
  near-instant (position reports flow continuously the moment a provider is running, which is
  normally already true whenever someone would be chatting through the map). Verified/reproduced
  deliberately in `tools/UopProbe`'s test, not just reasoned about.
- **`RelayMultiplayerClient.SendChatMessage`** sends immediately (not throttled like position
  reports - no keepalive reason to delay a chat message) and **raises `ChatMessageReceived`
  locally right away for the sender's own message**, rather than waiting on a round trip - the
  server's `BroadcastPayloadAsync` already excludes the sender from its own broadcast (same
  pattern position reports use), so without this the sender would never see their own message
  echoed back. `CurrentPlayerName` (the same `_displayName ?? characterName ?? "Player"` fallback
  chain `SendLoopAsync` already used) is now a shared private property so both paths agree on
  "who am I" without duplicating the logic.
- **`ChatWindow`** (`NewUOAM.App`): a view of `MainWindow._chatHistory` (since 2026-09-29, see
  "Chat history and unread counter" below; it used to own its list and start empty on every
  open). `Grid` with a star-sized
  message row and an auto-sized input row so the message list actually grows/shrinks with the
  window (`RowDefinition Height="*"` / `"Auto"`) while `MinWidth`/`MinHeight` (260/180) stop it
  collapsing into something unusable - satisfies "prvky by se měly uzpůsobovat velikosti okna...
  nastavit nějakou hranici minima" directly.
- **Opening trigger** (`MainWindow.MapBorder_PreviewTextInput`): `MapBorder` (a plain `Border`)
  was made `Focusable="True"`; a click anywhere on the map (`MapBorder_MouseLeftButtonDown`, every
  click, not just map-only-mode drags) calls `MapBorder.Focus()`. Uses **`PreviewTextInput`, not
  `PreviewKeyDown`** - it hands over the actual composed Unicode character(s) (respecting
  Shift/CapsLock/keyboard layout) instead of a raw `Key` enum needing manual, error-prone
  reconstruction, and it naturally never fires for non-printable keys (Enter, Escape, arrows, Tab)
  - exactly "did the user start typing a message," nothing else, with no extra filtering needed.
  Gated on `_relayClient is not null` - matches the user's own framing ("je připojená k mapě, ať
  už korektně nebo dali špatné heslo") of "connected" meaning the local client is active, not that
  the room password was necessarily valid (the app has no way to know that either way - see the
  room-password design above). The triggering keystroke itself is forwarded into the freshly
  focused chat input (`ChatWindow.FocusInput(seedText)`) rather than dropped, so typing "ahoj"
  doesn't lose its first character.
- **Verified two ways**: a full in-process `tools/UopProbe` test against a real loopback
  `RelayServer` (self-echo, cross-room isolation, Unicode + literal-pipe round-trip, unknown-
  password silent drop, oversized-message truncation, and the receive-before-first-send
  limitation above - all passed) and a live smoke test against the real deployed server
  (`89.168.122.175:27980`, a throwaway room created and cleaned up afterward) confirming chat
  works end-to-end over the real internet path with correct Unicode. The interactive click-into-
  map-then-type UI trigger itself was **not** live-automated this round - the desktop was in
  active real use at the time (a foreground Jira tab), and simulating keyboard input would have
  stolen focus from it (see the "shared/live during working sessions" lesson elsewhere in this
  file) - it relies on standard, well-documented WPF `Focus()`/`PreviewTextInput` behavior plus
  the passing protocol/server/client tests above; ask the user to confirm the click-and-type flow
  directly.

### Event-driven presence, player colors, invisible-name rule (2026-09-23)

Requested because two freshly connected maps took seconds to see each other's position/chat.
Root cause: a client wasn't registered on the server until its first position report (needs a
running local provider), and a newcomer only learned about others at the next 3s full-roster
sweep; a disconnect was only noticed after the 15s timeout.
- **New messages** (`RelayProtocol`): `UOAMRH1` hello (client → server, sent at connect and every
  `ClientHelloIntervalMs` while there's no position yet), `UOAMRB1` bye (sent from
  `RelayMultiplayerClient.StopAsync`), `UOAMRJ1` join (server → rest of the room).
  `RelayServer.AnnounceJoinAsync` runs once per brand-new session, **scoped to that room only**:
  join notice + the newcomer's position to everyone else, every other member's last position to the
  newcomer. `HandleByeAsync` removes the session and broadcasts `UOAMRL1` immediately (only if the
  bye came from the session's current endpoint). The 3s sweep/15s timeout remain as the fallback.
  Measured in `tools/UopProbe`'s loopback test: roster arrives ~0ms after connect, leave ~16ms.
- The client sends **nothing** until it knows its name (`CurrentPlayerName` is null with no
  display name and no position yet) - registering as a placeholder and renaming a moment later
  would look like two people joining.
- **Colors**: optional trailing `RRGGBB` field on report/broadcast/hello/join/chat-broadcast
  (missing → `DefaultPlayerColor` cyan, so old/new builds interoperate). Chat send carries no color
  - the server stamps the session's color on. Picker = fixed readable palette `PlayerColors.Palette`
  (`AppSettings.MultiplayerColor`); applies to the remote dot/arrow/label and name+text in chat.
- **Wire encoding is now UTF-8** (`RelayProtocol.WireEncoding`, was ASCII - names like "Jiří"
  used to arrive as "Ond?ej"). ASCII-only traffic is byte-identical.
- **Invisible names**: `RelayProtocol.IsVisibleName` (whitespace, control/format chars, zero-width,
  Hangul/Braille fillers...). The app refuses to connect with **error 103** (checks the raw,
  untrimmed text - empty is still allowed = use character name); the server parsers drop such
  names too. Numbered connect errors in `MainWindow`: 101 bad address, 102 missing room password,
  103 invisible name, 104 connect failed - shown as a MessageBox + status bar.
- A received message or a join/leave notice never opens the chat window (user's request
  2026-09-25: they show in the game, the chat opens only by typing into the map).
- **Chat window details (2026-09-29, user's requests):**
  - Every line starts with its arrival time `HH:mm` (`ChatMessageViewModel.Time`).
  - There's no Send button any more; Enter sends.
  - 👥 opens `PlayersPopup`, fed by `ChatWindow.PlayersProvider` = `MainWindow.DescribeRoomPlayers`:
    you ("ty") first, then others in their color with distance and direction.
  - 🗑 clears this map's history.
- **Auto-connect (2026-09-29, user's request).** `AutoConnectRelayCheckBox` in Online
  (`AppSettings.AutoConnectRelay`, default off). After the startup map/marker load, `Loaded`
  calls `AutoConnectRelayAsync`, which uses the same `ConnectRelayAsync(automatic: true)` as the
  button.
  - In automatic mode errors never open a dialog. They go to the status bar with their number.
  - 101/102/103/105/107 are not retried (waiting won't fix the settings).
  - 106/104 (no answer, network failure, e.g. right after boot) are retried
    `AutoConnectAttempts` (5) times, `AutoConnectRetrySeconds` (20s) apart. The retry stops if
    the user connects by hand or unticks the option.
  - Verified with the real app (scratch build, settings backed up and restored): auto-connect
    succeeded against the live relay. Against a dead port it showed "chyba 106, zkusím to znovu
    … pokus 2/5", then 3/5, and no dialog.
- **Chat history and unread counter (2026-09-29, user's request).**
  - `MainWindow._chatHistory` keeps every chat line since connecting, open window or not:
    messages, private ones, join/leave, panic and shared-marker notices, via
    `AddChatLine`/`AddChatSystemLine`.
  - It's memory only and capped at `MaxChatHistory` (200). `DisconnectRelayAsync` clears it
    (Odpojit, kick), and so does closing the map. Nothing from before you connected, since the
    server stores nothing.
  - `ChatWindow` just displays it, so closing and reopening the chat loses nothing. Its 🗑
    button clears the history on this map only.
  - Messages from others (not your own echo, not system lines) that arrive while the window is
    closed count as unread. Opening the chat resets the count.
  - `ChatUnreadBadge` ("✉ N", 99+) shares one right-aligned `StackPanel` with `CompassTopRight`,
    left of the letter, so they can't overlap. Clicking it opens the chat. It handles button
    *down* and marks it handled, so the map's click/drag/double-click doesn't run too.
  - Toggle: `ShowUnreadChatCheckBox` in Online > Připojit k mapě (`AppSettings.ShowUnreadChat`,
    default on). `UpdateChatUnreadBadge` returns early while the badge is still null, because
    the checkbox's Checked handler fires during `InitializeComponent`.
  - Verified in the real app via UI Automation: connected to room "test", a scripted TestBot
    sent 2 messages, the badge read "2", and a `PrintWindow` screenshot showed it next to "N"
    without overlap. Not automated: clicking the badge, the history showing up in a newly
    opened chat, and 🗑.
- `ChatWindow` no longer has `Owner = MainWindow`. An owned window always stays above its owner,
  so the chat used to become always-on-top whenever the map did (map-only mode).
- Double-clicking the chat's message list toggles its **compact mode**: `WindowStyle.None`, its
  own `Topmost`, `CanResizeWithGrip`, and a drag on the message list moves the window. Messages,
  input box and Send button stay. It uses `PreviewMouseLeftButtonDown`, because the
  ScrollViewer handles the bubbling event, and ignores clicks on the scrollbar.
- **Round 2 (same day, after live user testing):**
  - *Bad room password now gives an error.* The user reported that a wrong password still showed
    "připojeno". Added `UOAMRQ1` access query / `UOAMRA1` OK|DENIED reply.
    `RelayMultiplayerClient.StartAsync` now returns `RelayConnectResult` and resends the query
    every 1s for up to 3s. It is only "connected" after an OK. The app shows **105** "Nepodařilo
    se připojit k mapě. Kontaktujte admina mapy." for DENIED; per the user's request, the message
    never says the password is wrong. It shows **106** when the server doesn't answer. The
    earlier "no oracle for valid passwords" design is knowingly given up here (user's call).
    Note: a new app against an old server build always gets 106, so deploy the server first.
  - *"Players only appear after they move" root cause:* the app forwarded its position to the
    relay only from `OnPositionChanged`. B2 emits only on change and the Orion script only every
    ~2s. A player standing still at connect time therefore had no position, and without a
    display name no name, so they never registered: no join notices, invisible to others.
    Fixed with `MainWindow._lastLocalUpdate`, which seeds a new relay client via
    `ReportLocalPosition` before `StartAsync`. When the name is still unknown after connecting,
    the status bar says it's waiting for the character name.
  - Color picker is now a swatch button + `Popup` grid (`PlayerColors.Palette`): 12 hues × 5
    levels plus a grey ramp and default cyan, 72 in total, plus a custom `#RRGGBB` field. The
    rows are generated by target **relative luminance**, not HSL lightness, so every entry is
    readable on the dark UI. Custom colors below `MinReadableLuminance` (0.18) are rejected.
    Changing color mid-connection applies live (`RelayMultiplayerClient.Color` setter; the
    server takes the color from every report/hello).
  - *Disconnect paths.* `MainWindow.DisconnectRelayAsync(statusMessage)` is the single teardown
    used by the Odpojit button and by a kick (`RelayMultiplayerClient.Kicked` event, raised when
    the admin deletes the room; handled via `BeginInvoke`, since the teardown awaits the receive
    loop that raises it). It always writes a status line. Before this, Odpojit left
    "připojeno k ..." in the status bar (user report), and a kick left the app internally
    "connected". Closing the window calls `SendByeNow()` (synchronous bye), so others see the
    leave immediately instead of after the 15s timeout. Verified in the real app via UI
    Automation against the live server: the status bar and buttons are correct after Odpojit and
    after a kick, and the server log shows `left` right after Odpojit.
  - Remote player marker is a `Rectangle` instead of an `Ellipse`. Since 2026-09-29 (user's
    request) it's exactly the self marker's size: `PlayerMarkerSizePx` (5) physical pixels, no
    outline, placed on the physical pixel grid in `UpdateRemotePlayerOverlay` (it used to be
    9×9 logical + outline, ~11 px at 125%).
  - Verified: loopback test + live test on the deployed server (throwaway room). Wrong password
    was DENIED in 32ms. A standing player got the join notice in 53ms. The color change
    propagated live. The app was launched and the popup showed 72 swatches via UI Automation.
- **Deployed and verified live** the same day (throwaway room on `89.168.122.175:27980`, created
  and deleted by the test): newcomer with no position saw the other player in ~32ms, clean
  disconnect announced in ~31ms, colors + UTF-8 names/chat intact. The new server is backward
  compatible with older app builds (they just keep the old delays and show up cyan).
- **Startup-crash lesson from this round**: the first build crashed on launch (the exe "just
  didn't open" - no window, no dialog) with a `TypeInitializationException` in `PlayerColors`:
  `Palette` was declared above `BrushCache`, static fields initialize in declaration order, so
  building the palette locked a still-null cache. Build + protocol tests were green because they
  never touch WPF-side statics. Two takeaways: (1) after any `NewUOAM.App` change, actually
  launch the exe for a few seconds (and ideally check one new control via UI Automation) - a green
  build isn't enough; (2) when the app "won't start", the full .NET stack trace is in the Windows
  Application event log (`Get-WinEvent`, providers `.NET Runtime` / `Application Error`) - check
  that first instead of guessing.

