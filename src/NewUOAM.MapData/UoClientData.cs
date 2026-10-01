using NewUOAM.MapData.Colors;
using NewUOAM.MapData.Maps;

namespace NewUOAM.MapData;

/// <summary>Top-level handle onto one UO client install: opens/caches facets and shared tables on demand.</summary>
public sealed class UoClientData : IDisposable
{
    public string ClientDirectory { get; }

    private readonly Dictionary<int, MapFacet> _facets = new();
    // Keyed by (facet, view). Built on pool threads and read on the UI thread, hence the lock.
    private readonly Dictionary<(int Facet, StaticsView View), FacetColorMap> _colorMaps = new();
    private readonly object _colorMapsLock = new();
    private RadarColorTable? _radarColors;
    private string? _radarColorsPath;

    /// <summary>Safety cap on how far up facet indices are probed when discovering/preloading
    /// "all facets" - discovery actually stops earlier, at the first couple of consecutive misses.</summary>
    public const int MaxProbedFacetIndex = 15;

    public UoClientData(string clientDirectory)
    {
        if (!Directory.Exists(clientDirectory))
            throw new DirectoryNotFoundException($"Client directory not found: {clientDirectory}");
        ClientDirectory = clientDirectory;
    }

    public MapFacet GetFacet(int index)
    {
        // Locked: color maps are built/loaded on pool threads while the UI thread opens facets too.
        lock (_facets)
        {
            if (_facets.TryGetValue(index, out var facet)) return facet;
            facet = MapFacet.Open(ClientDirectory, index);
            _facets[index] = facet;
            return facet;
        }
    }

    public bool TryGetFacet(int index, out MapFacet facet)
    {
        try
        {
            facet = GetFacet(index);
            return true;
        }
        catch (FileNotFoundException)
        {
            facet = null!;
            return false;
        }
    }

    /// <summary>Facet indices actually present in this client folder (probes 0.. and stops after
    /// two consecutive misses, capped by <see cref="MaxProbedFacetIndex"/>).</summary>
    public IReadOnlyList<int> DiscoverFacetIndices()
    {
        var found = new List<int>();
        int misses = 0;
        for (int i = 0; i <= MaxProbedFacetIndex && misses < 2; i++)
        {
            if (TryGetFacet(i, out _)) { found.Add(i); misses = 0; }
            else misses++;
        }
        return found;
    }

    public bool TryGetColorMap(int facetIndex, StaticsView view, out FacetColorMap map)
    {
        lock (_colorMapsLock) return _colorMaps.TryGetValue((facetIndex, view), out map!);
    }

    /// <summary>Drops every built color map except those of <paramref name="keep"/> - one view of
    /// a big facet is ~112 MiB, so switching views shouldn't keep the old one in memory. The disk
    /// cache keeps it, so switching back is a ~50 ms load per facet.</summary>
    public void ReleaseColorMapsExcept(StaticsView keep)
    {
        lock (_colorMapsLock)
        {
            foreach (var key in _colorMaps.Keys.Where(k => k.View != keep).ToList())
                _colorMaps.Remove(key);
        }
    }

    /// <summary>Builds (or returns the already-built, or loads a still-valid on-disk cached) color
    /// map for one facet in one view. Building from scratch is expensive (decodes every tile) -
    /// callers on a UI thread should offload this to a background thread, e.g. via
    /// <see cref="PreloadAllColorMapsAsync"/>.</summary>
    public FacetColorMap GetOrBuildColorMap(int facetIndex, StaticsView view, CancellationToken ct = default)
    {
        if (TryGetColorMap(facetIndex, view, out var existing)) return existing;

        var facet = GetFacet(facetIndex);
        var radar = RadarColors;
        var sourceFiles = SourceFilesFor(facet);

        if (!FacetColorMapDiskCache.TryLoad(ClientDirectory, facetIndex, view, sourceFiles, out var map) || map is null)
        {
            map = FacetColorMap.Build(facet, radar, view, ct);
            FacetColorMapDiskCache.Save(ClientDirectory, facetIndex, sourceFiles, map);
        }

        lock (_colorMapsLock) _colorMaps[(facetIndex, view)] = map;
        return map;
    }

