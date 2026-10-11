# Positioning: variants A / B1 / B2, MemoryScanner, automatic client tracking

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

### NewUOAM.Positioning — pluggable "where is the player" sources

`IPositionProvider` is the seam: `StartAsync`/`StopAsync` + a `PositionChanged` event carrying a
`PositionUpdate` (X, Y, Z, Map, CharacterName, Timestamp). Three variants live side by side in
`Providers/`, chosen because none of them can destabilize the game client:

- **A — `OrionUdpPositionProvider`** (implemented): listens on loopback UDP (default port
  27974) for pushes from `tools/OrionScripts/PositionFeed.oajs`, a script that runs *inside*
  OrionUO (its official JavaScript scripting API — `Player.X/Y/Z/Map`, `Orion.UdpSend`), not an
  injected/hooked component. Wire format is documented at the top of
  `OrionUdpPositionProvider.cs`: `UOAM1|X|Y|Z|MAP|NAME`. If you change the format, update the
  `.oajs` script to match — there's no version negotiation.
  **Deploying the script (this user's OrionUO GUI, v3.0.38.0 "Tzeentch"):** the file must be
  copied into `C:\Orion Launcher\OA\` (the folder the Scripts tab's "Scripts from folder" panel
  actually reads — *not* `tools/OrionScripts` in this repo, and not the other `OA\DP\`/`OA\TEMP\`
  folders this user also has, which are unrelated personal-script staging areas). The script's
  entire body must live inside one top-level named function (e.g. `function PositionFeed() {...}`)
  — OrionUO's Scripts editor only lists top-level function names in its "Handling" dropdown as
  runnable/selectable entry points; a bare top-level `while(true)` with no enclosing function
  shows up as an empty dropdown with nothing to Start. This wasn't documented anywhere in
  `OrionUO_Documentation.md` (which covers the scripting API and in-game text commands, not the
  Scripts-tab GUI) — it was found by screenshot, not by doc reading.
- **B1 — `PacketProxyPositionProvider`** (**paused, not deleted** - see below for why; UI is
  commented out in `MainWindow.xaml`/`.xaml.cs`, backend code is untouched and still builds): a
  local TCP relay in `PacketProxy/`. Two hops: the login
  hop (`LoginRedirectRewriter`) buffers server->client bytes until it has the complete 0x8C "Play
  Server Ack" packet, rewrites its embedded IP/port to point back at this proxy's own game-hop
  listener (OS-assigned port), then flushes everything buffered in one write - it does NOT rewrite
  bytes in place chunk-by-chunk, because 0x8C is only 11 bytes and splitting across two TCP reads
  is possible, and there is no way to un-send bytes already forwarded. The game hop relays every
  byte completely unmodified and runs `PacketStreamWalker` + `PositionExtractor` purely as a
  read-only side channel - **parsing never gates or alters what gets forwarded**, so a framing
  mistake can only cost position updates, never corrupt the player's actual connection.
  - `UoPacketLengths.cs`: fixed length (or "variable, has own length prefix") for all 256 packet
    IDs, cross-checked between two independent authoritative open-source implementations (ServUO's
    server-side packet writers and ClassicUO's client-side `PacketsTable.cs`) - they agreed on
    every ID this matters for.
  - `LoginRedirectRewriter.cs` **forwards each packet the moment it's complete** - it does NOT
    buffer everything until 0x8C shows up. An earlier version did exactly that (reasoning: 0x8C
    could split across TCP reads, so wait for the whole thing before sending anything) and it
    caused a real deadlock on a live login attempt (hung at "Verifying Account", found via the
    user's own test against Dark Paradise/Sphere): the server only sends 0x8C *after* the client
    answers an earlier packet (0xA8 Server List) - buffering held that 0xA8 back too, so the
    client never got to answer, so the server never sent 0x8C, so the proxy waited forever. The
    split-packet worry was real but already handled elsewhere: `PacketStreamWalker` never returns
    an incomplete packet in the first place, so forwarding every packet it does return, immediately,
    is both safe and required. See `tools/UopProbe`'s current content for the regression test that
    reproduces this exact ordering (server list before redirect).
  - `PositionExtractor.cs`: UO movement is **client-authoritative** - a normal accepted step
    (`0x02` Move Request -> `0x22` Move Ack) carries no coordinates at all, so it dead-reckons:
    remembers the direction per in-flight sequence number from `0x02`, steps `(X,Y)` by that
    direction's delta when the matching `0x22` arrives. `0x1B` (Enter World) seeds the absolute
    start position (and gives map width/height, matched against `FacetInfo.Known` to resolve a
    facet index - there's no explicit facet ID in this packet); `0x21` (Move Reject) and `0x20`
    (Mobile Update, only when its serial matches ours) resync it to an authoritative absolute
    position. Byte offsets came from ServUO's actual packet-writing source, not memory - and one
    real bug was caught this way: `0x1B`'s Z field is a 2-byte short, but `0x20`/`0x21`'s Z field
    is a 1-byte sbyte - easy to get wrong by pattern-matching between the three.
  - **Why it's paused - live-tested against Dark Paradise (Sphere/Source-X), found a real blocker,
    not a bug:** the login hop is confirmed plaintext (hex dump showed `0xEF` login seed, the
    literal account name (`<account>`), `0xA8` server list with `Dark Paradise` in ASCII - all
    exactly matching `UoPacketLengths`). But the **game hop** (after the 0x8C redirect) sends just
    4 raw bytes with no packet-ID wrapper - the legacy bare-seed convention, not the modern 0xEF
    one - followed by data with no recognizable structure. This is the classic client's optional
    "login encryption" layer (what Razor/UOSteam's "remove encryption" checkbox strips), active on
    the game connection specifically. Implementing that cipher is a legitimate, scoped follow-up
    (it's a well-documented, publicly-known simple algorithm - several OSS UO tools implement it)
    but is new work, not a fix to what's here. Everything in `PacketProxy/` was validated up to
    this point: synthetic packets built to the verified byte layout, a real-socket integration
    test (fake login server + fake client through the actual `TcpListener`/relay code), and the
    live test itself (login-hop redirect worked correctly, including a deadlock bug found and
    fixed along the way - see `LoginRedirectRewriter` above). See `tools/UopProbe`'s current
    content for whichever test suite was run last (it's a scratch/REPL file, gets overwritten).
- **B2 — `ProcessMemoryPositionProvider`** — **implemented and verified live**, 2026-09-23. External,
  read-only `ReadProcessMemory` (OpenProcess + ReadProcessMemory only - no writes, no injection,
  no hooking). Real correction found this round: the actual running game process for this user's
  setup is **`OrionUO64.exe`**, not `client.exe` - OrionUO here is a full replacement client, not a
  script host layered on the original. The pointer chain was found via a real live
  `NewUOAM.MemoryScanner` session (see that project's own entry below for the tool, and the
  session's methodology): `moduleBase(OrionUO64.exe) + 0x3796EF8` → (8-byte pointer) → player
  object → `+0x28` = X (int32), `+0x2C` = Y (int32), `+0x41` = Z (sbyte). The build these were
  found on: `C:\Orion Launcher\OrionUO64.exe` v1.0.37.0, 5 886 976 bytes, SHA-256
  `7EE94D35FAAEAA4B149EBCBF2F816C57A39479885AC41254D7EDD609D493E35B` (Orion Assistant v3.0.38.0).
  **Multi-build support (2026-09-24, second PC):** that PC runs a *different* build,
  `C:\Games\DP\Ultima Online DP\Orion Launcher\OrionUO64.exe` v1.0.35.1 (SHA-256 `66D65061…`).
  Its player-object layout is identical (X/Y/Z `+0x28/+0x2C/+0x41`), only the global moved, to
  `+0x3793EE8`. Beware: that PC also has an unused `C:\Orion Launcher` v1.0.38.0. Always hash
  the *running* process's `MainModule.FileName`, not a guessed install path. `OrionPlayerPointerLocator`
  now holds a SHA-256 → offset table of known builds. For an **unknown build** it locates `g_Player`
  itself from the live process. It counts RIP-relative `mov r64,[rip+disp32]` loads in the
  executable sections (PE headers read from memory), ranks the targets in writable sections,
  and picks the most-read global whose object has plausible X/Y **and contains the character
  name** from the window title in its first 0x400 bytes (the name sits inline at `+0x97`). The
  name check is required: a decoy global (#19 by reads, `+0x515A90`) points to another object
  with near-player coordinates. `g_Player` itself is #3 (534 loads). Verified live on v1.0.35.1:
  the heuristic alone found `+0x3793EE8` in ~15ms, and a wrong name finds nothing. B2 no longer
  fails silently: the status bar shows the build ("OrionUO 1.0.35.1", or "neznámá verze…" plus
  the auto-found offset, to be added to the table) and warns after 3s of a null pointer
  (character not logged in). If a future build changes the object layout too, the heuristic
  finds nothing and keeps saying so. Then a `NewUOAM.MemoryScanner` session is the fix. Map/facet has no confirmed offset - hardcoded to `0` (Felucca), a real, deliberate,
  documented limitation since this user's shard (Dark Paradise) only actually runs Felucca in
  practice; revisit (another scanner session, changing facet, seeing what changes) if ever used
  somewhere with more than one facet. Character name is NOT read from memory - it's parsed from
  the process's own window title (`"Bodhi (Dark Paradise)"` → `"Bodhi"`, see
  `ExtractCharacterName`) instead, since a raw in-memory string search for the name turned out far
  noisier (hundreds of unrelated hits - chat history, UI text caches) than the position struct
  search was, and the window title gives it for free. **Known rough edge, not a data-correctness
  bug**: `Process.MainWindowTitle` reflects whatever window the OS currently considers the
  process's "main" one, which can transiently be a secondary dialog OrionUO opened (observed live:
  showed "Text Dialog" instead of the character name while some other OrionUO window had focus) -
  the X/Y/Z data itself was unaffected and confirmed correct throughout, only the cosmetic name
  label can occasionally be wrong. `FindCandidateProcesses()` enumerates every running
  `OrionUO64.exe` (oldest first) with its window title - the multiboxing requirement ("1-3
  klienty, chci si vybrat, který trackovat"). Since 2026-09-28 there's no picker + Start any
  more: the map tracks a client on its own and the **Klient** menu switches between them (see
  "Automatic client tracking"). `IPositionProvider.PositionChanged` is wired to the same
  `OnPositionChanged` handler Variant A uses (still fully provider-agnostic). Verified end-to-end against the user's actual real, running game (PID captured live,
  not a synthetic test) - `PositionText` showed real, correct, live-updating X/Y/Z matching the
  values reported throughout the scanning session.

- **B3 — `UoAssistPositionProvider`** (2026-10-11, user's request: Endor Revived = ClassicUO
  1.46 fork `EndorRevived.exe` with Razor 1.46 as a plugin, any install folder). No memory
  reading at all: only window messages to the assistant's `UOASSIST-TP-MSG-WND` window, the way
  old UOAM worked with UOAssist/Razor. Contract read from Razor's source (`Razor/Client/UOAssist.cs`,
  `Core/Player.cs`, markdwags/Razor), then verified live:
  - `GET_COORDS` (WM_USER+202) polled every 50 ms → `X | Y<<16`, `0` = no character. No Z (reported 0).
  - `REGISTER` (WM_USER+200, wParam = our message-only window) **toggles**: the same hwnd again
    unregisters (returns 2). So one registration per assistant window, `_registered` tracks it,
    and Stop sends it once more to unregister. Each provider gets a fresh hwnd, so restarts are safe.
  - Facet: `MAP_INFO` (WM_USER+313, wParam = facet) is posted **only on a change**
    (`PlayerData.OnMapChange`). `LOGIN` (WM_USER+303) → facet 0 (new player object starts at 0;
    Razor posts MAP_INFO right after for another facet). **Limitation:** attaching to a character
    already logged in on a non-zero facet shows 0 until the next facet change or relog - Razor has
    no query for it. Within one app run the last facet per character name is remembered
    (static dictionary), so switching clients back and forth keeps it.
  - `ChangeWindowMessageFilterEx` allows LOGIN/LOGOUT/MAP_INFO, else UIPI drops them when the map
    is elevated and the client isn't (same trap as the bridge).
  - Name: Razor's own window title "Ilmater (Endor revived) - Razor v1.46.0.0", re-read every 2s.
  - Candidates: every process with a UOAssist window except `OrionUO64` (B2 reads those, with Z).
    The Klient menu lists Orion clients first, then UOAssist ones; `B2ProcessItem.ViaUoAssist`
    picks the provider. Both providers implement `IClientPositionProvider` (ProcessId), which
    the app uses for in-game text / command bridge / stale-update checks.
  - The provider runs its own thread with a message loop (window + 50 ms/2 s timers), so all its
    state is single-threaded. StartAsync does one test GET_COORDS first (fails fast on UIPI/hang).
  - Verified 2026-10-11: standalone test (two start/stop rounds, register → unregister → register
    again, no failure), then the real Debug app `--no-elevate` picked the Endor client on its own:
    "X: 2528 Y: 1426 Z: 0 Map: 0 (Ilmater)", Endor's `.mul` map (7168×4096) rendered with the
    marker. The UoaBridge also registers `-c/-panic/-unpanic/-who/-t` with Razor (WM_USER+401..405).
    Walking and a live facet change: see the result line below.
  - `OverrideFacet` (World menu, see `app-ui.md`): posted as WM_APP+1 to the provider's own window,
    so it runs on the provider thread; sets the facet like a MAP_INFO (and remembers it per
    character) and polls at once. B2's facet is now a volatile field (starts 0) with the same
    method; its poll loop re-emits when the facet changes.

### NewUOAM.MemoryScanner — the live scanning tool that found the above

An interactive Cheat-Engine-style console tool (`scan`/`next`/`list`/`watch`/`near`/`findstr[16]`/
`read`/`findptr`/`findptrrange`), entirely read-only (`Native.cs`: OpenProcess/VirtualQueryEx/
ReadProcessMemory only). `MemoryScanner.cs` holds the actual mechanics:
- `ScanInt32`/`NarrowInt32` - the classic exact-value scan/narrow technique (move in-game, the
  value that used to match your old coordinate and now matches the new one survives each round),
  restricted by default to committed, private, writable pages (`EnumerateScannableRegions`) - the
  same "just scan writable heap memory" default real memory-scanning tools use, since that's
  where live game state actually lives, and it's dramatically faster/less noisy than scanning
  everything.
- `FindPointersTo`/`FindPointersInRange` - the reverse-pointer search that turns a found (but
  restart-unstable) heap address into a genuinely stable one: searches a BROADER set of regions
  (`EnumerateRegionsIncludingModuleData`, private pages AND a loaded module's own writable
  .data/.bss) for an 8-byte value matching the target - a hit inside the module's own image is a
  static global pointer, stable across restarts (and even ASLR - ASLR only relocates the WHOLE
  module by a random amount, never offsets *within* it, so `moduleBase + fixedOffset` computed
  fresh each run via `Process.MainModule.BaseAddress` always resolves correctly).
- `selftest <path-to-target-exe>` - not for real use; exercises scan/narrow/read end-to-end
  against a throwaway target process holding one known value at a fixed unmanaged address (native
  heap, not a GC-managed object that a compacting GC could move mid-test) - validates the core
  mechanics work in this environment before ever touching the real client. Caught two real
  test-harness bugs during development, neither in the scanner itself: (1) PowerShell's default
  `Process.StandardInput` writer prepends a UTF-8 BOM on the very first write, corrupting the
  first piped command's text (`﻿selftest` doesn't match the `"selftest"` case) - worked around
  with a sacrificial blank first line, not fixable from the scanner's side since it's how the
  *caller* writes to the pipe; (2) driving two separately-launched interactive console processes
  from an outer script via raw line-counted stdio reads is fragile (no reliable way to know how
  many lines a given command will emit) - the `selftest` command itself sidesteps this entirely by
  driving both ends from ONE process instead of an external orchestrator juggling two pipes.
- **Non-interactive mode** (`--state <file> <command> [args]`) runs ONE command against a
  candidate list persisted to a small JSON file between invocations, then exits - added
  specifically so an outer driver (this session's actual live scanning was driven by Claude
  running one command per real-world action - "the user moved, now run `next <new X>`" - not by
  the user typing into the tool directly) can do one scan/next/findptr step per real-world action
  without needing to keep an interactive process's stdin open across however long that action
  takes (a human actually walking in-game, checking a coordinate, etc.). The normal interactive
  REPL (for a human typing directly) is unchanged and still the default with no `--state` arg.

Both former "stub" providers (B1 paused, B2 now done) exist so the interface and the app's
provider-switching UI are already exercised regardless of which variants are actually finished.


**Automatic client tracking (2026-09-28, user's request):** B2 has no Start/Stop/refresh
buttons any more. The **Klient** menu lists the running clients, with the tracked one checked.
- The menu is rebuilt from `_clientItems` in `ClientMenu_SubmenuOpened`, so the background
  refresh never touches an open menu.
- `ScanClientsAsync` runs at startup and every 2s (`_clientScanTimer`). The process enumeration
  itself runs on a pool thread (`Task.Run`), so it can't hold up a map frame.
- `FindCandidateProcesses` returns OrionUO64.exe processes **oldest first**, and the map tracks
  the first one.
- Picking another client in the menu switches to it. When the tracked client exits
  (`Process.Exited`, so immediately), the map moves to the first remaining client. With none
  running, the view goes to **0,0** without a player marker ("žádný klient") and keeps scanning.
- A switch first resets to 0,0 / "čekám na pozici", so a client at the login screen never shows
  the previous character.
- A client whose memory can't be opened (map not admin, client admin) goes to
  `_failedClientPids`. It isn't retried automatically, only when picked by hand.
- `OnPositionChanged` ignores updates from a provider that is no longer `_b2Provider`.
- `ShowLocalPosition` re-applies `_lastLocalUpdate` after a map load, because the client is
  usually tracked before the map finishes loading and B2 only reports on change.
- Since a client can be caught at the login screen, `ProcessMemoryPositionProvider` re-reads
  the character name from the window title every 2s. It only accepts "Name (Shard)" titles, so
  "Text Dialog" etc. are ignored, and it follows relogs too.
- Cost: one scan is ~3 ms (measured in PowerShell with 257 processes), so ~0.15% of a core at
  one scan per 2s.
- Verified via UI Automation against the user's real clients plus a fake `OrionUO64.exe` (a
  renamed ping.exe, driven through the Klient menu):
  - automatic pick of the oldest client, and a manual switch (the check mark follows);
  - "čekám na pozici" for a client without a position;
  - the fallback to the first client 1s after the tracked one exited;
  - an admin client read by the `--no-elevate` test instance → "Přístup byl odepřen. Spusť
    mapu jako správce."
- Not yet tested: the "no client at all" state (the user's clients were running).
- **Relay while there's no position (2026-09-29, user's call):** `ShowNoPosition` also reports
  0,0 to the relay (`_noClientRelayUpdate`), under the last known name so the room doesn't see
  a "new" player. You stay connected and can chat. It's also the seed when connecting while no
  client runs. It's deliberately kept out of `_lastLocalUpdate`, so shared-marker and panic
  directions aren't computed from 0,0. With no name at all (no display name, never had a
  client) nothing is reported, as before.
  - Verified with a local relay and an observer client: the real client, then a fake one →
    observer saw "Will B Back at 0,0" (same name, no leave), and back to the real position
    when the fake exited. "LEFT" only after Odpojit.

