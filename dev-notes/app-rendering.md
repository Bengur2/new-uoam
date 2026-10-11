# NewUOAM.App: rendering, zoom, layout, map views

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

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


**Map views (2026-10-01, user's request):** Mapa > Normální pohled / X-ray pohled and Zobrazit /
Skrýt statiky, like old UOAM's Normal view / X-ray view / Show / Hide statics. Default: Normal with
statics. With statics hidden the map is land only, so Normal/X-ray are disabled (as in old UOAM).
- **The rules come from old UOAM's own output, not its docs.** Old UOAM saves its renders per view
  (`MAP0-1.BMP` normal, `-X1` X-ray, `-NS1` no statics; 8bpp, 1 px/tile; `-2/-4/-8` are zoomed out).
  `C:\Games\Endor\UO Auto-Map` has full-size ones next to the client files they were made from
  (`C:\Games\Endor\Client\Muls`). Each pixel was matched to the land or one of the tile's statics
  by radar color, and candidate rules were scored:
  - Normal: the topmost static (highest Z, the **later** one in statics.mul on a tie) if its
    `Z >= land Z`, else the land - 99.997%. The old code (topmost, always, first on a tie) scored
    87%: buried statics showed through, which is why the map looked X-ray-ish.
  - X-ray: the topmost static with `Z <= land Z`, else the land (surface buildings and trees
    vanish, cellars and caves show) - 99.64%.
  - Hidden: land only - 100% (the pixel depends on the land tile id alone).
  - tiledata.mul item height plays no part (every `Z + height` variant scored worse).
  - Moria's renders give the same Normal rule at 97.8% (its BMPs are probably older than its map
    files).
  - The app's own `FacetColorMap.TileColor` against the Endor BMPs: Normal 100%, X-ray 99.9%,
    Hidden 100% of the unambiguous tiles.
- Code: `StaticsView` + `MapFacet.TryGetDrawnStatic` (MapData), `MainWindow.MapView.cs` (menu,
  `AppSettings.ShowStatics`/`XRayView`). Color maps are kept per (facet, view). A switch drops the
  other view's maps from memory (~112 MiB per big facet); `EnsureCache` rebuilds because
  `_cacheStaticsView` differs; `PreloadColorMapsAsync` loads or builds the new view, the facet on
  screen first. A newer preload cancels an older one (`FacetColorMap.Build` checks the token every
  64 rows). Until a facet's map is ready, `RenderRegion` decodes the same view on demand. The first
  switch to a view costs a build (several seconds for the DP facet); after that it's a disk-cache
  load.
- **Switching speed (user's report: 2-3 s per switch, old UOAM instant).** Measured on DP: a
  view from the disk cache loads in ~50 ms, but drawing the cache region decoded on demand took
  ~0.8 s at 2 px/tile and more zoomed out, and that's what the first version did (it dropped the
  old view and redrew at once). Now `SetStaticsView` keeps drawing the old view
  (`_drawnStaticsView`) until `TryLoadCachedColorMap` has the facet on screen in memory, then
  redraws; only a view not on disk yet is drawn on demand while it builds. And like old UOAM
  (which renders every view at load), `PrebuildOtherViewsAsync` quietly builds the other views
  of every facet into the disk cache after the load; views already there cost a header check
  (`FacetColorMapDiskCache.IsCurrent`). Building one big facet's view takes ~16 s. Measured via
  UI Automation on Moria: the screen changes 130-230 ms after the click (including the test's
  own screenshot polling).
- **Memory:** a released view's arrays sit on the large object heap, which a normal GC doesn't
  compact: 401 MB before switching grew to 2.2 GB after six switches. `ReturnFreedColorMaps`
  (a compacting GC after a switch's preload and after the prebuild) keeps it at 720 MB.
- **Second comparison, against what the user actually compared** (`C:\temp\Moria` and old
  UOAM's `MAP25166902-1/-X2/-NS2.BMP` there, every tile): color within the palette's
  quantization on 99.71% (Normal), 99.67% (X-ray), 99.99% (no statics). The rest is mostly a few
  areas where the map data changed after old UOAM made its BMPs: around 3680,2660 a whole town is
  laid out differently (25% of pixels differ there, ~0.3% around 2367,2734). **Old UOAM doesn't
  notice such changes** - it keeps showing its saved BMP (named by the map file's size), while
  this app rebuilds when a source file's mtime or size changes. A copy with only
  `map0LegacyMUL.uop` gave identical results, so it isn't a .mul/.uop difference. The half-size
  X2/NS2 images sample the top-left tile of each 2x2.
- Verified in the real app via UI Automation: the menu switches, the disabled state with statics
  hidden, the saved settings and the visible change on screen.

