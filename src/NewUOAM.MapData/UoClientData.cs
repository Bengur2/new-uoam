using NewUOAM.MapData.Colors;
using NewUOAM.MapData.Maps;

namespace NewUOAM.MapData;

/// <summary>Top-level handle onto one UO client install: opens/caches facets and shared tables on demand.</summary>
public sealed class UoClientData : IDisposable
{
    public string ClientDirectory { get; }

    private readonly Dictionary<int, MapFacet> _facets = new();
    private readonly Dictionary<int, FacetColorMap> _colorMaps = new();
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
        if (_facets.TryGetValue(index, out var facet)) return facet;
        facet = MapFacet.Open(ClientDirectory, index);
        _facets[index] = facet;
        return facet;
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

    public bool TryGetColorMap(int facetIndex, out FacetColorMap map) =>
        _colorMaps.TryGetValue(facetIndex, out map!);

    /// <summary>Builds (or returns the already-built, or loads a still-valid on-disk cached) color
    /// map for one facet. Building from scratch is expensive (decodes every tile) - callers on a
    /// UI thread should offload this to a background thread, e.g. via
    /// <see cref="PreloadAllColorMapsAsync"/>.</summary>
    public FacetColorMap GetOrBuildColorMap(int facetIndex)
    {
        if (_colorMaps.TryGetValue(facetIndex, out var existing)) return existing;

        var facet = GetFacet(facetIndex);
        var radar = RadarColors;
        var sourceFiles = _radarColorsPath is null
            ? facet.SourceFilePaths
            : new List<string>(facet.SourceFilePaths) { _radarColorsPath };

        if (FacetColorMapDiskCache.TryLoad(ClientDirectory, facetIndex, sourceFiles, out var cached) && cached is not null)
        {
            _colorMaps[facetIndex] = cached;
            return cached;
        }

        var map = FacetColorMap.Build(facet, radar);
        _colorMaps[facetIndex] = map;
        FacetColorMapDiskCache.Save(ClientDirectory, facetIndex, sourceFiles, map);
        return map;
    }

    /// <summary>Precomputes the color map for every facet this client folder has, so the app can
    /// pan/zoom/rotate by sampling instead of re-decoding raw tiles on every repaint. Reports
    /// progress as "facetIndex/totalCount" strings; safe to call from a background thread (each
    /// facet's decode work only touches that facet's own data).</summary>
    public async Task PreloadAllColorMapsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var indices = DiscoverFacetIndices();
        for (int n = 0; n < indices.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            int facetIndex = indices[n];
            progress?.Report($"Předpočítávám mapu facetu {facetIndex} ({n + 1}/{indices.Count})…");
            await Task.Run(() => GetOrBuildColorMap(facetIndex), ct);
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
        foreach (var facet in _facets.Values) facet.Dispose();
        _facets.Clear();
        _colorMaps.Clear();
    }
}
