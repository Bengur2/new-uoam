# Multiplayer relay, rooms, deployed instance

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

## Multiplayer relay (see all others' positions on the same map)

A layer on top of, not a replacement for, `IPositionProvider` - independent of which variant
(A/B1/B2) supplies THIS app instance's own position, it just forwards that position outward and
surfaces everyone else's. Protocol and client live in `NewUOAM.Positioning/Relay/`; the server is
its own standalone console project, `NewUOAM.RelayServer` (depends only on `NewUOAM.Positioning`).

- `Relay/RelayProtocol.cs` — the wire format, shared by both client and server so there's a single
  source of truth (not duplicated/hand-kept-in-sync between two projects). Reuses the same
  `|`-delimited ASCII line style as `OrionUdpPositionProvider` rather than inventing a new one:
  `UOAMRC1|name|x|y|z|map` (client → server report), `UOAMRS1|name|x|y|z|map` (server → client
  broadcast of one player's position), `UOAMRL1|name` (server → client: that player left/timed
  out). No auth/encryption — anyone who knows the server's address:port can report a fake position
  or read the roster; scoped for a small friend-group relay, not a public/adversarial deployment.
- `Relay/RelayMultiplayerClient.cs` — client side. Deliberately **not** an `IPositionProvider`: it
  doesn't produce this app's own position (that still comes from whichever local provider is
  active), it relays it onward and surfaces remote players via `RemotePlayerUpdated`/
  `RemotePlayerLeft` events. `ReportLocalPosition(update)` just records the latest value; a
  background loop actually sends it at a fixed `RelayProtocol.ClientReportIntervalMs` (50ms,
  originally 500ms then 150ms - lowered twice after live testing kept feeling laggy, see the
  changelog entry below) cadence regardless of how often the local provider pushes (as fast as
  every ~20ms from the Orion feed) —
  this both throttles the outbound rate (remote players' map icons don't need every single local
  update) and doubles as the session keepalive the server needs even while standing still.
- `NewUOAM.RelayServer/RelayServer.cs` — the server. In-memory only, no persistence, sessions
  keyed by player name (two simultaneous clients reporting the same name collide - a known,
  accepted limitation for this deployment scale). Forwards each report to every other known
  session immediately (not batched), plus a periodic full-roster re-broadcast
  (`RelayProtocol.FullRosterBroadcastIntervalSeconds`, 3s) so a late joiner or a client that missed
  a packet (UDP has no delivery guarantee) catches up without a dedicated handshake, and a sweep
  that drops/announces-leave for sessions silent longer than `RelayProtocol.SessionTimeoutSeconds`
  (15s). `Program.cs` binds `0.0.0.0` by default (`--bind`/`--port` override) specifically so the
  exact same build runs unmodified whether hosted locally, on a LAN box, or a public VPS - the
  address a player types into the app is the only thing that differs between those deployments.
- **Why UDP, not TCP**: client always initiates, server is always the reachable/public side, so a
  connectionless client-to-server report works through an ordinary home NAT/firewall the same way
  a game client's own traffic does - no port-forwarding needed on the player's end, only on the
  server's (trivial on a VPS, no NAT to fight). TCP would additionally need reconnect-handling
  logic on the client for network hiccups; UDP's periodic report already tolerates that for free
  (a dropped/late packet just means a slightly stale marker until the next one, not a broken
  session).
