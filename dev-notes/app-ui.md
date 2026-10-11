# NewUOAM.App: menus, dialogs, context menu, startup

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

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


**UI language (2026-10-01, user's request):** Mapa > Jazyk > Čeština / Slovenčina / English
(`AppSettings.Language`, default cs, applied in `App.OnStartup` before the first window).
- **Only controls are translated**: menus, dialogs, buttons, labels, tooltips, the marker panel,
  marker context menus, and the fixed texts of the track/update/admin/create-room/chat windows.
  Status-bar lines, error dialogs (101-107...), chat and system lines and everything shown in the
  game stay Czech (user's call: "jen ovládání mapy, ne hlášky"). Old UOAM's own English names
  (Track Player, New Label..., Go to location..., Edit Label, Drop or Pickup Marker, Edit/Delete)
  stay English in every language.
- `UiStrings.All` is the one table (key, cs, sk, en); Czech is the original wording. `Loc.Apply`
  writes every key into `Application.Resources` and XAML uses `{DynamicResource Key}`, so open
  windows switch live. Code uses `Loc.T`/`Loc.F`/`Loc.Plural` (Czech/Slovak plural forms
  `_1`/`_2`/`_5`), or `SetResourceReference` for a text that is one whole key. Texts that code
  keeps on screen are refreshed from `Loc.Changed` (`MainWindow.ApplyLocalizedTexts`,
  `TrackMapWindow.UpdateHeader`).
- **Adding a control text:** add a row to `UiStrings.All` (all three languages) and reference its
  key. A duplicate key throws at startup. A scratch check verified that every used key exists,
  every key is used, and every row has the same `{n}` placeholders in all three languages.
- Verified via UI Automation: cs -> en -> sk live (menu bar, title "(dev)", both settings
  dialogs' titles), the setting survives a restart, then back to cs. Screenshots: the English
  settings dialog, the Slovak online dialog, the English marker panel.


**Right-click menu, Track Player, New Label (2026-09-24)**: `MapBorder_MouseRightButtonUp` builds
a `ContextMenu` modeled on old UOAM's. Only two of its items exist so far, as the user asked:
- **Track Player**: `_trackPlayer`, default on, not persisted. Off means `OnPositionChanged` stops
  calling `BeginPanAnimation`, the self marker is drawn via `WorldToScreen` instead of dead
  center, and a left-drag pans the map (`_dragStart` + mouse capture). The drag sets
  `_viewX/_viewY` directly and keeps `_centerX/_centerY` = the rounded view, because `EnsureCache`
  always centers the cache on `_centerX/_centerY`. With tracking off, a drag in map-only mode pans
  instead of moving the window. Turning tracking back on pans back to the player (snaps if far).
- **World** (2026-10-11, user's request): submenu of the facets found in the client folder
  (`DiscoverFacetIndices`, names from `MapFacet.Name`), the shown one checked. `SetWorld` switches
  the view (map + markers, view clamped to the new facet) and calls
  `IClientPositionProvider.OverrideFacet` on the tracked client, so the player marker and the
  position sent to the relay move to that facet too. It means "my character is on this facet",
  not a browse-only view: every position update sets the facet anyway, so a view-only switch
  would snap back on the next step. B3 drops the override on Razor's next MAP_INFO/LOGIN; B2
  keeps it while it runs. Provider side tested (override → immediate re-emit, back, remembered
  for a new provider); the menu click itself not yet (UIA/posted messages can't open WPF context
  menus) - ask the user.
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

