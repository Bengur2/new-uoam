# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status at a glance (as of 2026-10-01)

A low-latency successor to UO Auto-Map for Ultima Online (C#/.NET 8, WPF), used by this user on
the Dark Paradise shard (Sphere/Source-X, Felucca only) with the OrionUO client. Each item below
has a detailed section further down - this list is just the map of what exists.

| Area | State | Where it's documented |
|---|---|---|
| Map reading (`.mul`/`.uop` land + statics, radar colors) | done, validated byte-for-byte | "NewUOAM.MapData" |
| Precomputed color maps + disk cache | done | "NewUOAM.MapData" (`FacetColorMap*`) |
| Rendering (45°/north-up, even zoom steps, 3x render cache, pan animation, DPI-exact) | done | "NewUOAM.App — WPF" |
| Variant A - OrionUO script → loopback UDP | done; **UI hidden** since 2026-09-24 (user's request), no auto-start | "NewUOAM.Positioning" |
| Variant B1 - packet proxy | **paused** (game hop is encrypted), UI commented out | "NewUOAM.Positioning" |
| Variant B2 - ReadProcessMemory of `OrionUO64.exe` | done, live (facet hardcoded 0); known builds by hash + auto-locate for unknown ones; **automatic client tracking, no Start/Stop** (2026-09-28) | "NewUOAM.Positioning" + "MemoryScanner" + "Automatic client tracking" |
| Menu bar (Mapa/Online/Klient dialogs), map-only mode, window placement memory, auto-load of map + markers | done | "Menu bar", "Window placement memory", "Auto-load" |
| Marker overlay (UOAM `.map`/`.csv`, 199 icons, hover label) | done | "Marker overlay" |
| Side panel = marker browser (sections, categories, search, fly-to) | done | "Side panel = marker browser" |
| Sharing markers with the room (hidden by default at receivers, save to `shared_markers.map`) | done, server deployed 2026-09-29 | "Sharing markers with the room" |
| Multiplayer relay (UDP, positions, glide, edge arrows, display name) | done, deployed | "Multiplayer relay" |
| Rooms (admin create/list/delete, kick on delete); players create their own rooms (server-generated password), admin list with dates + multi-delete + creation switch, 90-day expiry | done; v2 loopback 22/22 + UI test, deployed 2026-10-01 (live 7/7; `.prev` + `rooms.json.bak-20261001` kept on the VM) | "Multiplayer rooms" + "Player-created rooms" |
| Room chat (no persistence, click map + type) | done, deployed | "Room chat" |
| Event-driven presence, player colors, invisible-name error 103, UTF-8 wire | done, deployed | "Event-driven presence..." |
| Map chat shown inside the UO client (UOAssist API via Orion Assistant) | done, verified live | "Map chat inside the UO client" |
| In-game commands `-c`, `-c name>text`, `-panic`, `-unpanic` (UOAssist ADD_CMD, bridge process) | done, verified with simulated OA input; server deployed 2026-09-25 (live test 20/20) | "In-game commands" |
| Track reports `-t name, name` + `TrackPlayers.oajs` + track map window | done 2026-09-30, loopback 11/11 + real app with a local relay; relay deployed 2026-09-30 (live 6/6); script's first live run pending | "Track reports (-t)" |
| Player package + self-update (signed feed on GitHub Releases, in-place file swap) | done 2026-10-01, unit 21/21 + E2E 1.0.0→1.0.1 against a local server; latest release **v1.0.3** (live feed, zip and setup verified) | "Self-update (player package)" + `docs/RELEASE.md` |
| Installer `NewUOAM-Setup.exe` (Inno Setup, per-user, uninstall keeps player files) + website https://bengur2.github.io/new-uoam/ (`docs/index.html`, GitHub Pages) | done 2026-10-01, both live | "Self-update (player package)" → "Installer + web" |
| Public repo `Bengur2/new-uoam` (one clean commit, no account names/emails; old history in private `new-uoam-private`), commits as `168770972+Bengur2@users.noreply.github.com` | since 2026-10-01 | `HANDOVER.md` 6/6b |

Open items: see "Watch list" and "Known follow-up work" at the bottom. **On a new machine, read `HANDOVER.md`
first** (Czech handover written 2026-09-24 when the user moved to a second PC). It has the
step-by-step for the B2-doesn't-work-on-the-other-PC problem and the exact client-build
fingerprint that B2's offsets belong to.

## Commands

```bash
dotnet build NewUOAM.slnx -c Debug              # build everything
dotnet run --project src/NewUOAM.App            # run the WPF viewer
dotnet run --project src/NewUOAM.RelayServer    # run the multiplayer relay server (see below)
dotnet run --project tools/UopProbe             # scratch/validation tool (see below)
powershell -ExecutionPolicy Bypass -File tools\Release\release.ps1 -NotesFile notes.txt [-Publish]   # player package / release (docs/RELEASE.md)
```

No test project exists yet. `tools/UopProbe/Program.cs` is a throwaway scratch program (its
content gets overwritten repeatedly) used to empirically validate map-format assumptions against
the real client files on this machine — treat it as a REPL, not a persistent test suite. When
picking real client data to validate against, `C:\Moria` has both `.mul` and the `.uop` variant
of every facet (so parser output can be diffed against known-good raw bytes); `C:\_PERSONAL\Games\DP\Ultima Online DP`
has `.uop` only, which is the shape the app has to handle in the field.

## Architecture

Core projects, no circular deps: `NewUOAM.MapData` (no dependencies) ← `NewUOAM.Positioning`
(depends on nothing else either — it only models position data/transport) ← `NewUOAM.App` (WPF,
depends on both). `NewUOAM.MemoryScanner` is a standalone console tool for Variant B2 (see below).
`NewUOAM.UoaBridge` is a tiny dependency-free helper exe for in-game commands (see "In-game
commands"); `NewUOAM.App` references it only to get its files copied next to the app.
`NewUOAM.RelayServer` is a standalone console app for the multiplayer relay (see below) - it only
depends on `NewUOAM.Positioning` (for `PositionUpdate` and the shared `Relay/RelayProtocol.cs`
wire format), not on `NewUOAM.App` or `NewUOAM.MapData`.
`NewUOAM.Updates` (no dependencies, no WPF) holds the self-update: the signed feed format, download
and the file swap. `NewUOAM.App` and `tools/NewUOAM.ReleaseTool` (publisher side) both use it.

### NewUOAM.MapData — reading the client's own files, zero network/game dependency

- `Uop/UopFile.cs` + `Uop/UopHash.cs` — generic reader for the Mythic `.uop` container format.
  Entries are keyed by a 64-bit hash of a synthetic file name (the format has no directory
  table); **do not** assume physical file order equals logical order — some facets store patch
  entries out of sequence. Always resolve `logicalIndex -> entry` through `UopFile.TryGetEntry`
  with the name pattern documented in `docs/UOP_FORMAT_NOTES.md`, never by iterating
  `EntriesInFileOrder` positionally (that only worked by coincidence for a couple of facets and
  was proven wrong for Ilshenar/map2 during development — see that doc for the full story).
- `Maps/MulMapBlockSource.cs` / `Maps/UopMapBlockSource.cs` — both implement `IMapBlockSource`
  (raw 196-byte block access). `Maps/MapFacet.cs` picks `.mul` over `.uop` when both exist (the
  `.mul` path is trivially simple and used as ground truth during development) and falls back to
  `.uop` otherwise.
- `Maps/FacetInfo.cs` holds the known width/height (in 8x8-tile blocks) per stock facet index.
  These are empirically measured, not textbook values — see `docs/UOP_FORMAT_NOTES.md` if they
  ever look wrong for a given facet. `MapFacet.Open`'s `ResolveDimensions` never blindly trusts
  a facet index's "expected" width — a shard can give facet slot N the dimensions normally
  associated with a *different* stock facet (hit in practice: the DP shard's Felucca/facet 0 is
  sized exactly like stock Trammel, 896x512 not 768x512 blocks). Blindly keeping the expected
  width and truncating height via integer division silently produces a wrong stride, which
  renders as diagonal/striped garbage — every candidate width (and a few common heights) is
  checked against the actual measured block count instead, picking whichever divides evenly.
- Cell ordering within a block is row-major (`cellIndex = cellY * 8 + cellX`) — this was
  determined by comparing terrain-elevation smoothness between the two possible orderings, not
  from documentation, because no reliable public source could be found. Don't change it without
  re-running that check (see `docs/UOP_FORMAT_NOTES.md`).
- `Colors/RadarColorTable.cs` reads `radarcol.mul` (flat array of 16-bit 5-5-5 RGB, one entry per
  tile/graphic id) — this is the same table the in-game radar map uses, so it gives
  visually-authentic colors without needing a full `tiledata.mul` parser.
- `Markers/MarkerFileReader.cs` parses both the legacy UOAM `.map` format
  (`+IconName:X Y MapIndex Name`) and a `.csv` format
  (`x,y,mapindex,name,iconname,color,zoom`), for compatibility with existing community marker
  files. Wired into `NewUOAM.App`'s "Markery" UI (see the marker overlay entry further down) -
  `MarkerEntry.Visible` reflects the file's own leading `+`/`-` (UOAM's own per-category default
  visibility, not just "does this entry exist"). Since 2026-09-28 the UI uses it only as a
  category's *default* (see "Side panel = marker browser").
- `Maps/StaticItem.cs` + `Maps/MulStaticsSource.cs`: statics (buildings/trees/fences/...) from
  `staidxN.mul` (12-byte-per-block index: int32 offset into staticsN.mul or -1, int32 length,
  int32 unused) + `staticsN.mul` (7-byte records: ushort tileId, byte x, byte y [0-7 within the
  block], sbyte z, short hue). Only the `.mul` path exists - both test clients have it directly,
  no `.uop` variant needed yet. Wired into `MapFacet` (`GetStaticsAt`/`TryGetTopStatic`, its own
  `LruCache<long, IReadOnlyList<StaticItem>>` alongside the land-block cache) and into
  `FacetColorMap.Build` / `MainWindow.Redraw`'s fallback path: the **topmost** (highest Z) static
  on a tile is drawn instead of the land tile, matching how the in-game radar map only ever shows
  one thing per tile. Its radar color comes from `radarcol.mul` at index `tileId + 0x4000` (the
  same flat table land tiles use - land occupies 0x0000-0x3FFF, item/static graphics continue
  from 0x4000, confirmed by the table's own size: 65536 = 16384 + 49152). Empirically validated
  against `C:\Moria`/Felucca (`tools/UopProbe`): 2.76M statics total, no out-of-range X/Y, and a
  200x200 area around Britain has ~735x more statics than one near the map corner. Adds real time
  to `PreloadAllColorMapsAsync` (~13s land-only -> ~21s with statics on `C:\Moria`'s 6 facets) -
  not yet optimized (statics lookups aren't prefetched/batched, only individually block-cached).
- `Util/LruCache.cs` — the *only* LRU cache in the codebase; `MapFacet`'s raw-block cache and
  `UopMapBlockSource`'s decompressed-group cache both use it. **Do not hand-roll another one.**
  A from-scratch version of this exact cache (`Dictionary` + `LinkedList<TKey>`, evicting via
  `LinkedList.Remove(value)`) was the real cause of a severe perf regression: `Remove(value)` is
  an O(n) scan for the matching node, so a full cache made every subsequent lookup scan up to
  `capacity` entries — preloading one ~25M-tile facet went from ~4s to "doesn't finish in 2
  minutes". `LruCache<TKey,TValue>` fixes this by having the dictionary hold the
  `LinkedListNode<T>` directly, so touching an entry is `LinkedList.Remove(node)` (O(1)).
- `Colors/FacetColorMap.cs` + `UoClientData.PreloadAllColorMapsAsync` — decodes every tile of a
  facet to color exactly once (1 px/tile BGRA array) instead of re-decoding raw blocks + a radar
  lookup on every repaint. Built in the background right after "Načíst mapu" opens facet 0 (see
  `MainWindow.LoadMapButton_Click`); ~13s / ~270MB for all 6 facets on `C:\Moria`. `MainWindow`
  falls back to the raw `MapFacet.GetLandTile` path for a facet whose color map isn't ready yet,
  so the app stays usable (just slower for that facet) during the initial preload window.
- `Colors/FacetColorMapDiskCache.cs` — persists a built `FacetColorMap` to
  `%LocalAppData%\NewUOAM\colormap-cache\<sha1(clientDir)>\facet{N}.bin` (`UoClientData.GetOrBuildColorMap`
  tries this before falling back to `FacetColorMap.Build`) so the several-seconds-per-facet decode
  doesn't have to happen on every single app launch - the user explicitly asked for exactly this
  ("aby mapa zůstala načtená i po vypnutí programu"). Invalidation is automatic and exact: the
  cache file records `(LastWriteTimeUtc.Ticks, Length)` for every one of `MapFacet.SourceFilePaths`
  plus `radarcol.mul` at save time, and a load is rejected (falls through to a real rebuild) if any
  of those don't match bit-for-bit on load - re-patching the client or hand-editing a `.mul`
  invalidates automatically, nothing has to remember to bust the cache manually. Verified against
  real Moria data in `tools/UopProbe`: cache load is ~0.03s vs ~6.6s to build (200x), byte-identical
  output, and touching `map0.mul`'s mtime does force a real (slow, correct) rebuild.

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

### NewUOAM.App — WPF

`MainWindow.xaml.cs` owns a `UoClientData` (facet cache + radar colors for one client install)
and whichever `IPositionProvider` is active.

**`Redraw()` renders at physical screen pixels, not WPF's 96-DPI logical units - this matters a
lot, found from the user reporting tiles looked "too sharp/rounded" (compared to true square
pixels in the real client / old UOAM) and that the roundedness visibly changed during movement,
worse at low zoom.** A `WriteableBitmap` declared at 96 DPI still gets resampled by WPF's own
compositor to the monitor's real DPI whenever Windows scaling isn't 100% (this machine runs
125%/120 DPI, one of the most common Windows defaults) - and that compositor-level resampling is
NOT controlled by the `Image`'s `RenderOptions.BitmapScalingMode="NearestNeighbor"` (that only
governs *explicit* scaling of the image within its own layout, not the separate system-DPI
compositing pass). The softening this caused shifts as content pans (which physical pixel a given
logical one lands on changes continuously) and is proportionally worse at low zoom (a ~1-physical-
pixel blur matters far more when a tile is only 2px wide than when it's 24px wide) - matching
every symptom reported. Fixed by getting the real DPI via `VisualTreeHelper.GetDpi(this)` and
sizing/declaring `_bitmap` in real physical pixels (`ActualWidth * dpi.DpiScaleX`, etc., declared
at `96 * dpi.DpiScaleX` DPI) instead of hardcoded 96 - WPF's compositor then has nothing left to
rescale, one physical pixel in, one out. (Confirmed via a DPI-aware Win32 `GetDeviceCaps` query,
not .NET's own `Graphics.DpiX`/`Screen` APIs - those report the *virtualized* 96 DPI for any
calling process that hasn't itself declared DPI-awareness, e.g. plain `powershell.exe`, and will
misleadingly claim 100% scaling on a real 125% system.)

**Rendering is a two-layer cache, matching how old UOAM actually works - render into a raster
once, pan by cropping a moving window over it - not "recompute colors every repaint."**
`RenderRegion()` does the per-*destination-pixel* inverse-sampling color computation (world tile
via `ScreenToWorld`, `FacetColorMap.Sample` when the color map is ready, else falling back to
`MapFacet.GetLandTile`/`TryGetTopStatic`) but is only ever called by `EnsureCache()`, into an
offscreen buffer (`_cacheBgra`) sized `CacheMarginFactor` (3) times the viewport in each
dimension - i.e. a full viewport's worth of margin on every side. `Redraw()` itself never calls
`RenderRegion` directly: it asks `EnsureCache` to (re)build the cache only if the mode/zoom/facet
changed or the current viewport would need pixels outside the cached region, then blits
(`Array.Copy`, not resampled) the viewport-sized crop out of the cache into `_bitmap`. As long as
the player stays within the margin, a repaint is a pure memory copy - this is not an optimization
detail, it's the actual fix for a real bug: an earlier version recomputed every pixel from scratch
on every repaint, and even with rendering mathematically proven pixel-stable under panning (see
below), the user could still tell it apart from old UOAM and asked directly "can we render it the
way UOAM does". Validated in `tools/UopProbe` with a synthetic-color simulation of a few hundred
random-walk steps: the cache-blit result matches a from-scratch direct render at every single
step, and the cache absorbs >99% of steps as pure reuse (only regenerating a handful of times).

`_cacheCenterX/_cacheCenterY` (double) record which world position the cache was generated for;
the crop offset from the current view is `ProjectDelta(_viewX - _cacheCenterX, _viewY -
_cacheCenterY)`, rounded to the nearest pixel - reuses the same projection math as
`WorldToScreen`, generalized to a delta instead of a point. For whole-tile deltas this rounds to
an exact integer *only because* `_pixelsPerTile` is constrained to always be even (see below) -
without that constraint this cache would silently produce torn/misaligned crops.

**`_centerX/_centerY` (int, the exact logical player tile) vs `_viewX/_viewY` (double, what
rendering actually uses) are deliberately two different things**, animated between by
`BeginPanAnimation`/`OnAnimationTick`: even with the cache fix above mathematically eliminating
resampling artifacts, the user could still tell every repaint was a discrete multi-pixel jump and
said so directly ("furt se to mění, nepříjemné pro oko... nemůžeme to dělat stejně jako UOAM" -
asked, in effect, for the *cadence* to match too, not just the pixel-exactness). `BeginPanAnimation`
retargets `_animFromX/Y -> _animToX/Y` (continuing smoothly from wherever `_viewX/_viewY` already
is, even mid-flight, if updates arrive faster than the animation finishes) and subscribes to
`CompositionTarget.Rendering` (unsubscribing once done, so an idle map costs nothing) to ease
`_viewX/_viewY` toward the target over `AnimationDurationMs` (150ms). This is safe precisely
because the cache content is never touched during an animation - only the integer crop offset
read out of it changes each frame, so a fractional/rounded `_viewX/_viewY` costs at most a
sub-pixel wobble in *where the window sits*, never a re-decoded tile boundary. The player marker
is drawn at a fixed screen position (dead center) rather than via `WorldToScreen`, deliberately -
its world position is always exactly the pan target, so it belongs glued to the middle of the
screen (background slides under it) rather than drifting across the screen as `_viewX/_viewY`
catches up, matching the "camera follows player" convention every top-down map/game uses.

**A real bug this animation introduced, found and fixed the same session (the user's "bublání
1x1 pixelů" report):** `EnsureCache`'s regeneration used to set `_cacheCenterX/Y = _viewX/_viewY`
directly - fine when idle (an integer), but during an animation `_viewX/_viewY` is fractional, and
if a regeneration happens to land mid-animation (inevitable over any sustained walk, once the
margin runs out), the new cache's origin lands on a different fractional sub-pixel phase than the
old one. Two individually-correct caches at different fractional phases are *not* guaranteed
phase-aligned with each other (only integer-to-integer deltas are, per `ProjectDelta`'s
contract) - so near-single-pixel features (small statics, terrain speckle) could visibly hop by a
fraction of a pixel exactly at a regeneration boundary. Fixed by always generating the cache
centered on the exact integer `_centerX/_centerY`, never the live `_viewX/_viewY` - `RenderRegion`
now takes its center as an explicit `(double centerX, double centerY)` parameter instead of
reading `_viewX/_viewY` via `ScreenToWorld`, specifically so `EnsureCache` can pass the *intended*
integer center without needing to temporarily mutate shared state. Verified in `tools/UopProbe`:
two caches generated at nearby fractional centers are demonstrably NOT phase-aligned (confirms the
bug was real), while two generated at `Math.Round()`ed versions of the same centers always are
(4/4 across render modes and zoom levels) - `Round()` is exactly what passing `_centerX/_centerY`
(already integer) achieves.

**A second, more serious bug the animation introduced crashed the app outright** ("kdykoliv
zapnu UDP, aplikace spadne"): `BeginPanAnimation` never bounded how far apart `_viewX/_viewY`
(the animation's starting point) and the new target could be. The very first position update
after clicking Start - jumping from the default map-center view to wherever the player actually
is, routinely thousands of tiles away - would animate across that entire distance over just
150ms. `EnsureCache`'s render cache only ever has about one viewport's worth of margin, sized
relative to the *viewport*, not to how far `_viewX/_viewY` might roam mid-animation - so partway
through that animation, the crop needed for the current `_viewX/_viewY` could land completely
outside even a just-regenerated cache, and `Array.Copy` in `Redraw()` would throw
`ArgumentOutOfRangeException`, crashing the app. Two independent fixes, both present:
`MaxAnimatedDistanceTiles` (40) makes `BeginPanAnimation` snap instantly instead of animating for
any jump bigger than ordinary footsteps (a recall, facet change, or the first position after
Start should never have animated across the whole map anyway); and `Redraw()` now unconditionally
`Math.Clamp`s `srcX`/`srcY` into the cache's valid range before the `Array.Copy` regardless - pure
defense-in-depth, since `_cacheWidth - width` (`CacheMarginFactor` is 3, i.e. always `>= 2*width`)
is always `>= 0` this can never itself throw. Verified end-to-end (not just logically): launched
the real app via UI Automation, clicked Start, and fed it the literal crash scenario over real UDP
- a jump from the default view straight to a position 1000+ tiles away, then a burst of 20ms-spaced
small steps - the app survived both. That session also picked up this machine's *actual* live
OrionUO feed (an old `PositionFeed()` run was apparently still going from earlier testing and had
nothing listening on port 27974 until this test bound it) - real Enter World -> `Player.Name()`
"Primarch Mortarion" data flowed through the exact code paths above without crashing either.

`OnPositionChanged` (called from the position provider's own thread, e.g. the UDP receive loop)
uses `Dispatcher.BeginInvoke`, never `Invoke` — and the actual `Redraw()` call goes through
`RequestRedraw()`, which coalesces bursts of updates into a single pending repaint instead of
queuing one per position update. Getting either of these wrong reintroduces the exact "app gets
horribly stuck once the Orion feed is running" regression this was built to fix: a blocking
`Invoke` stalls the provider's receive loop on every repaint, and one repaint per update (instead
of "at most one pending, using the latest state") backs up once repaints take longer than the
~50ms between updates.

`AppSettings.cs` persists the client folder path and the two overlay checkbox states to
`%LocalAppData%\NewUOAM\settings.json` (loaded in the constructor, saved on window `Closing` and
right after a successful "Načíst mapu" so the path survives even if the app doesn't close
cleanly). The coordinate readout (`CoordinatesOverlay`) and the four-corner compass
(`CompassTopLeft`/`TopRight`/`BottomLeft`/`BottomRight`) are plain WPF `TextBlock`s layered over
`MapImage` in a `Grid` (not drawn into the bitmap) so they stay crisp text regardless of zoom;
`UpdateCompassOverlay()` picks which letters go in which corner based on `_renderMode` (matching
old UOAM's own W/N/S/E-in-corners convention for `Rotated45`; plain NW/NE/SW/SE for `NorthUp`,
since that mode has no single "which corner is north" answer the way the diagonal view does).

Two projections share the same per-pixel loop, selected by `_renderMode`
(`WorldToScreen`/`ScreenToWorld` branch on it):
- `Rotated45` (default): classic UO isometric-style screen orientation — `(dx-dy)*h, (dx+dy)*h`
  with `h = pixelsPerTile/2`. This is a plain rotation+uniform scale (not an anisotropic 2:1
  squash) — confirmed against a real old-UOAM screenshot, whose diamond tiles read as
  symmetric, not flattened.
- `NorthUp`: straight `dx*pixelsPerTile, dy*pixelsPerTile`.

`_pixelsPerTile` (mouse wheel, `[2, 24]`) is the only zoom knob and is shared by both projections.
**It must stay an even integer.** The wheel handler steps it by a flat `±2`, not a multiplicative
factor - an earlier version used `*1.15`/`/1.15`, and after even one scroll click `pixelsPerTile`
became some non-integer float (e.g. 4.6), which broke `Rotated45` specifically: that projection
moves the screen by `pixelsPerTile/2` pixels per 1-tile step, and unless that's a whole number,
each step the player takes shifts the tile grid's phase relative to the pixel grid by a fraction,
which nearest-neighbor sampling renders as pixels "reshuffling" near every tile edge on every
move - a real bug the user caught by comparing against old UOAM, where panning only ever slides a
pre-rendered raster and never re-derives tile boundaries, so nothing can reshuffle. Verified with
a standalone numeric check in `tools/UopProbe` (not the app itself): for an even `pixelsPerTile`,
rendering two frames whose center differs by N tiles produces bit-for-bit identical tile grids up
to an exact whole-pixel shift (checked at several zoom levels, both projections); for an odd value
the same check shows tens of thousands of mismatched pixels per single-tile move in `Rotated45`
(confirms both the diagnosis and that the check itself is meaningful, not a tautology). `NorthUp`
alone would have been fine with any integer - it only needed `Rotated45`'s stricter "even" rule.

Map-only mode (double-click) collapses `SettingsPanel`/`StatusBarBottom` (via
`UpdateSettingsPanelVisibility`, which also hides `SettingsToggleButton` - see the UI layout entry
below), drops `WindowStyle`/`ResizeMode` to borderless/no-resize, and sets `Topmost = true`. A single
click-drag in that state calls `DragMove()` (`MapBorder_MouseLeftButtonDown` - double-click still
toggles the mode, single-click-drag only does anything while already in map-only mode, so it
doesn't hijack plain clicks in the normal window). **This whole rendering/interaction layer is
provider-agnostic** — it only reads `_currentFacet`/`_centerX`/`_centerY`/`_markerX`/`_markerY`,
set from `OnPositionChanged`, so B1/B2 need zero UI changes, just another `IPositionProvider`.

**UI layout: collapsible side panel, not the old always-visible top toolbar.** (Historical: since
2026-09-28 every setting lives in the menu dialogs and the panel is the marker browser - see
"Menu bar" and "Side panel = marker browser". The clipping/fixed-width reasoning below still
applies.) Replaced
after the user hit two real problems with the original multi-row horizontal `StackPanel` toolbar:
(1) marker/remote-player icons positioned near the map's edge could visually bleed into the
toolbar/status bar above/below it, because WPF panels don't clip their content to their own layout
bounds by default; (2) narrowing the window hid toolbar buttons entirely, since a horizontal
`StackPanel` doesn't wrap or scroll. Fixed both together, not separately:
- `MapBorder` now has `ClipToBounds="True"` - this alone fixes the bleed-through for every overlay
  (markers, remote players, the self marker, coordinate/compass text), not just markers
  specifically, since it clips at the shared map container rather than per-overlay.
- All the old toolbar rows moved into `SettingsPanel` (a `Border` docked left, **fixed width
  (320px)**, containing a `ScrollViewer` for its content) - deliberately fixed-width and
  independent of the window/map width, so controls can never get clipped by narrowing the window
  again; content is grouped into labeled sections (KLIENT / VARIANTA A / MULTIPLAYER / MARKERY)
  with consistent vertical spacing instead of one continuous horizontal run per row.
  `SettingsToggleButton` (☰, fixed top-left over the map, declared last in the map `Grid` so it's
  always the topmost element) shows/hides it - state in `_settingsPanelOpen`, persisted via
  `AppSettings.SettingsPanelOpen` (defaults `true`), applied through `UpdateSettingsPanelVisibility`
  (also folds in the map-only-mode check, so one method governs both reasons the panel might be
  hidden). Toggling it resizes `MapBorder`, which already triggers a repaint via the existing
  `MapBorder_SizeChanged` handler - no extra redraw call needed.
- Verified via non-invasive testing only (`PrintWindow` screenshots + `SetWindowPos`/UI Automation
  `InvokePattern` to resize/toggle programmatically, no `SendInput`/`Cursor.Position`) - a real
  screenshot at a deliberately narrowed 500px window width confirmed every control stays visible
  (panel's own `ScrollViewer` kicks in vertically instead of anything getting clipped
  horizontally), and toggling the panel closed confirmed the map cleanly fills the freed space.
  This followed directly from the "don't hijack the user's real cursor" lesson recorded in the
  marker overlay section above - none of this verification needed to move the mouse at all.

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

**Side panel = marker browser (2026-09-28, user's request, modeled on a colleague's "Moria
Reborn Atlas"):** the ☰ panel now holds only the marker list (`MainWindow.MarkerPanel.cs`).
Every setting moved to the menu dialogs; ZOBRAZENÍ (rotation, coordinates, compass) is the
third section of Mapa > Nastavení.
- **Layout.** Search box (case- and diacritics-insensitive), Vše/Nic, an "N zobrazeno" count,
  and a virtualizing `ListBox` of flat rows. `MarkerCategoryRow` has a checkbox, icon, name,
  count and ▸/▾. `MarkerEntryRow` has an icon, name and x,y.
- **Categories.** A category is the normalized icon name, the same grouping as old UOAM's +/-.
  - Its default visibility comes from the files: visible if any of its markers is `+`.
  - A checkbox or Vše/Nic overrides the default, stored in `AppSettings.MarkerCategories`
    (key → bool).
  - `UpdateMarkersOverlay` uses `IsMarkerVisible` (category) instead of the per-entry
    `MarkerEntry.Visible`.
- **Clicking.** Clicking a category row expands it in place (no full rebuild, so the scroll
  position stays), and search shows everything expanded. Clicking a marker row turns Track
  Player off, `FlyTo`s the marker and shows its hover label for 3s.
  - Both are `PreviewMouseLeftButtonUp` on the item container, ignoring clicks inside the
    checkbox. UI Automation can't trigger them, so they were checked by the user, not by the
    automated test.
- **Facet.** The list shows the facet on screen, and `UpdateMarkersOverlay` rebuilds it when
  the facet changes.
- **Auto-load.** Markers load at startup together with the map (`AutoStartAsync`), since the
  panel is useless empty.
- **Found in testing:** the user's DP marker files have 426 of 434 markers on MapIndex 1
  (Trammel) and only 8 on 0. DP's facet 0 is Trammel-sized (see `ResolveDimensions`), so these
  files were probably made for UOAM treating the DP map as Trammel. **Resolved 2026-09-28:** the
  user's `DP Dungy.map`/`DP Mesta.map` in `C:\Games\DP\Ultima Online DP` were converted to
  MapIndex 0 (all 434 now on facet 0; the originals with index 1 are the `.bak` files next to
  them). No code change - the app still filters by facet as before.

**Sharing markers with the room (2026-09-29, user's request)**, in `MainWindow.SharedMarks.cs`
(app side) and `RelayProtocol` `UOAMK*1` messages (wire side).
- **Sharing.** Right-click a marker (panel or map) → "Sdílet s místností", or a category row
  → "Sdílet kategorii…". "Přestat sdílet" works for one marker or a category, and "Přestat
  sdílet všechny" is on the MOJE MARKERY header.
  - A marker's id is a hash of its content (`SharedMarkId`), so re-sharing is idempotent.
  - The client paces outgoing changes at 25 per 50ms tick.
- **Panel sections.** MOJE MARKERY (all files except `shared_markers.map`), ULOŽENÉ OD OSTATNÍCH
  (`shared_markers.map`, category keys prefixed `S:`), SDÍLENÉ V MÍSTNOSTI (player → markers).
- **Receivers: hidden by default** (user's call: a mis-clicked big category mustn't flood the
  map). Three switches, memory only: section checkbox (everyone) › player checkbox (default
  off; resets that player's per-marker choices) › per-marker override. Vše/Nic don't touch
  shared markers.
- **Drawn** as the icon in a 1px frame of the owner's color. Hover: "Name (od X)". Right-click:
  save / hide this / hide all from X.
- **Uložit** (button on a row, "Uložit vše od X" on a player row) appends to
  `shared_markers.map` in the effective markers folder via `MarkerFileStore`, skipping local
  twins (same X, Y, map and name, `_localMarkerKeys`). A shared marker with a local twin isn't
  drawn twice and its row says "uloženo". Unshare and leave never touch saved copies.
- **Right-click on a marker row in the panel (2026-09-29)** opens the same menu as its icon on
  the map (`ShowMarkerContextMenu`: drop/shared marker, share, move, Edit/Delete) and doesn't
  fly to it. Section/category/player rows keep their own menus.
- **Merging into your own markers (2026-09-29).** Right-click the ULOŽENÉ OD OSTATNÍCH header,
  a saved category, a saved marker row or its icon on the map → "Přesunout … do Moje markery ▸
  <file>" (every marker file in that folder except `shared_markers.map`, plus
  `NewUOAM Labels.map`). `MoveToOwnMarkers` appends to the target first, then removes the line
  from `shared_markers.map`. A marker you already have in your own files is only removed.
- **Category spelling (2026-09-29, user's report: new categories were saved all caps).** The
  dialog's type list is the icon file names ("TOWN"). `IconSpelling` writes a newly picked type
  (and a type saved from a share) the way your files already spell it, else in normal case
  ("Town"). The panel shows an all-caps category name in normal case too, preferring a
  non-all-caps spelling from any file, so old "TOWN"/"BANK" labels read "Town"/"Bank".
- **Announcements.** A live share is announced once per burst ("X sdílí N markerů") in the
  status bar, the chat (if open) and in game.
- **Server.** Sets per room and owner, memory only, capped at `MaxSharedMarksPerPlayer` (1000).
  Removed (broadcast `*`) when the owner leaves, whether by bye or timeout.
- **Sync over UDP.** Each change bumps the owner's version. A client applies only the next
  version; a gap, or a 3s-sweep manifest (version + count) that disagrees, triggers a resync
  request (throttled to 1/s per owner). The server answers with begin + all markers, paced 50
  per 10ms. Newcomers get the resync for every owner right away.
- **Validation** on both sides (`CreateSharedMark`): id alphanumeric ≤16, coordinates in range,
  icon letters/digits/space/-/_ ≤40, name ≤80 with **control characters replaced**. A line break
  would otherwise inject lines into `shared_markers.map` on save.
- **Verified:**
  - Loopback protocol test (14 checks): single, category of 300 in ~0.7s, batched
    announcements, late-joiner resync, unshare one/all, sanitized name, cap of 1000, owner
    leaving, and against a fake server that a version gap and a bad manifest both request a
    resync.
  - The loopback test caught a real bug: `ResyncRequestedAt = long.MinValue` overflowed the
    throttle check, so no resync was ever requested.
  - The real app via UI Automation with a scripted second player: hidden by default (434→434),
    player checkbox (→487), search, Uložit → file line + ULOŽENÉ section + "uloženo", unshare
    and leave keep the saved copy.
  - **Not automatable:** the right-click share menus (UIA can't open them). Sharing from the
    app needs a manual check.
- **Deployed 2026-09-29.** The previous binary is kept as `NewUOAM.RelayServer.prev`. The same
  protocol test (all 10 relay checks) passed live against `89.168.122.175:27980` in a throwaway
  room, which was deleted afterwards.

**Menu bar (2026-09-28, user's request, modeled on old UOAM's File/Zoom/Places/...):** `MainMenu`
under the title bar, hidden in map-only mode (`SetMapOnlyMode`). So far:
- **Mapa > Nastavení…** (`ClientSettingsContent`) has two sections:
  - "Soubory mapy (.mul / .uop)": the folder holding `map0.mul`/`map0LegacyMUL.uop`, statics
    and `radarcol.mul`, plus Procházet and Načíst mapu.
  - Markers, moved here from the side panel. **An empty markers folder means the map files
    folder** (`EffectiveMarkersDirectory`, also used by `EnsureMarkersDirectory` for new
    labels). The empty box shows a gray `(složka mapy: …)` hint (`MarkersDirPlaceholder`).
  - Both sections have a short description of what the folder must contain.
- **Online > Připojit k mapě…**: the whole former MULTIPLAYER section (`OnlineSettingsContent`).
  It includes Admin… and the in-game command hint.

These controls moved out of the side panel. It keeps ZOBRAZENÍ (rotation, coordinates,
compass), B2 and markers. The two panels are still declared in `MainWindow.xaml` and owned by
`MainWindow`, inside a collapsed `DialogPanelStore` grid. `HostedPanelWindow` (owned, one per
panel, Esc closes) moves the panel in when it opens and back when it closes. So all existing code
that reads/writes those controls (settings, connect state, error dialogs) works unchanged with
the dialog closed. Consequence: **no `ElementName` bindings inside these panels**, since they
don't resolve after the move. `PlayerColorPopup.PlacementTarget` is set in code for that reason.
Verified via UI Automation (both dialogs open twice, saved values present, `PrintWindow`
screenshots). Note for UIA tests: owned windows appear as descendants of the owner, not as
top-level windows.

**Zoom range** (`ZoomLevels`, mouse wheel) covers two regimes sharing one phase-stability
invariant. `pixelsPerTile >= 2` (even integers 2-24, `D=1`) is the original zoom-in range
described above. Below that, `pixelsPerTile = 2/D` for integer `D` up to 8 (down to 0.25 px/tile)
lets multiple world tiles collapse into one screen pixel for zoom-out. To keep the same "any two
cache-generation centers are pixel-exactly aligned" guarantee at `D>1`, `EnsureCache` snaps the
cache's regeneration center to the nearest multiple of `D` tiles (not the exact player position) -
any two multiples of `D` project to an exact integer pixel delta via `ProjectDelta` the same way
two exact-tile centers already did for `D=1`. No box-filtering/averaging was added for the
`D>1` case - each screen pixel still nearest-neighbor-samples one representative tile out of the
several it covers, which can look visually noisier at max zoom-out than a proper downsample would,
but doesn't reintroduce the pan-shimmer bug (that was about frame-to-frame drift, which the
`D`-snapping addresses directly). Verified in `tools/UopProbe`: multiple-of-`D` centers always
project to exact integer pixels for `D` 1-8 in both render modes (and non-multiples demonstrably
don't, confirming the check isn't vacuous).

**Right-click menu, Track Player, New Label (2026-09-24)**: `MapBorder_MouseRightButtonUp` builds
a `ContextMenu` modeled on old UOAM's. Only two of its items exist so far, as the user asked:
- **Track Player**: `_trackPlayer`, default on, not persisted. Off means `OnPositionChanged` stops
  calling `BeginPanAnimation`, the self marker is drawn via `WorldToScreen` instead of dead
  center, and a left-drag pans the map (`_dragStart` + mouse capture). The drag sets
  `_viewX/_viewY` directly and keeps `_centerX/_centerY` = the rounded view, because `EnsureCache`
  always centers the cache on `_centerX/_centerY`. With tracking off, a drag in map-only mode pans
  instead of moving the window. Turning tracking back on pans back to the player (snaps if far).
- **New Label...** and **Edit / Delete** (right-click on a marker icon; its handler sets
  `e.Handled`, so the map menu doesn't open too) share `LabelEditWindow`, old UOAM's "Edit Label"
  dialog without Latitude/Longitude:
  - Name.
  - Type: the bundled icons. A file's own spelling ("landmark", "Ostatní") is kept unless
    changed.
  - X/Y: prefilled from the clicked tile, editable, validated against the facet size.
  - Land: facets from `_clientData`.
  - File: every `.map`/`.csv` in the markers folder, plus `NewUOAM Labels.map`, the default.
  `_markers` holds `LoadedMarker(Entry, FilePath)`. A new label goes to
  `%LocalAppData%\NewUOAM\markers` if no markers folder is set.
- **`MarkerFileStore`** (MapData) does all the writing. Real marker files are ANSI: Moria's
  `Shadow.map` is Windows-1250 with the icon `Ostatní` and CRLF endings, and the reader used to
  decode it as UTF-8. So:
  - Encoding is detected per file: valid UTF-8 with non-ASCII bytes means UTF-8; anything else,
    including new files, is system ANSI, which old UOAM/Orion read.
  - `MarkerFileReader.Read` now uses the same decoding.
  - Edit/Delete replaces or drops exactly one raw byte line (matched by record equality with
    the loaded entry). Every other line and its ending stays byte-identical, and a line that
    can't be found is reported, never guessed.
  - `<file>.bak` is written once before the first change to an existing file.
  - Moving a label to another file appends there first, then deletes the original.
  - Tested on copies of the real `Shadow.map` and `NewUOAM Labels.map` (20 checks: exactly one
    line differs, the `Ostatní` bytes are untouched, Czech names round-trip in ANSI, CSV works).
The self marker is now a red 5×5 square.

**Go to location... (2026-09-29, user's request)** in the map's context menu (after New
Label...): `GoToLocationWindow` asks for X/Y (prefilled with the clicked tile, validated against
the facet on screen), then `GoToLocation` `FlyTo`s there. **Track Player is not turned off**
(user's correction the same day): with it on, `_goToHold` keeps `ViewFollowsPlayer` false, so the
view waits on the target. The first position update that actually moves your character
(`ShowLocalPosition`) calls `ReturnFromGoTo`, which flies back and then follows again. Toggling
Track Player or a panic peek ends the hold. With Track Player off it's a plain fly-to. A yellow
crosshair (`GoToCrosshair`, placed by `UpdateGoToCrosshair` in `Redraw`) marks the tile for
`FlightMs` + `GoToCrosshairSeconds` (4s). Its timer starts with the flight, because a step
during the flight replaces that flight's "done". The real mouse pointer isn't moved.

**Own marker color + edge arrow (2026-09-29, user's request).** Mapa > Nastavení > Zobrazení has
"Barva mé postavy" (`SelfMarkerColorPopup`, the same palette grid as the player color via the
shared `ColorSwatchTemplate` resource, any #RRGGBB allowed, "Výchozí (červená)" resets to
`FF0000`; `AppSettings.SelfMarkerColor`, null = default). It colors the square `DrawMarker`
draws and `SelfArrow`. Only on your own map; others see your player color. With Track Player off
(or during a panic peek), once your square is out of view, `UpdateSelfArrow` (called from
`Redraw`) shows `SelfArrow` at the map's edge pointing toward your character. It uses the same
`PlaceEdgeArrow` math as the other players' arrows. Clicking the arrow
(`SelfArrow_MouseLeftButtonDown`, on button down and handled, so the map's click/drag doesn't
also run) flies back to your character. With Track Player on it reuses the "Go to" fly-back
(`_goToHold` + `ReturnFromGoTo`, cancelling a panic peek) and then follows again. With it off it
just `FlyTo`s. The arrow's canvas is hit-testable, but has no background and the crosshair isn't,
so other clicks still reach the map.

**Drop or Pickup Marker / Shared Marker (2026-09-25)**, old UOAM's "point to run to":
- **Menus.** The map menu and a label's menu (before Edit/Delete) get checkable
  "Drop or Pickup Marker" and, only while connected, "Drop or Pick Up Shared Marker". Checked
  means a marker is down, and choosing the item then picks it up, wherever you clicked. On a
  label, the marker drops on the label's tile.
- **Drawing.** `UpdateDropMarkerOverlay` (end of `Redraw`) draws the dot and a line from the
  player on `DropMarkersCanvas`. `DropDistanceText` sits bottom-right, left of the compass
  letter: "N tiles" in yellow for the local marker, "Shared: N tiles" in gold. Distance is UO
  range, `max(|dx|,|dy|)`.
- **Relay.** Relay messages `UOAMMD1` drop / `UOAMMP1` pickup (client→server) and `UOAMMS1`
  state (server→client: `SET|x|y|map|by|notify` or `NONE|by|notify`).
  - One marker per room, anyone in the room can drop or pick it up, memory only.
  - The server sends the state to the whole room **including the sender**, so the client never
    changes `_sharedMarker` itself, it only mirrors the server.
  - The state is also sent to newcomers and in every 3s sweep (notify=0), so a lost pickup
    datagram can't leave a stale marker.
  - Loopback-tested (15 checks incl. room isolation, re-drop by another player, late joiner,
    resync).
  - **Deployed 2026-09-25** from the second PC (key now in the project root there,
    `ssh-key-2026-09-22.key`). The previous binary is kept on the VM as
    `NewUOAM.RelayServer.prev` for rollback. Live test in a throwaway room: drop seen by the
    other client in 29ms, pickup cleared for both, room deleted afterwards.
- **In-game text.** On a live drop (notify=1) every client in the room shows one line
  **over the player's head**: `Shared Marker /^ North`. The direction is computed from that
  client's own position (`DirectionName`: North…NorthWest, UO north = −Y).
  - It uses the same UOAssist `DISPLAY_TEXT` (`WM_USER+207`) *without* the 0x10000
    system-message flag. Razor's `UOAssist.cs` maps that to `World.Player.OverheadMessage`
    (`UoAssistTextSender.DisplayOverheadText`, hue 0x59).
  - It is gated by the same "Zobrazovat chat mapy ve hře" checkbox.
  - **Repeats every `SharedMarkerRepeatSeconds` (10s)** while a marker is down, like old UOAM.
    The direction is recomputed each time. A late joiner's resync starts the repeat too. A new
    drop restarts the interval, and pickup/disconnect stops it.
  - **Arrow = `DirectionArrow`, pure ASCII, pointing where the direction lies on the client's
    screen** (the world is shown rotated 45°, so North is up-right): North `/^`, NorthEast `->`,
    East `\v`, SouthEast `V` (capital: a lone `v` got lost in the line, user's call), South `v/`, SouthWest `<-`, West `^\`, NorthWest `^`.
  - Tested live 2026-09-25: Unicode arrows (`↑↗…`, `⇑`, `▲►`, i.e. what ALT+24… types) turn
    into `?`/`^`/`>`, because Orion Assistant converts the atom to ANSI cp1250, which has no
    arrows. Raw CP437 bytes 0x18–0x1B render blank. The overhead font also blanks cp1250 chars
    above 127 (`« »` were empty overhead, though the journal showed them). Only ASCII works.
  - Verified live against Orion Assistant: all 8 lines render correctly overhead.

**Window placement memory (2026-09-28, user's request)**: the map's bounds, maximized flag and
map-only mode (`AppSettings.MainWindowBounds`/`MapOnlyMode`) and the chat's bounds
(`ChatWindowBounds`, recorded when the chat closes; the map's `Closing` closes the chat first)
are restored at the next start. `ScreenPlacement` (`EnumDisplayMonitors` work areas, converted
with `GetDpiForSystem` since the app is system-DPI-aware) checks a saved rect against today's
monitors: kept if all four corners are on some monitor, pulled fully onto the monitor it overlaps
most if partly off-screen, dropped (default placement) if it overlaps none, e.g. it was on a
second monitor that's disconnected now. A chat without a usable saved position opens next to the
map (right, below, left, above - `PlaceNextTo`), over it only if nothing fits. **In map-only
mode the chat opens already in compact mode**: the map is always on top then and used to hide a
freshly opened normal chat window. **Two crashes fixed the same day, both on switching compact
off:** changing `ResizeMode` to/from `CanResizeWithGrip` swaps the default Window template, and
`WindowChromeWorker` breaks if it sees that swap. (1) Changing the chrome while the new template
isn't applied yet → `GetChild(window, 0)` out of range. (2) A template change while a chrome is
set queues a deferred `_FixupTemplateIssues`, which throws `NullReferenceException` if the chrome
was removed before it runs. `ChatWindow.SetCompact` therefore removes the chrome *first* when
leaving compact mode and adds it *last* (after `ApplyTemplate()`) when entering it. Verified with
a scratch WPF harness that toggles compact six times with 500ms between steps (so deferred
dispatcher work runs) and catches `DispatcherUnhandledException`. Both crashes reproduced on the
old code, and it passes now. A harness that closes the window right after toggling misses (2).

**Elevation (2026-09-24, user's request)**: `App.OnStartup` relaunches the exe via
`Verb = "runas"` (UAC prompt) and exits when not running as admin, because B2 can't `OpenProcess`
an OrionUO that runs as admin. There is no `StartupUri`, and `App` creates `MainWindow` itself.
A declined prompt keeps the app running unelevated: `App.ElevationDeclined`, a status-bar
warning, and a "Spusť mapu jako správce" hint on a failed B2 Start. **`--no-elevate` skips the
relaunch. Always pass it when launching the app from automation/tests**: an elevated window
can't be driven by non-elevated UI Automation, and the prompt would pop up on the user's live
desktop. Caveat: if the Windows account isn't in Administrators, UAC runs the app as a
*different* user (the main PC's interactive-user/admin-account split). It then reads that user's
`%LocalAppData%\NewUOAM\settings.json`, so the saved settings seem to vanish.

**Auto-load/auto-start at launch**: if `AppSettings.ClientDirectory` is a valid existing folder,
the constructor wires `Loaded += async (_, _) => await AutoStartAsync()` (deferred to `Loaded`,
not called inline, because `Redraw()` needs `MapBorder.ActualWidth/Height` which is only valid
after the first layout pass). `AutoStartAsync` only calls `LoadMapAsync()` (the same body as
`LoadMapButton_Click`). It used to also auto-start the Varianta A UDP feed; the user asked on
2026-09-24 to drop that and hide Varianta A's UI entirely (commented out in `MainWindow.xaml`/
`.xaml.cs` like B1; `OrionUdpPositionProvider` itself is untouched). B2 is now the only position
source in the UI; since 2026-09-28 it tracks a running client automatically ("Automatic client
tracking"), and `AutoStartAsync` also loads the markers right after the map. "Are the map files current" needed no
separate check - `FacetColorMapDiskCache`'s existing mtime+size invalidation already handles it.
**Settings-wipe bug (found and fixed 2026-09-23; it had been wiping the user's real settings on
every launch):** `ShowMarkersCheckBox` has `IsChecked="True"` in XAML, and WPF fires its Checked
handler *during `InitializeComponent()`*. That handler calls `SaveSettings()`, which ran before any
text box was filled, so it overwrote `settings.json` with empty values. `AppSettings.Load()` then
read back the freshly emptied file. Symptoms: client path, display name, room password and markers
folder never survived a restart, and auto-load never fired. Confirmed with a temporary
`Environment.StackTrace` dump in `SaveSettings` (stack: `ShowMarkersCheckBox_Changed` ←
`InitializeComponent` ← ctor). Fixed by `_applyingLoadedSettings`, which **starts `true` as a
field initializer** (so it covers `InitializeComponent`) and is cleared at the end of the
constructor. `SaveSettings` is a no-op while it's set. Keep that invariant for any new
control/handler that saves settings. Verified: values survive a restart and the file isn't
touched during startup.

Gotcha hit while building this: `AppSettings.Load()`'s catch-all try/catch silently swallows a
malformed `settings.json` (e.g. an unescaped backslash - `"C:\Moria"` is invalid JSON, `"C:\\Moria"`
is required) and falls back to empty defaults, so a corrupted settings file makes auto-load
silently never fire with no error anywhere - worth checking first if auto-load seems to not be
working in some future session.

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
  client may show it differently; 0x005A is the nearest stock hue. Not yet checked in game. Player RGB colors aren't mapped to UO hues (would need `hues.mul`) - possible follow-up.
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

## Marker overlay (landmark icons, UOAM/Orion-compatible)

`MainWindow.xaml.cs`'s marker fields/methods (`_markers`, `_markerViews`, `LoadMarkersAsync`,
`BuildMarkerViews`, `GetMarkerIcon`/`LoadIconFile`, `UpdateMarkersOverlay`) wire
`NewUOAM.MapData.Markers.MarkerFileReader` (previously unused by the UI) into a real overlay,
reusing the exact same `.map`/`.csv` files and icon-name conventions old UOAM and OrionUO's own
World Map both already use - not a new format.

- **Icon assets**: `src/NewUOAM.App/Assets/MapIcons/*.png` - 199 files copied directly from a real
  OrionUO install's `OrionData/WorldMapExternalMarkers/MapIcons/` folder (the same icon set old
  UOAM itself used; Orion's World Map feature is explicitly UOAM-marker-file-compatible, confirmed
  by testing against this project's own real `DP Mesta.map`/`DP Dungy.map`, which parsed and
  rendered correctly with zero format changes). Content-included with `CopyToOutputDirectory` in
  the csproj (plain files next to the exe, loaded by path like everything else this app reads from
  disk - not compiled in as WPF pack resources). `NormalizeIconName` (upper-case, strip whitespace)
  maps a marker file's inconsistent spellings ("arms", "armourers guild") onto the icon files'
  naming ("ARMS.png", "ARMOURERSGUILD.png") - this exact scheme was verified against every icon
  name actually present in this project's real DP/Orion marker files, not designed in the abstract.
  An unmatched icon name falls back to `FallbackMarkerIconName` ("OTHER", which the bundled set
  does include) rather than rendering nothing.
- **Loading is folder-based, not single-file**: `LoadMarkersAsync` reads every `.map`/`.csv` file
  directly inside the chosen folder (not recursive) and merges their entries - deliberate, because
  this project's own real data has several sibling marker files at once (the DP-shard-specific
  ones plus Orion's stock Atlas/Common/Custom/Dungeons/Eodon/Labels/TerMur), matching how both old
  UOAM and Orion's own `WorldMapExternalMarkers` folder are actually laid out. Runs the file I/O on
  a background thread (`Task.Run`, with `Dispatcher.Invoke`/`BeginInvoke` back to the UI thread for
  the actual state mutation) even though realistic marker-file sizes are small enough this is
  barely needed - defensive against a large/slow folder, same reasoning as other async I/O in this
  app.
- **Static, not animated**: unlike remote players, markers never move in world space - `Image`
  elements are built ONCE per loaded entry (`BuildMarkerViews`, not incremental like
  `EnsureRemotePlayerView`) and just repositioned every `Redraw()` via `WorldToScreen` (the camera
  panning is what moves them on screen, not the markers themselves) - no animation loop needed.
- **Visibility is two-layered** (historical - since 2026-09-28 the second layer is the category
  checkbox in the side panel, with the file's `+`/`-` only as its default): the master
  `ShowMarkersCheckBox` toggles the whole layer, but even
  with it checked, `UpdateMarkersOverlay` still hides any individual marker whose own
  `MarkerEntry.Visible` is `false` (the file's own leading `+`/`-`) - real UOAM/Orion marker files
  use this per-entry (most of this project's own DP data defaults categories like "baker"/"arms" to
  hidden and only a few like "TELEPORTER"/"SCENIC" to shown), so respecting it is required for the
  overlay to look like old UOAM's own defaults instead of an undifferentiated icon dump. Also
  hidden: any marker whose `MapIndex` doesn't match `_currentFacetIndex` (this project's own DP
  marker data is a good real-world example of why this matters - it's almost entirely Trammel/
  `MapIndex 1`, only a handful of entries target Felucca).
- **Hover tooltip, not a permanent on-map label**: `MarkersCanvas` is deliberately NOT
  `IsHitTestVisible="False"` (unlike the other map overlays) specifically so each `Image` can
  respond to mouse hover. A permanently-visible text label per marker was considered and rejected -
  this project's own real marker data loads hundreds of entries from just a couple of files, and
  showing all of them as on-map text at once would be unreadable clutter; hover-to-reveal matches
  what was actually asked for ("při najetí myší na ikonu se zobrazí její název").
  **Not a native WPF `ToolTip`, though - a custom hover label** (`MarkerHoverLabel` in XAML,
  `ShowMarkerHoverLabel`/`HideMarkerHoverLabel`/`PositionMarkerHoverLabel` in code-behind), wired to
  each `Image`'s `MouseEnter`/`MouseLeave` instead of `Image.ToolTip`. This is a direct consequence
  of a real bug native ToolTips hit here - see the Dispatcher-starvation entry a few paragraphs
  down: `ToolTipService`'s popup relies on an internal delay-timer that can be indefinitely starved
  by continuous higher-priority work (the live position feed), so it could fail to show at all, not
  just late. Lowering `OnPositionChanged`'s priority fixed it in isolated testing, but the user
  reported it was still failing with a live feed running in real use - rather than keep chasing
  exactly which additional Dispatcher-priority source was still starving the native popup (a
  `CompositionTarget.Rendering`-driven redraw loop runs continuously for as long as position
  updates keep the pan animation retargeting, and isn't gated by an ordinary priority queue item
  the same way), switching to `MouseEnter`/`MouseLeave` sidesteps the whole class of bug: those are
  ordinary input events dispatched synchronously as part of hit-testing on each mouse-move, not a
  deferred timer competing for idle Dispatcher time. `_hoveredMarker`/`_hoveredMarkerIcon` track
  which marker (if any) is currently hovered so `UpdateMarkersOverlay` can keep the label glued to
  its icon if the camera moves while hovering, and hide it automatically if that marker becomes
  hidden mid-hover (facet change, toggle off).
- Verified with a real screenshot (via `PrintWindow`, not `CopyFromScreen` - see the note below on
  why that distinction mattered mid-testing), not just an automation-tree check: loaded this
  project's actual `DP Mesta.map`/`DP Dungy.map`, confirmed 434 markers parsed from 2 files,
  confirmed nothing rendered on Felucca (correct - the data barely covers it) and real icon
  glyphs rendered at correct positions over real town buildings on Trammel once the player's
  position was set there.
- **A real, genuinely subtle bug found via user report after the above: "I only see one marker,
  and hovering it shows no tooltip."** The "only one marker" half turned out to be expected (this
  project's real DP marker data is Trammel-heavy and sparse on Felucca - wherever the player
  happens to be, only whatever's genuinely nearby shows, same as any real map), confirmed by
  reproducing the user's exact report live: the character was in fact on Trammel near Ankh, the
  only DP marker anywhere close, in a small default-zoom viewport. The missing-tooltip half was
  real, and root-causing it took real effort because the obvious hypotheses were all wrong: not a
  hit-testing bug (`AutomationElement.FromPoint` at a marker's exact center returned that exact
  element, tooltip content and all - `ToolTip` was correctly wired the whole time, confirmed via
  UI Automation's `HelpText`, which WPF derives from a plain-string `ToolTip` automatically), not a
  z-order/overlap issue, not a test-coordinate-precision issue (eventually ruled out via
  `SendInput`-based real synthetic mouse movement plus a differential control: a plain static-XAML
  `Button`'s tooltip worked fine under the exact same automation method). The actual cause: **WPF's
  `ToolTipService` needs the UI thread's Dispatcher queue to actually go idle down to
  `DispatcherPriority.Input` (5) for its hover timer to fire, and `OnPositionChanged` was
  dispatching at `DispatcherPriority.Normal` (9) - strictly higher, and WPF's Dispatcher always
  fully drains every higher-priority item before touching a lower-priority one regardless of
  arrival order.** With the live Orion feed pushing a fresh position roughly every ~20ms, a new
  Normal-priority item was always queued again before the queue ever drained down to Input's
  level, so tooltips could never appear at all while the feed was running - not "delayed", never.
  Confirmed conclusively with a differential test: the same static button's tooltip worked
  perfectly with the UDP feed stopped and failed the exact same way once it was started. Fixed by
  dropping that one dispatch to `DispatcherPriority.Input` (see its own comment in
  `OnPositionChanged` for the full reasoning) - re-verified live afterward with the feed actively
  running: tooltip appeared correctly. This was already a bug before markers existed (anything
  Input-priority - drag-selection, hypothetically a future click handler - would have had the same
  problem), markers just made it visible for the first time since they were the first feature that
  actually needed sustained UI-thread idle time to work at all.
- **A real testing-infrastructure discovery, not a product bug** (found while chasing why a
  freshly-edited `settings.json` never seemed to take effect for automated tests): this machine has
  **two separate `%LocalAppData%\NewUOAM\settings.json` files** - `C:\Users\<admin>\...` and
  `C:\Users\<user>\...` - because processes launched via tool-driven PowerShell run under a
  different Windows identity (`<admin>`) than the user's own real interactive desktop login
  (`<user>`) - the same identity split hit earlier during this session's SSH key permission
  troubleshooting. The `<user>` file is the real one with the user's actual usage (their own
  chosen multiplayer display name, real client path) and must never be overwritten by test
  automation; `<admin>`'s is effectively a separate test profile. Net effect for any future
  session: don't assume settings-file edits or reads reflect what the actual user sees, and prefer
  driving the UI directly (`ValuePattern.SetValue`, clicking "Načíst mapu" explicitly) over relying
  on settings-driven auto-load when testing via automation - it sidesteps this identity split
  entirely and is more deterministic anyway. Also: `CopyFromScreen`-based screenshot capture grabs
  whatever is topmost on screen at those pixel coordinates, which can be something unrelated on the
  user's own live desktop (hit this directly - an unrelated folder-browser dialog ended up in a
  capture); `PrintWindow` (with `PW_RENDERFULLCONTENT`) captures a specific HWND's own content
  regardless of what else is on screen and should be preferred for any future screenshot-based
  verification of this app.

## Self-update (player package) - 2026-10-01

User's request: publish new versions for other players; the map checks at startup and can
download them, **without touching players' markers or settings**. The user wants the source
public (so players can see the map doesn't spy on them), so packages live on **GitHub Releases
of `Bengur2/new-uoam`**, not on the relay VM (the user worried downloads would slow the relay;
the VM never serves them). The step-by-step for releasing, the signing key and the settings
compatibility rules are in **`docs/RELEASE.md`** (Czech, user-facing). Read it before releasing
or before changing `AppSettings`.
- **Package.** `tools/Release/release.ps1` publishes `NewUOAM.App` self-contained win-x64 (user's
  pick: players install nothing; zip ~67 MB, 463 files, `SatelliteResourceLanguages=cs`, no pdb).
  `NewUOAM.UoaBridge` is replaced by a **single-file, trimmed, self-contained exe** (~10 MB). The
  bridge runs from its own copy in `%LocalAppData%` and players have no .NET, so it can't use the
  app's runtime. `UoAssistCommands.PrepareBridgeCopy` hashes the dll when there is one (dev build)
  or the exe (package). Verified: the packaged bridge runs with a bogus `DOTNET_ROOT` (started,
  "client exited" in `bridge.log`). `tools/NewUOAM.ReleaseTool pack` writes `NewUOAM.files` (every
  file of the package), zips the folder as `NewUOAM\…` and writes the signed `update.json`.
- **Version** = `<Version>` in `NewUOAM.App.csproj` (`App.CurrentVersion`, normalized to
  major.minor.build; shown as a disabled "Verze x.y.z" item in the Mapa menu and in the main
  window's title, "new UOAM x.y.z", plus " (vývoj)" for a build without `NewUOAM.files`). Tag `v<version>`.
- **Feed.** `UpdateFeed.DefaultFeedUrl` = `…/releases/latest/download/update.json`. GitHub
  redirects it to the newest non-draft, non-prerelease release; no API, no rate limit. It only
  works once the repo is public. `update.json` = `{manifest: base64(JSON), signature: base64}`,
  ECDSA P-256/SHA-256 over the exact manifest bytes. The manifest holds version, notes,
  package URL, SHA-256 and size. The app trusts only `UpdateFeed.PublicKey`. The private key is
  `update-signing.key` in the project root (gitignored via `*.key`; must be backed up like the SSH
  key). From the default feed, the package URL must start with `UpdateFeed.PackageUrlPrefix`.
  Download size and SHA-256 are checked against the signed manifest. Only a version greater than
  the installed one is offered, so an old signed feed can't downgrade anyone.
- **Install (`PackageInstaller.Apply`), no separate updater.** Windows allows *renaming* a running
  exe and its loaded DLLs. Tested on a throwaway self-contained WPF app: all 243 files, 48 of them
  loaded modules, renamed while it ran. So each listed file is renamed to `*.old-update` and the
  new one copied in. Files the old `NewUOAM.files` had and the new one doesn't are moved aside too.
  Any exception rolls back every step. A write probe first turns "no write access" (Program Files
  without admin) into a clear message. Only listed files are touched; settings, markers and caches
  are in `%LocalAppData%`/player folders, never in the app folder.
- **Flow.** `MainWindow.Updates.cs` `CheckForUpdatesAsync`: at startup in `Loaded` (after the
  map/markers load, before auto-connect; failures silent) and from Mapa > Zkontrolovat
  aktualizace (failures in a MessageBox). A build without `NewUOAM.files` (dev build from `bin\`)
  never checks. `UpdateWindow` (version, notes, "markery i nastavení zůstanou", progress,
  Aktualizovat / Později → Zrušit during the download) downloads to
  `%LocalAppData%\NewUOAM\updates\<version>-<pid>`, stages and applies, then sets
  `App.RestartExePath`. The map `Close()`s (the normal save-settings + relay bye path) and
  `App.OnExit` starts the new exe with the same args. If the app isn't elevated, `--no-elevate` is
  added so a declined UAC isn't asked again. The new instance deletes `*.old-update` on a
  background task with retries (0/3/10/30 s), because the old process is still exiting. Leftover
  download dirs older than 1 h are deleted too.
- **Installer + web (2026-10-01, user's request).** `release.ps1` also builds `NewUOAM-Setup.exe`
  from `installer/NewUOAM.iss` (Inno Setup 6, installed per-user via winget; Czech UI from its
  `Czech.isl`) out of the same folder as the zip, `NewUOAM.files` included. Per-user install to
  `%LocalAppData%\Programs\NewUOAM`, `PrivilegesRequired=lowest` (the self-update must be able to
  write there), Start-menu shortcut + optional desktop one, `CloseApplications`, post-install launch
  via `shellexec` (so the app's own UAC relaunch works). **Uninstall deletes only the package's
  files**: the `[Code]` section reads `{app}\NewUOAM.files` (kept current by self-updates, so it
  also covers files a later version added) and deletes each listed file plus its `.old-update`.
  The 1.0.2 installer as first built wiped the whole `{app}` folder (`[UninstallDelete]
  filesandordirs`). The user asked whether uninstall could delete markers, and a player keeping
  marker files in the app folder would have lost them, so it was changed the same day. Never
  `%LocalAppData%\NewUOAM`. The asset name is version-less, so
  `releases/latest/download/NewUOAM-Setup.exe` is a stable link.
  Verified: silent install into a scratch dir (465 files, Start-menu entry, Apps entry "new UOAM
  1.0.1"), then silent uninstall (shortcut and Apps entry gone, settings untouched). For the
  package-files-only uninstall: planted `DP Mesta.map`, `moje markery\stara uoam.map` and
  `Assets\MapIcons\MOJE.png` survived. All 465 package files, two `.old-update` backups and a file
  "added by an update" (appended to `NewUOAM.files`) were removed. App icon `src/NewUOAM.App/Assets/app.ico` (`ApplicationIcon`; also
  `docs/img/icon.png`) is drawn by `tools/Release/make-icon.ps1`. Main window title shows the version.
  The web is `docs/index.html` (single file, Czech: features, install, first run, controls,
  in-game commands, privacy, FAQ), served by GitHub Pages from `main` `/docs` (`docs/.nojekyll`,
  so files are served as-is) at https://bengur2.github.io/new-uoam/. No Actions workflow: the gh
  token has no `workflow` scope, and GitHub refused a push containing one. The download button links to
  the stable installer URL, and JS shows version/date/size from the GitHub API (works without JS).
  `docs/img/map.png` is rendered from the DP client data with the app's Rotated45 math + DP
  marker icons, no player data. Checked in headless Edge at 1280px and in a 390px iframe (headless
  Edge's own minimum window width makes a direct 390px screenshot misleading).
- **Testing:** `--update-feed <url>` (App arg) overrides the feed (then any package URL is
  allowed, the signature is still required). `release.ps1 -PackageBaseUrl http://localhost:27999/`
  builds a package for it.
- **Verified 2026-10-01:**
  - Scratch unit test (21 checks): valid/foreign-key/tampered/garbage/wrong-product feeds, the
    embedded key rejecting a test-signed feed, version compare, rollback leaving the folder
    byte-identical when a file is locked, replace/add/remove-dropped, a player's own file kept,
    held backups surviving cleanup, update with a held stale backup, `..` in the list rejected.
  - E2E with the real packages (scratch install of 1.0.0, 1.0.1 built with a temporary version
    bump, served by a PowerShell `HttpListener` on localhost, UI Automation clicking Aktualizovat).
    The dialog showed (screenshot), the 1.0.0 process exited and 1.0.1 started from the same
    folder. No `*.old-update` was left after 15 s, the file list matched 1.0.1, `mine.txt` stayed,
    `settings.json` was unchanged, and the restarted 1.0.1 fetched the feed again without offering
    anything.
  - v1.0.1 released on GitHub 2026-10-01: the live `latest/download/update.json` verified (signature
    OK, zip matches). **Not yet:** the user seeing the dialog in their installed 1.0.0, and the update of
    an *elevated* instance (the E2E used `--no-elevate`).

## Watch list (the user asked to keep an eye on these and re-test them over time)

- **B2 across Orion builds** (added 2026-09-24): after any OrionUO update, or on another PC,
  check B2's status line right after Start. "OrionUO x.y.z" means a known build.
  "neznámá verze…" followed by "Adresa postavy nalezena automaticky: …+0x…" means the heuristic
  worked; add that hash and offset to `OrionPlayerPointerLocator.KnownBuilds` and confirm the
  position matches the game. If it keeps saying the address can't be found while the character
  is logged in, the object layout changed and a MemoryScanner session is needed. Also watch for a
  wrong pick: a position that is near the player but lags or doesn't follow them exactly (the
  `+0x515A90` decoy type).

## Known follow-up work

`README.md`'s "Co chybí" list is the user-facing version of this. Currently open:
- **B1**: paused on the game hop's "login encryption" layer - implementing that cipher is new
  work; don't resume without the user explicitly asking. Facet changes at runtime aren't tracked
  either (would need `0xBF`/`0x08`).
- **B2**: facet hardcoded to 0 (fine for DP); character name from the window title can
  transiently show another OrionUO window's title.
- **Relay**: admin password is a plain systemd `ExecStart` argument (accepted risk).
- **Names are unique per room** (2026-09-25). This fixes the old "same name collides"
  limitation, which the user actually hit: two map windows on one PC share settings, so both
  were "Test1". The server then treated them as one session with a flapping endpoint, and the
  shared marker reached the second window only via the 3s resync, with no overhead text.
  - `RelayServer.TouchSessionAsync` refuses a name that another endpoint is actively using in
    the same room. It drops the message and replies `UOAMRN1`, at most once a second per
    endpoint. Other rooms are unaffected.
  - The access query now carries the name when it's known, and the reply can be `NAME_TAKEN`.
    The client then gets `RelayConnectResult.NameTaken`, or the `NameTaken` event if the name
    only arrives later (character name from B2).
  - The app shows error **107** and disconnects.
  - Known edge: after a crash without a bye, the same name is blocked for up to 15s (session
    timeout).
  - Loopback-tested (8 checks) and live-tested; deployed 2026-09-25.
- **Not live-tested by clicking yet** (ask the user to confirm):
  - sharing markers from the app's right-click menus (UIA can't open context menus);
  - the "no client running at all" state (0,0 on the map and on the relay);
  - the color picker's effect on another player's map;
  - the error-103 dialog;
  - the chat join/leave lines.
- **Test tooling notes** (for UI Automation runs):
  - The marker list is virtualized: scroll it (`ScrollPattern`) before looking for rows.
  - Owned dialogs appear as descendants of the main window, not top-level windows.
  - PowerShell 5.1 needs test scripts saved as UTF-8 **with BOM** (or "…" and diacritics break).
  - Back up and restore `%LocalAppData%\NewUOAM\settings.json` around any test that types into
    settings fields.
  - Point the markers folder at a scratch dir when a test saves markers.
