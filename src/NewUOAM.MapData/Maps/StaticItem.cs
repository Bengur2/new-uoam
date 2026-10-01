namespace NewUOAM.MapData.Maps;

/// <summary>One static item (building piece, tree, fence, ...) placed at a fixed position.</summary>
public readonly record struct StaticItem(ushort TileId, byte X, byte Y, sbyte Z, short Hue);
