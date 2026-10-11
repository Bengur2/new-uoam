# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

A low-latency successor to UO Auto-Map for Ultima Online (C#/.NET 8, WPF), used by this user on
the Dark Paradise shard (Sphere/Source-X, Felucca only) with the OrionUO client. **On a new
machine, read `HANDOVER.md` first** (Czech handover: B2 on another PC, client-build fingerprints,
repo/release setup).

**This file is only the index.** The full notes (design decisions, bugs found, how each feature
was verified) live in `dev-notes/`. **Before changing an area, read its dev-notes file.** When
you finish a feature, document it in the matching dev-notes file and only update the status
table here. Keep this file short: `CLAUDE.md` has a ~150k character limit, and it passed it
once (2026-10-11, 162k), which is why it was split.

## Project status and where the details are

| Area | State | Notes file → section |
|---|---|---|
| Map reading (`.mul`/`.uop` land + statics, radar colors), color maps + disk cache, LRU cache | done, validated byte-for-byte | `mapdata.md` |
| Rendering (45°/north-up, even zoom steps, 3x render cache, pan animation, DPI-exact), zoom range, UI layout | done | `app-rendering.md` |
| Map views like old UOAM (Normal / X-ray, Show / Hide statics) | done 2026-10-01, matches old UOAM's BMPs 100% / 99.9% / 100% | `app-rendering.md` → "Map views" |
| Variant A - OrionUO script → loopback UDP | done; **UI hidden** since 2026-09-24, no auto-start | `positioning.md` |
| Variant B1 - packet proxy | **paused** (game hop is encrypted), UI commented out | `positioning.md` |
| Variant B2 - ReadProcessMemory of `OrionUO64.exe` (facet hardcoded 0, known builds by hash + auto-locate), MemoryScanner | done, live | `positioning.md` |
| Automatic client tracking (Klient menu, no Start/Stop) | done 2026-09-28 | `positioning.md` → "Automatic client tracking" |
| Variant B3 - UOAssist API (Razor in ClassicUO clients, e.g. Endor Revived): position polling + facet from `MAP_INFO`; World menu (manual facet); in-game hues checked against the client's `hues.mul` | done 2026-10-11, live facet change + World click pending | `positioning.md` → "B3" |
| Menu bar (Mapa/Online/Klient dialogs), UI language cs/sk/en, right-click menu (Track Player, New Label, Edit/Delete, Go to location), own marker color + edge arrow, Drop/Shared Marker | done | `app-ui.md` |
| Window placement memory, elevation (`--no-elevate`), auto-load, settings-wipe bug | done | `app-ui.md` |
| Marker overlay (UOAM `.map`/`.csv`, 199 icons, hover label) | done | `markers.md` → "Marker overlay" |
| Side panel = marker browser; own marker categories (`NewUOAM kategorie.json`) | done (categories 2026-10-02, v1.0.5) | `markers.md` |
| Sharing markers with the room (hidden by default at receivers, `shared_markers.map`) | done, server deployed 2026-09-29 | `markers.md` → "Sharing markers with the room" |
| Multiplayer relay (UDP, positions, glide, edge arrows, display name) | done, deployed | `relay.md` |
| Rooms (admin), player-created rooms, admin v2, 90-day expiry | done, deployed 2026-10-01 | `relay.md` |
| Deployed relay instance (Oracle VM `89.168.122.175:27980`, systemd, deploy steps) | live | `relay.md` → "Deployed relay instance" |
| Room chat, event-driven presence, player colors, error codes 101-107, auto-connect, chat history + unread counter | done, deployed | `relay-chat-presence.md` |
| Map chat inside the UO client (UOAssist API via Orion Assistant) | done, verified live | `ingame.md` |
| In-game commands `-c`, `-c name>text`, `-panic`, `-unpanic`, `-who` (UoaBridge) | done, deployed 2026-09-25 | `ingame.md` |
| Track reports `-t` + `TrackPlayers.oajs` + track map window | done 2026-09-30; script's first live run pending | `ingame.md` → "Track reports (-t)" |
| Player package + signed self-update, installer, website | done 2026-10-01; latest release **v1.0.6** | `self-update.md` + `docs/RELEASE.md` |
| Public repo `Bengur2/new-uoam` (one clean commit; old history in private `new-uoam-private`), commits as `168770972+Bengur2@users.noreply.github.com` | since 2026-10-01 | `HANDOVER.md` 6/6b |

Other docs: `docs/UOP_FORMAT_NOTES.md` (UOP format, cell order, facet sizes), `docs/RELEASE.md`
(releasing, signing key, settings compatibility). `docs/` is the public GitHub Pages site, so
developer notes don't go there.

## Commands

```bash
dotnet build NewUOAM.slnx -c Debug              # build everything
dotnet run --project src/NewUOAM.App            # run the WPF viewer
dotnet run --project src/NewUOAM.RelayServer    # run the multiplayer relay server (`dev-notes/relay.md`)
dotnet run --project tools/UopProbe             # scratch/validation tool
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
depends on both). `NewUOAM.MemoryScanner` is a standalone console tool for Variant B2 (`dev-notes/positioning.md`).
`NewUOAM.UoaBridge` is a tiny dependency-free helper exe for in-game commands (see `dev-notes/ingame.md`, "In-game
commands"); `NewUOAM.App` references it only to get its files copied next to the app.
`NewUOAM.RelayServer` is a standalone console app for the multiplayer relay (`dev-notes/relay.md`) - it only
depends on `NewUOAM.Positioning` (for `PositionUpdate` and the shared `Relay/RelayProtocol.cs`
wire format), not on `NewUOAM.App` or `NewUOAM.MapData`.
`NewUOAM.Updates` (no dependencies, no WPF) holds the self-update: the signed feed format, download
and the file swap. `NewUOAM.App` and `tools/NewUOAM.ReleaseTool` (publisher side) both use it.


## Rules that break things when forgotten

Short form; the reasoning is in the named notes file.
- **Map data** (`mapdata.md`): resolve UOP entries via `UopFile.TryGetEntry`, never by physical
  order. Don't trust a facet index's stock width (`ResolveDimensions`). Cell order is row-major,
  don't change without re-checking. `Util/LruCache.cs` is the only LRU cache, don't hand-roll one.
- **Rendering** (`app-rendering.md`): render at physical pixels (real DPI). `_pixelsPerTile` stays
  an even integer (or `2/D` when zoomed out, cache center snapped to multiples of `D`). The cache is
  always generated around the integer `_centerX/_centerY`, never the fractional `_viewX/_viewY`.
  Keep `MaxAnimatedDistanceTiles` and the `Array.Copy` clamp in `Redraw()`.
- **Threading** (`app-rendering.md`, `markers.md`): `OnPositionChanged` uses `Dispatcher.BeginInvoke`
  at `DispatcherPriority.Input`, never `Invoke` or Normal priority, and repaints go through
  `RequestRedraw()`. Marker hover uses `MouseEnter`/`MouseLeave`, not WPF ToolTips.
- **Settings** (`app-ui.md`): `SaveSettings` is a no-op while `_applyingLoadedSettings` is set (it
  starts `true` so it covers `InitializeComponent`); keep that for any new control that saves.
  Handlers fired during `InitializeComponent` must tolerate null controls. A malformed
  `settings.json` silently loads defaults. Settings compatibility rules: `docs/RELEASE.md`.
- **Dialog panels** (`app-ui.md`): no `ElementName` bindings inside panels hosted by
  `HostedPanelWindow`.
- **Static init order**: static fields initialize in declaration order (the `PlayerColors` startup
  crash). After any `NewUOAM.App` change, actually launch the exe; a green build isn't enough. When
  it won't start, read the Windows Application event log (`.NET Runtime` / `Application Error`).
- **Relay** (`relay.md`): wire format lives only in `Relay/RelayProtocol.cs`. **Deploy the server
  before releasing an app that uses new messages** (old servers drop them or behave differently).
  `dotnet publish -o publish/relay` with a **forward slash** through the Bash tool. Upload as
  `.new`, `mv` over, restart; keep the previous binary as `.prev`. Never write player IPs to disk.
- **In-game bridge** (`ingame.md`): never kill a running `NewUOAM.UoaBridge` while its client runs
  (OA has no unregister; commands stay dead until the client restarts). Changes to the bridge or
  the command list only reach clients started afterwards. In-game text must be ASCII
  (`ToGameSafeText`).
- **Client integration**: client features must work in Variant B through the UOAssist API, no
  Orion script and no injection, unless the user explicitly asks (as for `TrackPlayers.oajs`).
  B2 stays strictly read-only (OpenProcess + ReadProcessMemory). Hash the *running* client's
  module, not a guessed install path.
- **Testing** (all notes files): launch the app with `--no-elevate` from automation. Don't move the
  user's real mouse or send keys to the foreground window (UIA patterns, `PrintWindow` screenshots,
  posted messages). Back up and restore `%LocalAppData%\NewUOAM\settings.json`; point markers at a
  scratch folder. Two Windows identities on the main PC can mean two different settings files.
  Live relay tests use a throwaway room that is deleted afterwards.
- **Self-update** (`self-update.md`): `update-signing.key` (project root, gitignored) signs the
  feed; never commit it. Only listed package files are ever touched on update or uninstall.

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
