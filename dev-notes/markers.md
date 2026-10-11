# Markers: overlay, side panel, own categories, sharing

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

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

**Own marker categories (2026-10-02, user's request: e.g. a "Treasures" category for the
TREASURE_LEVEL1..8 icons, and moving one marker to another category).** Code:
`MainWindow.MarkerCategories.cs`, `MarkerCategoryStore.cs`, `TextPromptWindow`.
- **Stored in `NewUOAM kategorie.json` in the markers folder** (user's pick over settings.json: it
  travels with the marker files). The .map/.csv formats have no category field and old UOAM and
  Orion read them, so they're never changed. The file holds categories (name + normalized icon
  types) and per-marker assignments (marker file name, X, Y, map, name -> category name).
- **A marker's category:** its assignment, else your category holding its icon type, else its
  icon type as before. Keys: icon type "SHRINE", yours "C:" + name in capitals, "S:" in front for
  shared_markers.map. An assignment may also name an icon type's category ("Dungeon").
- **UI:** a marker's menu (map icon or panel row) has "Kategorie ▸" (by icon / yours / icon
  types / new). An icon type's category row has "Zařadit X do kategorie ▸". Your category's row has
  rename, delete (confirm) and "Ikony v kategorii ▸" (click = take the icon out). The Edit Label
  dialog has a Category field (editable; empty = by icon, a new name creates the category).
- **Bookkeeping:** editing a marker (also renaming or moving it to another file) carries its
  assignment; deleting it drops it; moving from shared_markers.map carries it. A category left
  with no icon types and no assignments is removed (it would have no panel row to delete it from).
  A category that gets markers takes their previous show/hide state (`KeepVisibility`), so
  regrouping never makes markers appear or vanish. A file that can't be read is logged and never
  overwritten. Menu headers with names use a TextBlock ("TREASURE_LEVEL1" would lose its "_" as an
  access key).
- **Shared markers don't carry categories** (user's pick): a received one falls into place by its
  icon. Sharing a category would need a protocol change and a relay deploy.
- **Verified:** context menus can't be opened by UI Automation, and posted right-clicks are dropped
  by WPF while the real cursor is elsewhere (the user's mouse wasn't touched). So a scratch host
  (`new App()` without Run, the real `MainWindow` shown off screen, markers from a scratch
  folder) raised the menu items' Click events and answered the dialogs in code: 20/20 checks
  (icon into a new category keeps hidden state, second icon, one marker by hand, Edit Label
  shows/clears/creates a category, rename of a marker carries it, category rename, duplicate name
  refused, survives a reload, icon taken out, category deleted, marker deleted drops its
  assignment, marker file untouched). The look of the menus wasn't checked on screen.

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

