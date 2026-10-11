# In-game integration: UOAssist chat, in-game commands, track reports

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

### Map chat inside the UO client (UOAssist API, how old UOAM did it) - 2026-09-23

The user asked for map-chat messages to appear inside the game like old UOAM did (bottom-left,
sysmsg-style, visible only to map users). The requirement was that it work with **Varianta B**,
not through the Orion script (Variant A). Researched before building:
- **Old UOAM never injected anything itself.** Its docs (uoam.net `uoamh8.html`/FAQ) say in-game
  display requires **UOAssist**: UOAM sent a Windows message to the assistant, and the assistant
  showed the text. Razor later re-implemented the same "UOAssist API" (`help/docs/uoaapi.md`;
  `UOAssist.cs` handles `DISPLAY_TEXT` by sending the client a *local* Unicode system-message
  packet, speaker "System"). Nothing goes to the server. (A pasted third-party chat claimed UOAM
  used its own DLL injection/`UOAMHelper.dll` - that is wrong, don't repeat it.)
- **The user's OrionUO64 exposes this API**: **Orion Assistant** (OA v3.0.38, closed source,
  loaded in-process) creates a window of class `UOASSIST-TP-MSG-WND` plus a class
  "Ultima Online" window titled "UOAM UO Fake Window", for old-UOAM compatibility. It isn't in
  the public Hotride/OrionUO source. It appears only once the client is logged in and OA is
  loaded (the second client, still at login, didn't have it).
- Contract: `GlobalAddAtom(text)` → `SendMessage(hwnd, WM_USER+207, hue | 0x10000 /*system msg*/,
  atom)`. The receiver returns 1 and frees the atom. Max 255 characters. Verified live: text
  appears bottom-left, nothing overhead. **OA's system-message path renders with an ASCII font
  that blanks every byte > 127**. Proven with test messages: the plain text lost its diacritics,
  UTF-8 smuggled through ANSI gave two blanks per char, and Latin-1-only "í á é" came out
  entirely blank. The system ANSI code page is 1250, so encoding isn't the issue; the client's
  Unicode *speech* font does render Czech (user's screenshot). No encoding trick can fix the
  sysmsg path. The user chose to transliterate for the game only; the app's chat window keeps
  the original text. `ToGameSafeText` does NFD + strips combining marks, maps typographic
  punctuation („“ ‚‘ – — …) and non-decomposing letters (ł ø ß æ œ đ) to ASCII, and turns
  anything else (emoji) into one '?'.
- Code: `NewUOAM.Positioning/ClientIntegration/UoAssistTextSender.cs` (window lookup per PID,
  send, chunking, sanitizing; `SendMessageTimeout` with 1s + `SMTO_ABORTIFHUNG`; deletes the atom
  itself if the receiver didn't take it). `MainWindow.ShowInGame` is called for every chat line
  (including your own, same as the chat window) and for join/leave notices. Sends are chained
  off the UI thread to keep their order. Target: the process tracked by B2
  (`ProcessMemoryPositionProvider.ProcessId`, so multiboxing picks the right client). Without
  B2 running it falls back to the only process exposing the API, if there's exactly one.
  "Can't send" is logged once until a send succeeds again. Toggle: `ShowChatInGameCheckBox`
  (`AppSettings.ShowChatInGame`, default on).
- Still read-only toward the client: no memory writes, no injection. It's a documented window
  message to an API the assistant exposes for exactly this. Hue is fixed: `DefaultHue`,
  since 2026-09-29 turquoise **0x05BA** (user's request; was green 0x44). It was picked as the
  entry of the DP client's `hues.mul` closest to #40E0D0 (groups of 4-byte header + 8 × 88-byte
  entries, hue = group*8 + entry + 1). It's outside the stock hue range, so another shard's
  client may show it differently; 0x005A is the nearest stock hue. Not yet checked in game.
  **Per-client hues (2026-10-11):** it did show differently - black on Endor Revived (Razor passes
  the hue through unchanged; Endor's `hues.mul` entry 0x05BA is junk, 29 of 32 ramp colors
  (0,0,8)). `MapData/Colors/HueTable` reads the loaded client folder's `hues.mul`;
  `ResolveInGameHues` (on map load) keeps each in-game hue (chat 0x05BA, shared marker 0x0059,
  panic 0x0021) when its ramp is a real dark→light ramp that still looks like the intended DP
  color, else takes the closest usable hue (compares ramp color #24). Endor → chat 0x005A,
  others unchanged; DP → all unchanged. Verified in the Endor client: test lines in the old hue
  (black), 0x005A (turquoise) and 0x0021 (red). Caveat: uses the *map's* client folder, not the
  tracked client's, so a map loaded from another shard's files would pick that shard's hues. Player RGB colors aren't mapped to UO hues (would need `hues.mul`) - possible follow-up.
  So is the reverse direction (UOAM's `--text` typed in-game → map chat, which would need the
  UOAssist `ADD_CMD` registration).
- Verified end-to-end: the real app with B2 on PID 31244, connected to a throwaway room. A
  second test player joined, sent "Ahoj z mapy, příliš žluťoučký kůň!" and left. All three
  lines showed in the game (PrintWindow screenshot), diacritics-free and readable.

### In-game commands (old UOAM's "-panic", "--text"...) - 2026-09-25

Typed in the UO client, handled by the map. Implemented: `-c text` (room chat), `-c name>text`
(private, unique name prefix is enough), `-panic`, `-unpanic`, and since 2026-09-29 `-who`
(`HandleWhoCommand`: one line per room mate, "Name - 120 tiles NorthEast" / "jiná mapa" /
"bez pozice", from `DescribeRoomPlayers`, shared with the chat's 👥 list). `-find/-mark/-unmark/
-share` are not done. **`-who` only works in clients started after that build**: a running
client's bridge keeps the command list it registered (see below).
- **Mechanism: UOAssist `ADD_CMD` (`WM_USER+209`)**, wParam = receiving HWND, lParam = atom with
  the command name. It returns the message id (>= `WM_USER+400`). When the player types
  `-name args`, OA swallows the line and posts that id to the HWND, wParam = atom with args
  (receiver deletes it). Contract from Razor's `uoaapi.md`/`UOAssist.cs`.
- **Live-tested OA v3.0.38 behavior (a probe window, user typing):**
  - `-cmd args`: delivered, nothing is said in game.
  - **Unregistered `-anything` is said aloud** (typos leak into the game).
  - **`--text` and `-- text` are swallowed and delivered nowhere**, so UOAM's `--` chat can't
    be done; hence `-c`.
  - **No unregister**: `ADD_CMD` with wParam 0 just returned a new id.
  - **Duplicate name -> only the FIRST registration gets it**, even when its window is dead.
    A registration therefore lives as long as the client process.
- **Hence `NewUOAM.UoaBridge`** (one process per client pid, named mutex
  `Local\NewUOAM.UoaBridge.<pid>`). It registers the commands once, re-registers if OA's window
  handle changes (relogin; old ids kept), and exits when the client exits.
  - Forwards to the app by `WM_COPYDATA` (dwData `NUOA`, UTF-16 `pid\ncommand\nargs`) to the
    app's message-only window titled `NewUOAM.CommandSink` (`UoAssistCommands`, an `HwndSource`
    with `HWND_MESSAGE` parent). The sink allows `WM_COPYDATA` through UIPI.
  - No sink -> answers in game "new UOAM nebezi". Log: `%LocalAppData%\NewUOAM\uoabridge\bridge.log`.
  - The app starts it (`EnsureCommandBridge`, every 3s) for the in-game target client (B2's pid,
    or the only UOAssist client), only once OA's window exists.
  - The app runs it from a **copy** in `%LocalAppData%\NewUOAM\uoabridge\<sha256 of dll>\`.
    A running bridge holds its files for the client's lifetime, so running from `bin` would
    break rebuilds.
  - Changing the bridge or the command list only reaches clients started afterwards. Keep it
    tiny and stable.
  - **Never kill a bridge** while its client runs: its commands are dead until the client
    restarts.
  - **UIPI (bug found live, fixed 2026-09-25):** the app self-elevates, so the bridge it starts
    is elevated, while the client isn't. Windows silently drops posts from the lower-integrity
    OA to the elevated bridge window (`PostMessage` fails with error 5), so every command
    vanished. The earlier test ran the app with `--no-elevate` and missed it. The bridge now
    calls `ChangeWindowMessageFilterEx(MSGFLT_ALLOW)` for each registered id. Bridges started
    before the fix stay broken until their client restarts.
  - **Several maps on one PC:** every map has a sink with the same title. The bridge offers the
    command to each sink in turn. `UoAssistCommands.AcceptsClient` makes a map that tracks
    another client via B2 decline (returns 0). A map without B2 accepts anything.
  - The bridge logs each received command (never its text) to `bridge.log`.
- **Protocol additions:**
  - Chat send gets an optional trailing target name, and the chat broadcast a trailing `P`
    (private). The server delivers to that one session only and drops an unknown target.
    **An old server ignores the target and broadcasts to the whole room (deployed 2026-09-25, previous binary kept as `.prev`). Deploy the server
    before users send private messages.**
  - Panic is **one per room, and anyone can turn it off** (user's call; people forget).
    - `UOAMPC1|room|name|1` makes the sender the room's panic, replacing anyone else's.
      `...|0` clears the room's panic, whoever set it.
    - State is `UOAMPS1|SET|name|notify` or `UOAMPS1|NONE|notify|actor|previous`
      (`RelayProtocol.PanicState`). `Room.PanicBy` on the server.
    - A change goes to the whole room incl. the sender. A no-op set is answered to the sender
      only. Newcomers get the current panic, and the 3s sweep re-sends it ("none" too).
    - A panicking player who leaves (bye or timeout) clears it quietly (notify 0).
- **App behavior:**
  - `-c` goes through `RelayMultiplayerClient.SendChatMessage(text, toPlayer)`.
  - `_roomPlayers` (joined + positioned players) is used for prefix matching. A `>` whose left
    part matches nobody is an error, never a public send.
  - Answers go to the client the command came from (`ShowInGame(targetPid, force: true)`).
  - Panic of others: `PanicFlashBorder` blinks, a red dashed line runs from you to them
    (`PlacePanicLine` in `UpdateRemotePlayerOverlay`), their marker blinks, and
    `SystemSounds.Exclamation` plays every 3s (`PanicSoundCheckBox`, persisted).
  - In game, **over your head** (user's request): `PANIC! X potřebuje pomoc! -> NorthEast, 37 tiles`,
    repeated every `SharedMarkerRepeatSeconds` (10s) per panicking room mate with a fresh
    direction (`PanicText`, `_panicRepeatTimer`). The "no longer needs help" line shows once.
  - Own panic: red "PANIC!" on the map, no sound (the sound is for others' panic only, by
    design). In game, over your own head, once: "PANIC zapnut - ostatni te vidi na mape".
  - "PANIC! ..." and your own "PANIC zapnut" are **red** (hue `PanicHue` 0x0021, the user's
    pick from a live comparison). The turn-off lines keep the default color.
  - While the room has a panic, the overhead "Shared Marker" text is **paused**
    (`ShowSharedMarkerInGame`). Its repeat timer keeps running, so it's back within 10s.
  - **Panic peek** (`StartPanicPeek`):
    - On a live panic of a room mate (not a re-sync), the map flies to them in `FlightMs`
      (350ms, ease-in-out, any distance - the user wanted a fly-over, not a jump). It stays
      `PanicPeekSeconds` (2s), then flies back.
    - With Track Player on it returns to your character (its position at that moment). With it
      off it returns to the exact previous view. A second panic retargets but keeps the
      original return point.
    - `ViewFollowsPlayer` (= Track Player and no peek) replaces `_trackPlayer` wherever the
      view follows or the self marker is glued to the center, so your own steps don't pull the
      view back mid-peek.
    - A drag or a Track Player change cancels the return. Disconnect snaps back
      (`FinishPanicPeekNow`).
    - While `_flying`, `EnsureCache` builds a viewport-sized cache (factor 1) centered on the
      flight's current tile, since a 3x cache would be rebuilt nearly every frame anyway. It
      gets 1/9 of the work; the full-margin cache returns on the first redraw after the flight.
  - Turning off says who did it ("X vypnul(a) paniku hráče Y", "X vypnul(a) tvou paniku"...).
  - **Space over the focused map toggles panic** (`MapBorder_PreviewKeyDown`, key repeat
    ignored): it turns the room's panic off if there is one, otherwise turns yours on. The same
    goes for the "Panic!" map menu item. It works in both normal and map-only mode. Space never
    opens the chat (`PreviewTextInput` ignores " "); inside the chat window it's a normal space.
  - User-confirmed live 2026-09-25: `-c` and `-panic`/`-unpanic` typed in Conlan's client.
- **Verified 2026-09-25:**
  - `tools/UopProbe` loopback test (15 checks: private only to target, not to other
    rooms/players, unknown target dropped, panic notify/no-op/newcomer/sweep).
  - Real app with B2 on Conlan + a local relay + a fake room mate. OA was simulated by posting
    the registered ids to the bridge window. Checked by screenshots: public/private send, usage
    and error replies in game, panic both ways (frame, line, texts), and the bridge surviving an
    app restart ("nebezi" while down, the new app instance answering after).
  - **Not yet:** the user typing the commands in a client with a live bridge, and Czech
    diacritics in args coming from OA.
- Leftovers of the probing: Bodhi's client (pid 20544) has `c`, `panic`, `-`, `uoamtest`
  registered to dead probe windows, so commands won't work there until that client restarts.

### Track reports (-t) - 2026-09-30

User's request: report the players found by the Tracking skill to the room, with a second map.
**The user explicitly chose an Orion script for this part** (it types the command itself), an
exception to the "Variant B, no Orion script" rule for client features; the map side still only
uses the UOAssist command path.
- **`tools/OrionScripts/TrackPlayers.oajs`**, `TrackPlayers()`:
  - Uses the Tracking skill, then picks "Players" in the "Tracking" menu (Animals / Monsters /
    Humans / Players). It picks it directly with `menu.Select`, not with an `Orion.WaitMenu`
    hook, because a hook could also answer (and close) the second menu.
  - "You see no evidence of people" means nobody was found.
  - Otherwise a second "Tracking" menu lists names. The script then does
    `Orion.Say("-t " + names.join(", "))`. **The name menu is left open**, so the user can
    click a name and track that player.
  - Comma is the separator, because names may contain spaces.
  - At start it closes Tracking menus left open by an earlier run (`CloseTrackingMenus`:
    `Orion.CloseMenu("Tracking")`, then `menu.Close()` on any unanswered Tracking "lastmenu"),
    so an old menu can't be mistaken for the new answer.
  - **No `Replayed()` checks.** Live, only the first run picked "Players", and later runs left
    the category menu open. Suspected cause: Sphere reuses the menu's serial/id, so Orion reports
    the new menu as already answered, or `lastmenu` still returns the previous run's menu. A new
    menu is therefore detected by `Orion.MenuCount() > 0` after the close, and the two menus are
    told apart by content (a "Players" item).
  - If the category menu is still open 300ms after `menu.Select`, the script falls back to
    `Orion.SelectMenu("Tracking", "Players")`. On a timeout it dumps the menu state to the
    TextWindow (`DumpMenus`).
  - The fix is not yet confirmed live.
  - **Ignore list (user's request):** names in `TrackPlayers_ignore.txt` next to the script (one
    per line, hand-editable, case-insensitive) are never reported. Managed with
    `TrackIgnoreAdd()` (`Orion.InputText`; beware, on its 60s timeout the next typed line goes to
    the world), `TrackIgnoreAddTarget()` (click a player), `TrackIgnoreRemove()` and
    `TrackIgnoreList()`.
  - **Long lists:** `SayTrack` splits the names into `-t` lines of at most 90 chars, 300ms
    apart. The map merges parts from the same client arriving within `TrackMergeMs` (1s) of each
    other into one report, at the position of the first part (`HandleTrackCommand` /
    `FlushTrack`). Above `MaxTrackNames` (20) it sends the first 20 and says so in the game.
  - Script logic unit-tested with Node and a mocked `Orion` (8 checks): 25 names → 4 lines ≤90
    chars, order kept; ignore add/dedupe/remove and a hand-edited CRLF file.
  - Deployed to `C:\Games\DP\Ultima Online DP\Orion Launcher\OA\` on this PC (that's where the
    Scripts tab reads from).
  - **Unverified live:** whether Orion Assistant intercepts a *script's* `Orion.Say("-t ...")`
    as a registered command like typed text. If it doesn't, the line would be said aloud.
    `TrackPlayersTest()` says `-who` for a safe first check.
- **Map** (`MainWindow.Tracking.cs`):
  - `-t` is a bridge command (`UoAssistCommands.Commands`, so only clients started after this
    build have it). `HandleTrackCommand` sends `RelayMultiplayerClient.ReportTrack` with **your
    current B2 position** (tracking finds people around you). It errors out without a
    connection, position or name.
  - `OnTrackReported` runs for everyone in the room, the reporter included. It adds a chat
    system line (unread for others, with distance and direction from you), a line in the game,
    and `SystemSounds.Asterisk` for others' reports (`TrackSoundCheckBox`). Then the track map.
- **Track map window** (`TrackMapWindow`):
  - Centered on the report: a ring, the names below it, and a header "Reporter · HH:mm (před N
    min) · X,Y · N hráčů" whose age refreshes every 15s. The names box is limited to the space
    below the ring and scrolls (a `ScrollViewer`, mouse wheel over it) when there are more names,
    since the game leaves little room for a big window. 20 names fit in a 300×260 window
    (screenshot). It keeps the last report until the next one or
    until it's closed.
  - Its own zoom (wheel, even 2-24 px/tile). Rendering is MainWindow's `RenderRegion`, made
    parametric (facet/mode/zoom) for this, via `RenderTrackMap`, in the main map's projection.
  - Double-click toggles map-only mode (same chrome order as `ChatWindow.SetCompact`). The ✕
    button closes it in both modes (user's requirement).
  - Settings in Online > Připojit k mapě > TRACKING: `ShowTrackMap`, `TrackMapAlwaysOpen`
    (radio "Otevřít navrchu, když někdo trackne" = pops up Topmost on others' reports, your own
    doesn't pop it / "Nechat otevřenou pořád" = opened at startup), `TrackSound`,
    `TrackMapBounds` + `TrackMapOnlyMode` (remembered like the chat's).
- **Wire:** `UOAMTR1|room|name|x|y|map|base64(names joined by \n)` (client → server) and
  `UOAMTB1|reporter|x|y|map|base64(names)|RRGGBB` (server → the whole room, reporter included).
  - Both sides clean names with `RelayProtocol.TrackNames`: control chars replaced, ≤40 chars,
    invisible names and case-insensitive duplicates dropped, at most 20.
  - The server throttles to 1 report/s per player (`Session.LastTrack`) and keeps nothing.
  - An old server drops these packets silently. **Deployed 2026-09-30** (previous binary kept
    as `NewUOAM.RelayServer.prev`). Live test in a throwaway room: the room mate got the report
    in 32 ms, the reporter got its echo, and the room was deleted afterwards.
- **Verified:**
  - Loopback (scratch, 11 checks): sender and room mate receive it, other room doesn't, names
    with spaces intact, 1/s throttle, name cleaning, 20-name cap, bad coordinates / no names
    rejected, unknown room password delivers nothing.
  - Real app + local relay + a scripted "Scout": auto-connected, the track window popped up
    with "Scout · … · 1500,1600" and "Intercessor, Sir Lancelot" (PrintWindow screenshot), and
    the status line and unread counter (1) updated.
  - Not verified: the script in the real game, map-only mode of the track window, and the
    always-open mode.

