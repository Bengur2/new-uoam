namespace NewUOAM.Positioning;

/// <summary>A position provider tied to one running UO client process (B2 memory reading, B3
/// UOAssist API). The app uses the process id to send in-game text and commands to the same
/// client it tracks, which matters when several clients run (multiboxing).</summary>
public interface IClientPositionProvider : IPositionProvider
{
    int ProcessId { get; }

    /// <summary>The facet set by hand (the map's World menu): reported with every position from
    /// now on, until the client itself reports a facet change (B3: Razor's MAP_INFO / LOGIN). B2
    /// has no facet source at all, so it keeps the manual one for as long as it runs.</summary>
    void OverrideFacet(int facet);
}
