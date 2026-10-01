namespace NewUOAM.MapData.Markers;

public sealed record MarkerEntry(
    int X,
    int Y,
    int MapIndex,
    string Name,
    string? IconName = null,
    string? Color = null,
    bool Visible = true,
    int ZoomIndex = 3);
