// ── Themes/Classic/Structure/ClassicOptions.cs — Classic's structure options, read where a view draws with them (themes plan 6.1, M4, M2) ──
//
// A structure option is read by the structure's own code (WVTH005). Most Classic views are templates with a `ViewContext`, but the picture that needs
// "are track covers hidden" is often a static builder several calls below one (a queue row, a search hit, a Recents row), run inside a render or a bound
// content region: a tracked read of the live option cell subscribes whichever computation is running, so the row re-draws when the option flips and
// nothing has to carry the value down the call chain. The core's own decisions (lane sets, chart cache keys) read the same value as `ThemePolicies.Lists`.

using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>Reads of Classic's structure options.</summary>
public static class ClassicOptions
{
    /// <summary>Track artwork is hidden (the `hideTrackArt` option). A tracked read: call it inside a render or a binding.</summary>
    public static bool HideTrackArt() => ThemeHost.Options.Get(ClassicStructure.HideTrackArt.Key);

    /// <summary>The track rows' skin (the `rowStyle` option). A tracked read: call it inside a render or a binding.</summary>
    public static TrackRowStyle RowStyle() => ThemeHost.Options.Get(ClassicStructure.RowStyle.Key);

    /// <summary>The Classic row skin is on (the three-lane hairline table, the 0.2.9 look). A tracked read.</summary>
    public static bool ClassicRows() => RowStyle() == TrackRowStyle.Classic;

    /// <summary>The page accent follows the artwork (the `accentFromArtwork` option, CT9). A tracked read.</summary>
    public static bool AccentFromArtwork() => ThemeHost.Options.Get(ClassicStructure.AccentFromArtwork.Key);

    /// <summary>The now-playing surfaces take the playing cover's colour (the `nowPlayingColors` option, CT9). A tracked read.</summary>
    public static bool NowPlayingColors() => ThemeHost.Options.Get(ClassicStructure.NowPlayingColors.Key);
}
