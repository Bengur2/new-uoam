# NewUOAM.MapData

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

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
  no `.uop` variant needed yet. Wired into `MapFacet` (`GetStaticsAt`/`TryGetDrawnStatic`, its own
  `LruCache<long, IReadOnlyList<StaticItem>>` alongside the land-block cache) and into
  `FacetColorMap.Build`/`TileColor` (also `RenderRegion`'s fallback path): at most one static per
  tile is drawn instead of the land tile, matching how the in-game radar map only ever shows one
  thing per tile. **Which one depends on the view** (`StaticsView`, see "Map views"; until
  2026-10-01 it was simply the topmost one, always). Its radar color comes from `radarcol.mul` at index `tileId + 0x4000` (the
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
  `%LocalAppData%\NewUOAM\colormap-cache\<sha1(clientDir)>\facet{N}-{view}.bin` (one per `StaticsView`,
  format version 2; a version-1 `facet{N}.bin` is deleted on the next save) (`UoClientData.GetOrBuildColorMap`
  tries this before falling back to `FacetColorMap.Build`) so the several-seconds-per-facet decode
  doesn't have to happen on every single app launch - the user explicitly asked for exactly this
  ("aby mapa zůstala načtená i po vypnutí programu"). Invalidation is automatic and exact: the
  cache file records `(LastWriteTimeUtc.Ticks, Length)` for every one of `MapFacet.SourceFilePaths`
  plus `radarcol.mul` at save time, and a load is rejected (falls through to a real rebuild) if any
  of those don't match bit-for-bit on load - re-patching the client or hand-editing a `.mul`
  invalidates automatically, nothing has to remember to bust the cache manually. Verified against
  real Moria data in `tools/UopProbe`: cache load is ~0.03s vs ~6.6s to build (200x), byte-identical
  output, and touching `map0.mul`'s mtime does force a real (slow, correct) rebuild.