    /// <summary>Loads a still-valid disk-cached color map into memory; false when there is none.
    /// Never builds - for a view switch, which should show the cached view (~50 ms) rather than
    /// start a build of several seconds on the spot.</summary>
    public bool TryLoadCachedColorMap(int facetIndex, StaticsView view)
    {
        if (TryGetColorMap(facetIndex, view, out _)) return true;
        var facet = GetFacet(facetIndex);
        _ = RadarColors; // sets _radarColorsPath, part of the cache key
        if (!FacetColorMapDiskCache.TryLoad(ClientDirectory, facetIndex, view, SourceFilesFor(facet), out var map) || map is null)
            return false;
        lock (_colorMapsLock) _colorMaps[(facetIndex, view)] = map;
        return true;
    }

    /// <summary>Builds the given views of every facet into the disk cache without keeping them in
    /// memory, so a later switch to them is a quick load - old UOAM likewise renders every view up
    /// front. Views already cached are skipped (header check only). <paramref name="firstFacet"/>
    /// goes first.</summary>
    public async Task PrebuildColorMapsOnDiskAsync(IReadOnlyList<StaticsView> views, int firstFacet, CancellationToken ct)
    {
        var indices = DiscoverFacetIndices().OrderBy(i => i == firstFacet ? 0 : 1).ThenBy(i => i).ToList();
        foreach (int facetIndex in indices)
        {
            foreach (var view in views)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Run(() =>
                {
                    if (TryGetColorMap(facetIndex, view, out _)) return; // in memory = already saved
                    var facet = GetFacet(facetIndex);
                    var radar = RadarColors;
                    var sourceFiles = SourceFilesFor(facet);
                    if (FacetColorMapDiskCache.IsCurrent(ClientDirectory, facetIndex, view, sourceFiles)) return;
                    FacetColorMapDiskCache.Save(ClientDirectory, facetIndex, sourceFiles, FacetColorMap.Build(facet, radar, view, ct));
                }, ct);
            }
        }
    }

    /// <summary>The files a facet's color map is derived from (the disk cache's validity key).
    /// Read <see cref="RadarColors"/> first, it sets the radarcol.mul path.</summary>
    private IReadOnlyList<string> SourceFilesFor(MapFacet facet) => _radarColorsPath is null
        ? facet.SourceFilePaths
        : new List<string>(facet.SourceFilePaths) { _radarColorsPath };

    /// <summary>Precomputes the color map for every facet this client folder has, in one view, so
    /// the app can pan/zoom/rotate by sampling instead of re-decoding raw tiles on every repaint.
    /// <paramref name="firstFacet"/> (the one on screen) goes first. Reports progress as
    /// "facetIndex/totalCount" strings; safe to call from a background thread (each facet's decode
    /// work only touches that facet's own data).</summary>
    public async Task PreloadAllColorMapsAsync(StaticsView view, int firstFacet = 0,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var indices = DiscoverFacetIndices().OrderBy(i => i == firstFacet ? 0 : 1).ThenBy(i => i).ToList();
        for (int n = 0; n < indices.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            int facetIndex = indices[n];
            progress?.Report($"Předpočítávám mapu facetu {facetIndex} ({n + 1}/{indices.Count})…");
            await Task.Run(() => GetOrBuildColorMap(facetIndex, view, ct), ct);
        }
    }

    public RadarColorTable? RadarColors
    {
        get
        {
            if (_radarColors is not null) return _radarColors;
            string? path = FindFile("radarcol.mul");
            if (path is null) return null;
            _radarColorsPath = path;
            return _radarColors = RadarColorTable.Load(path);
        }
    }

    private string? FindFile(string fileName)
    {
        string direct = Path.Combine(ClientDirectory, fileName);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(ClientDirectory)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        lock (_facets)
        {
            foreach (var facet in _facets.Values) facet.Dispose();
            _facets.Clear();
        }
        lock (_colorMapsLock) _colorMaps.Clear();
    }
}
