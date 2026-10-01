# Map file format notes (empirically validated)

These were confirmed against real client files (`C:\Moria`, which conveniently ships both the
raw `.mul` and the `.uop` variant of every facet, letting us diff a candidate parser's output
against known-good bytes). See `tools/UopProbe` for the scratch programs used to derive these.

## Legacy `mapN.mul` block layout

- Flat array of fixed 196-byte block records, `blockIndex = blockX * heightInBlocks + blockY`
  (column-major over the block grid: X is the outer loop).
- Each block: 4-byte header (not always zero - do not assume it's unused/ignorable) followed by
  64 land cells, **row-major** (`cellIndex = cellY * 8 + cellX`), each cell 3 bytes
  (`ushort tileId` LE, `sbyte z`).
- Confirmed by comparing terrain-elevation "roughness" (sum of abs Z differences between
  adjacent tiles) between row-major and column-major decodings over 8 sample regions of
  Felucca - row-major was ~1.7x smoother everywhere, column-major produces visibly scrambled
  terrain.

## `mapNLegacyMUL.uop` container

Generic Mythic ".uop" package: 28-byte header (`magic 0x0050594D`, `version`, `formatTimestamp`,
`int64 nextBlockOffset`, `uint32 blockCapacity`, `uint32 fileCount`) followed by a chain of
block tables (`int32 entryCount`, `int64 nextBlockOffset`, then `entryCount` x 34-byte entries:
`int64 offset, int32 headerLength, int32 compressedSize, int32 decompressedSize, uint64 hash,
int32 crc, int16 compressionMethod`). `compressionMethod`: 0 = stored, 1 = zlib.

Entries are keyed by a 64-bit hash of a logical file name (the format has no directory table).
For map data the name is:

```
build/map{facetIndex}legacymul/{groupIndex:D8}.dat
```

hashed with Jenkins "lookup3" `hashlittle2` (public domain), **word order `(b << 32) | c`**
(the mixing itself is the textbook lookup3 algorithm; the *return* word order was the one thing
that had to be found empirically - `(c << 32) | b`, which is what several blog writeups quote,
did **not** match; `(b << 32) | c` gave 96/96 hits against known-good map0 entries).

Each entry (a "group") normally covers 4096 consecutive blocks
(`groupIndex * 4096 .. groupIndex * 4096 + 4095`), i.e. `802816` decompressed bytes
(`4096 * 196`). **Do not assume physical file/offset order equals logical group order** - it
happens to for some facets but not others (map1/Trammel has a lone 1-block patch entry
physically sandwiched in the middle of the file; map2/Ilshenar's 15 entries are written to disk
in a scrambled physical order entirely). Always resolve `groupIndex -> entry` via the name-hash.

Reconstruction was verified byte-for-byte identical to the raw `.mul` for facets 0, 1, 3, 4, 5.
Facet 2 (Ilshenar) is identical except for a single 4-byte block-header mismatch in block 0
(one 196-byte block out of 57,600) - an apparent quirk in how that one historical patch was
originally packaged, not a bug in this reader. Negligible for map rendering purposes.

## Known facet dimensions (blocks, i.e. tiles/8)

Derived from measured file sizes, cross-checked against well-known public dimensions:

| Facet | Name     | Width (blocks) | Height (blocks) | Width (tiles) | Height (tiles) |
|------:|----------|----------------:|-----------------:|---------------:|-----------------:|
| 0     | Felucca  | 768             | 512               | 6144            | 4096              |
| 1     | Trammel  | 896             | 512               | 7168            | 4096              |
| 2     | Ilshenar | 288             | 200               | 2304            | 1600              |
| 3     | Malas    | 320             | 256               | 2560            | 2048              |
| 4     | Tokuno   | 181             | 181               | 1448            | 1448              |
| 5     | TerMur   | 160             | 512               | 1280            | 4096              |

A shard can legitimately resize a facet (most commonly extending its height); `MapFacet.Open`
trusts the *measured* block count from the file over this table and only falls back to a
square-ish guess when the measured count doesn't divide evenly by the known width.

## Statics (`staticsN.mul` / `staidxN.mul` / `staticsNLegacyMUL.uop`) - not yet implemented

Land-only rendering is enough for a first working map view; statics (buildings, trees, etc.)
are a separate index+data pair per block and are the next piece of `NewUOAM.MapData` to add.
