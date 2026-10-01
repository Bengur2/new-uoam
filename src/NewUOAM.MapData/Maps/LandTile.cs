namespace NewUOAM.MapData.Maps;

public readonly record struct LandTile(ushort TileId, sbyte Z)
{
    public bool IsEmpty => TileId == 0 && Z == 0;
}
