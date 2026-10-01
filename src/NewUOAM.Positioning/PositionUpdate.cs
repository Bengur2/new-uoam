namespace NewUOAM.Positioning;

/// <summary>A single player-position sample, regardless of which provider produced it.</summary>
public sealed record PositionUpdate(
    int X,
    int Y,
    int Z,
    int Map,
    string? CharacterName,
    DateTimeOffset Timestamp)
{
    public static PositionUpdate Now(int x, int y, int z, int map, string? name = null) =>
        new(x, y, z, map, name, DateTimeOffset.UtcNow);
}