- `MainWindow.xaml.cs` wiring: `StartRelayButton_Click`/`StopRelayButton_Click` own a
  `RelayMultiplayerClient` (parsed from the `RelayServerTextBox`, `host:port`, saved to
  `AppSettings.RelayServerAddress` like the client path). Not auto-connected at startup unless
  the user opts in (see "Auto-connect" below). `RelayServerTextBox` pre-fills with `DefaultRelayServerAddress`
  (this project's own deployed relay, `89.168.122.175:27980`) whenever `AppSettings.RelayServerAddress`
  is null - i.e. only on a fresh/reset settings file, since the user asked not to have to
  remember/retype the address. Once a connection is made once, the saved value takes over as usual
  and this default is never consulted again. `OnPositionChanged` calls `_relayClient?.ReportLocalPosition(update)`
  unconditionally (before the `_clientData is null` early-return, so it doesn't depend on a map
  being loaded). Remote players are rendered as plain WPF `Ellipse`+`TextBlock` pairs in a
  `Canvas` (`RemotePlayersCanvas`, overlaid in the same `Grid` as `MapImage`) — not drawn into the
  bitmap — for the same reason the coordinate/compass overlays aren't: crisp text at any zoom, and
  it sidesteps the render-cache entirely (remote markers change independently of when the cache
  regenerates). `_remotePlayerViews` keeps one view-pair alive per known remote player name across
  redraws (added on first sighting, removed on leave/disconnect) rather than recreating them every
  frame; `UpdateRemotePlayerOverlay` (called at the end of every `Redraw()`, and independently by
  the remote-animation tick below) just repositions them via `WorldToScreen` and hides ones on a
  different facet than the one currently displayed. One DPI subtlety: `WorldToScreen`'s output is
  in the same PHYSICAL pixel space the render-cache bitmap uses (see the DPI section above), but
  `Canvas.Left`/`Top` are WPF's LOGICAL units — the overlay divides by `dpi.DpiScaleX`/`Y` to
  convert.
- **Remote-marker glide animation** (`BeginRemoteAnimation`/`OnRemoteAnimationTick`/
  `InterpolateRemote`, `RemoteAnim` class): added after the user tried live multiplayer and
  reported it felt "slow, same as old UOAM" despite the underlying data being fresh. Root cause
  was two-fold, both fixed together: (1) `RelayProtocol.ClientReportIntervalMs` was 500ms
  (deliberately conservative, see its own doc comment) — lowered in two rounds, first to 150ms,
  then (user explicitly asked "is it safe to go lower, ideally 50ms?") to 50ms; trivial extra
  bandwidth at this deployment's scale, and still above the local Orion feed's own ~20ms polling
  floor so nothing's wasted. `RemoteAnimationDurationMs` (the glide duration below) was lowered in
  step, 150ms → 60ms, specifically because the glide itself adds latency - leaving it at 150ms
  while the report interval dropped to 50ms would have silently eaten most of the gain (the marker
  would still take 150ms to visually catch up to a report that arrived 50ms after the last one).
  (2) remote markers had NO interpolation at all — each report
  (`OnRemotePlayerUpdated`) just overwrote `_remotePlayers[name]` and let the next `Redraw()` snap
  straight to it, so even fresh data still visibly teleported instead of moving. Fixed by copying
  the exact same ease-out-quad technique `BeginPanAnimation`/`OnAnimationTick` already use for the
  LOCAL view, applied per remote player instead: one shared `CompositionTarget.Rendering`
  subscription (`_remoteAnimRunning`) services every remote player's glide at once, retargeting
  smoothly from wherever a marker currently is (not restarting from its last *reported* position)
  if a new update arrives mid-glide — same "retarget in flight" behavior `BeginPanAnimation` has.
  Deliberately does NOT reuse the local view's animation fields/loop — remote markers move
  independently of the camera and of each other, so each needs its own `FromX/Y`/`ToX/Y`/
  `StartTicks` state (`RemoteAnim`), keyed by player name in `_remoteAnims`. This animation loop
  is independent of and much cheaper than the main `Redraw()` pipeline (no bitmap/cache work, just
  repositioning existing WPF elements), so it runs on its own schedule rather than piggybacking on
  `RequestRedraw()`.
- **Off-screen edge-arrow indicator**: each remote player's view triple grew from (Dot, Label) to
  (Dot, Arrow, ArrowRotation, Label) - `Arrow` is a `Polygon` triangle (default orientation points
  +X), `ArrowRotation` a reusable `RotateTransform` on it (`RenderTransformOrigin` 0.5,0.5, so
  setting `.Angle` alone rotates it correctly without recomputing a transform object every frame).
  In `UpdateRemotePlayerOverlay`, once a marker's logical (x,y) is computed as before, its offset
  from the viewport center (dx,dy) is checked against `RemoteEdgeMargin`-inset half-extents
  (`halfW`/`halfH`); inside → normal dot (Arrow collapsed); outside → dot collapsed, Arrow shown
  instead, clamped to the inset rectangle boundary via the standard "which axis needs the smaller
  scale factor to reach its half-extent" ray-to-box-edge formula (`t = min(halfW/|dx|,
  halfH/|dy|)`, clamped point `= center + (dx,dy)*t`), and `ArrowRotation.Angle =
  atan2(dy,dx)*180/π` - both WPF's rotation convention and screen-space Y-down atan2 are clockwise-
  positive, so this needs no sign flip to point the arrow at the player's real (possibly far
  off-screen) position. Note this operates on the PROJECTED screen delta (dx,dy), not the raw
  world-space delta - in `Rotated45` mode the arrow's on-screen direction reflects the isometric
  projection of the world offset, not literal compass direction, which is what actually looks
  correct next to a rotated map (confirmed via a real screenshot during testing, not just reasoned
  about: a player ~2000 tiles away in +X pointed correctly toward the bottom-right of the rotated
  view, matching `WorldToScreen`'s own `(dx-dy)*h, (dx+dy)*h` transform).
  Since 2026-09-29 (user's report: names ran off the map) `PlaceRemoteLabel` places the name on
  the arrow's inner side (left of an arrow on the right half), switches sides for a dot near the
  edge when it wouldn't fit, and clamps it inside the map vertically.
- **Custom per-player display name**: `RelayMultiplayerClient`'s constructor takes an optional
  `displayName` - when non-blank, `SendLoopAsync` reports that instead of
  `update.CharacterName ?? "Player"`. Wired to a new `DisplayNameTextBox` next to the relay address
  field (persisted in `AppSettings.MultiplayerDisplayName`, same pattern as the other remembered
  fields) - lets someone show up on others' maps as e.g. "GG" without touching their actual UO
  character name. Purely cosmetic/client-side - `RelayProtocol`'s wire format and the server are
  unaware this exists, the override just changes what string ends up in the `name` field of the
  report the client already sends.
- **A real debugging story worth remembering** (found via `tools/UopProbe`'s end-to-end relay
  test, not by code review): the very first version of that test used `new UdpClient(0)` for the
  synthetic client sockets, which — unlike the parameterless `new UdpClient()` `RelayMultiplayerClient`
  actually uses — defaults to a **dual-stack IPv6 socket** whenever the OS supports IPv6 (which is
  effectively always). Mixed against the relay server's plain-IPv4 bind, every datagram silently
  vanished with no error on either side (the server's own `SendAsync` reported success). A second,
  independent bug in the same test made it look worse than it was: a `Task.WhenAny(receiveTask,
  Task.Delay(...))` pattern that times out leaves the abandoned `ReceiveAsync()` still running in
  the background — a later, real `ReceiveAsync()` call on the same socket can then race that
  orphaned one for the next datagram and lose, silently, since nothing observes the earlier task's
  result either way. Fixed by using `new UdpClient()` (matching production) and `ReceiveAsync(CancellationToken)`
  with a real per-attempt cancellation instead of an abandoned `Task.WhenAny`. Neither bug was in
  `RelayServer`/`RelayMultiplayerClient` itself - both were in the test harness - but both would
  have looked exactly like a real relay bug without the isolation testing that found them.

### Multiplayer "rooms" (password-gated isolation between friend groups)

Lets several separate friend groups share one relay deployment without seeing each other.
Requested verbatim: an admin can create named+password "rooms"; a player only ever needs the
room's *password* to join (typed in `RoomPasswordTextBox`, in Online > Připojit k mapě); people in the same room see each other, people in different rooms don't;
the admin can list existing rooms *without* player lists/positions/passwords.

- **A room's password IS its key**, not just a field - `RelayServer` keys `_roomsByPassword` by
  the password itself, since that's exactly what an incoming `RelayProtocol.ReportTag` packet
  carries (`RelayProtocol.EncodeReport(roomPassword, playerName, update)` - the report format grew
  a leading room-password field). A room's `Name` is purely a human label for the admin's own
  `LIST` output - never sent to or required by a connecting player. A report whose password
  doesn't match any known room is **silently dropped** - no session created, no error reply -
  deliberately no oracle for "is this password valid" (`RelayServer.ReceiveLoopAsync`).
- **Admin protocol is a separate request/response pair** (`RelayProtocol.AdminCommandTag`/
  `AdminResponseTag`, `UOAMAC1`/`UOAMAR1`) from the fire-and-forget report/broadcast/leave
  messages, because it needs an actual result (created OK? duplicate password? wrong admin
  password?). Gated by a **separate admin password**, supplied to the server only via
  `--admin-password` at startup (`Program.cs`) - if omitted, every admin command is rejected with
  `ADMIN_DISABLED` rather than silently doing nothing. `RelayAdminClient` (client side) is
  deliberately NOT a persistent connection like `RelayMultiplayerClient` - each call opens a
  short-lived `UdpClient`, sends one command, waits up to `RelayProtocol.AdminResponseTimeoutMs`
  (3s) for the one reply, then closes; fine for a manual, occasional admin action, not meant to be
  polled.
- **`AdminWindow`** (`NewUOAM.App`) is the admin UI - a separate WPF window (button: "Admin…" next
  to the relay Připojit/Odpojit buttons), not a separate tool, since the same person plays and
  administers. Create-room form + a refreshable room-name list + a "Smazat vybranou místnost"
  button (enabled only once a room is selected in the list); every error `RelayServer` can return
  (`BAD_ADMIN_PASSWORD`, `ADMIN_DISABLED`, `DUPLICATE_PASSWORD`, `INVALID_ARGS`, `NOT_FOUND`,
  `TIMEOUT`) gets a human Czech message via `AdminWindow.DescribeError`, not the raw wire code.
- **DELETE, and what happens to currently-connected players** (added on explicit user request:
  "musí být nějak pořešeno jak to smazat, když jsou k ní lidé připojení... navrhuji, že když jsem
  admin, tak je prostě odpojit a místnost smazat" - just kick them and delete). Identified by
  **room name**, not password - matches what the admin can actually see in the room list
  (passwords are deliberately never shown there, see above), at the cost of deleting *every* room
  sharing that exact name if the admin ever created duplicates (names were never enforced unique -
  simpler than making DELETE ambiguous or silently picking one). `RelayServer.HandleAdminDeleteAsync`
  removes the room from `_roomsByPassword` first (so no new joins/reports can land in it, even
  mid-kick), then sends every currently-connected session in it a new `RelayProtocol.KickedTag`
  (`UOAMRK1`) notice - `RelayMultiplayerClient` surfaces this as a `StatusMessage` event
  ("byl(a) jsi odpojen(a) administrátorem...") rather than a dedicated event, reusing the same
  status pipe connect/error messages already go through. `AdminWindow.DeleteRoomButton_Click`
  confirms via a plain `MessageBox` (Yes/No, warns that connected players will be disconnected)
  before sending the command - the one place in this feature with a destructive-action guard,
  since delete is the one operation here that can't be undone by just creating the room again
  (the old password is gone, and anyone connected has to be told the new one out of band).
- **Rooms persist to `rooms.json`** (next to the server binary, `AppContext.BaseDirectory` by
  default, overridable via `--rooms-file`) so a room survives a service restart - sessions
  themselves stay purely in-memory, same as before rooms existed. Format is just `[{Name,
  Password}]`, nothing else (no session/player data ever touches disk).
- **Two real bugs found by `tools/UopProbe`'s in-process end-to-end test** (spin up a real
  `RelayServer` on loopback, drive it with real `RelayAdminClient`/`RelayMultiplayerClient`
  instances - not mocks): (1) `RelayServer.RunAsync` never disposed its `UdpClient` on shutdown,
  so a second `RelayServer` bound to the same port immediately after (a restart, or this exact
  test) threw `SocketException 10048` "address already in use" - fixed with a `finally { _udp?.Dispose(); }`
  around the run loop. (2) `SendAsync`'s catch was `catch (SocketException ex)` only - broadened to
  `catch (Exception ex)` so a future non-socket failure in a reply send can't take down the whole
  receive loop for every room, not just the one packet that triggered it.
- **A deployment-tooling bug, not a product bug, that wasted real time**: `dotnet publish ... -o
  publish\relay` (backslash, the path style used earlier in this file/README) run through the
  Bash tool - not PowerShell - gets backslash-escaped: `\r` in `sh` is just a literal `r`, so
  `publish\relay` silently became the argument `publishrelay`, a *different* folder from the one
  `scp` was reading from (`publish/relay`, forward slash, untouched). Every "redeploy" during this
  round actually re-uploaded a stale pre-rooms binary from that old folder; the giveaway was the
  server's own startup log line (`Admin rozhraní zapnuto, ...`) never appearing even though the
  process ran fine and `strace` showed it receiving admin packets - because it was still running
  code from before the admin protocol existed, silently failing `TryParseReport` on every
  `UOAMAC1` packet and dropping it. **Always use forward slashes for `dotnet publish -o` paths
  when the command runs through the Bash tool** (`publish/relay`, not `publish\relay`) - confirmed
  fixed by re-publishing with a forward slash and diffing the two folders' file timestamps.

### Player-created rooms + admin v2 (2026-10-01)

User's request: players create their own rooms, only the admin sees and deletes rooms, and the
admin can switch player room creation off (default on) when spammed. The user chose to do creation
**in the app, not on the web**: GitHub Pages is static and a browser can't speak UDP, so a web
portal would need an HTTPS API on the VM.
- **Creation** (`RelayProtocol.RoomCreateTag` `UOAMNC1|requestId|base64(name)` →
  `UOAMNR1|requestId|OK|password` / `ERR|reason`): Online > Připojit k mapě > "Založit novou
  místnost…" (`CreateRoomWindow`). The result shows the password with Kopírovat, and "Použít heslo"
  fills `RoomPasswordTextBox`.
  - **The server generates the password** (10 chars, alphabet without l/o/0/1, crypto RNG), user's
    pick. A player-chosen password would turn "already taken" into an oracle for other rooms'
    passwords.
  - Limits (`HandleRoomCreateAsync`): the admin switch (`DISABLED`), `CleanRoomName` (trimmed,
    control chars dropped, ≤40, visible; `INVALID_NAME`), `MaxPlayerRooms` 500 player rooms
    (`FULL`), `MaxPlayerRoomsPerIpPerHour` 3 (`RATE_LIMIT`). The per-IP window is **memory only**;
    no IP is ever written to disk (the user's privacy page says so).
- **Room data**: `Room` gained `Id` (8 hex, admin addressing; names aren't unique),
  `CreatedByPlayer`, `CreatedUtc`, `LastUsedUtc`. `rooms.json` records carry them as optional
  fields. An old `[{Name, Password}]` file loads as admin rooms with fresh ids and is re-saved
  right away so the ids stay stable. Saves go through a temp file + rename. `LastUsedUtc` is set
  on every join (`AnnounceJoinAsync`) and refreshed every 10 min while a room has players. Those
  writes are throttled to once a minute (`SweepRoomsAsync`, plus a final save on shutdown).
- **Expiry**: player rooms without players and unused for `PlayerRoomExpiryDays` (90, the
  user's "3 months") are deleted by the sweep. Admin rooms never expire.
- **Admin protocol v2** (old CREATE/LIST/DELETE kept): `ROOMS|offset` (pages of
  `AdminRoomsPageSize` 40, newest first; total, count, then id/name/source/created/lastUsed per
  room; never passwords or players), `DELETEID|id,id,…` (kicks connected players like DELETE),
  `SETTINGS` / `SETCREATE|0/1` → creation on/off, player room count, cap. The switch persists in
  `relay-settings.json` next to `rooms.json`.
- **`AdminWindow`** rework: a list with Název / Založil (hráč/admin) / Založeno / Naposledy
  použita, multi-select delete with one confirmation, "Vybrat hráčské za posledních 24 h" (spam
  cleanup), the creation checkbox (Click handler, so it only reacts to the user), and admin room
  creation with a chosen password in an Expander. Against an older server it falls back to
  names only.
- `RelayAdminClient.SendAndAwaitAsync` now also treats a `SocketException` (e.g. ICMP port
  unreachable) as a timeout instead of throwing into an `async void` handler.
- **Verified:**
  - Scratch loopback test against a real `RelayServer` (22 checks): old-format load + ids, expiry
    of a 120-day-old player room but not an admin one, bad admin password, default on, create
    with trimmed name and alphabet, 4th create per hour refused, invisible name refused, player
    joins with the generated password, delete by id kicks them, switch off persists across a
    restart and refuses creation, ids stable after the restart, paging of 104 rooms, old LIST
    still works, no IP in `rooms.json`.
  - Real app + local relay via UI Automation: create → password → "Použít heslo" fills the field,
    admin list (screenshot), switch off (server log), select-24h + delete with confirmation.
  - UIA notes: `TogglePattern` doesn't raise a WPF CheckBox's `Click`, and MessageBox buttons
    are `Pane`s without Invoke. The test posted WM_KEYDOWN space to the window and BM_CLICK to the
    button instead of `SendKeys`, which would type into whatever window is in the foreground.


### Deployed relay instance (live, 2026-09-23)

A real relay server is running for actual multiplayer testing - this is operational info about
that specific deployment, not something derivable from the code:

- **Provider**: Oracle Cloud "Always Free" tier, region Germany Central (Frankfurt), availability
  domain AD 3 (`VM.Standard.E2.1.Micro` shape requires AD 3 specifically in this region).
- **Instance**: `uoam-relay`, Ubuntu. **Public IP: `89.168.122.175`**, port **UDP 27980**.
- **Firewall**: opened in two places (OCI is layered - both need the rule) - the instance's
  attached Network Security Group `ig-quick-action-NSG` (ingress, UDP, 0.0.0.0/0, dest port
  27980) AND the OS-level `iptables` on the VM itself (Oracle's stock Ubuntu image blocks
  everything but SSH by default at the OS level even when the cloud-side NSG/Security List is
  open - easy to forget the second layer and conclude the port "isn't opening").
- **Runs as a systemd service** `uoam-relay` (`/etc/systemd/system/uoam-relay.service`),
  `ExecStart=/home/ubuntu/relay/NewUOAM.RelayServer --port 27980 --bind 0.0.0.0 --admin-password <secret>`,
  `Restart=always` - survives VM reboots and SSH disconnects. Logs: `sudo journalctl -u uoam-relay -f`.
  The admin password (see "Multiplayer rooms" above) is deliberately not written out here, same
  reasoning as the SSH key below - ask the user, or read it directly off the VM's unit file
  (`cat /etc/systemd/system/uoam-relay.service`) if you already have SSH access.
- Room definitions persist at `/home/ubuntu/relay/rooms.json` (name + password only, no session
  data) - see "Multiplayer rooms" above for the format.
- **To deploy an update**: rebuild locally with
  `dotnet publish src/NewUOAM.RelayServer -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/relay`
  (**forward slash** - see the "deployment-tooling bug" note above; a backslash silently builds
  into the wrong folder when this runs through the Bash tool). The binary can't be `scp`'d directly
  over the running one (fails with "Failure"/ETXTBSY-like behavior since the service has it open) -
  upload to a temp name and swap it in instead:
  `scp publish/relay/NewUOAM.RelayServer ubuntu@89.168.122.175:/home/ubuntu/relay/NewUOAM.RelayServer.new`,
  then on the VM `chmod +x` it, `mv` it over the old one (an atomic rename, safe even while the old
  binary is still running), and `sudo systemctl restart uoam-relay`.
- SSH access is via a private key file the user holds locally (not recorded here - it's a
  personal machine file, not project state) with `ssh -i <key> ubuntu@89.168.122.175`.
- In the app's "Multiplayer" field, the address to use is `89.168.122.175:27980`.

