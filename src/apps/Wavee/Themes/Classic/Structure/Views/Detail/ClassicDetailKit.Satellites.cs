// ── Themes/Classic/Structure/Views/Detail/ClassicDetailKit.Satellites.cs — the save and more satellites (themes plan 6.1, T9c) ──────────
//
// The heart and the ⋯ at the rail's satellite size: the library's show pane draws them beside its primary
// (`ClassicLibraryShowPane.cs`); the pieces every detail view shares are in `ClassicDetailKit.cs` (the other half of this partial).
//
// A view may not name `Playback`, `Entities` or `Store` (WVTH002): the satellites take their verbs and facts as arguments.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee.Themes.Classic;

internal static partial class ClassicDetailKit
{
    // ══ T9c: THE SATELLITES (the library's show pane) ══════════════════════════════════════════════════════════════════════

    /// <summary>The heart as a satellite: <c>Controls.SaveButton</c> at <see cref="Detail.RailLayout.SatelliteSize"/>, inked by the
    /// page's ambient accent (the frame provides it), keyed on the uri (props freeze at mount).</summary>
    public static Element RailSatelliteSave(string uri, string name, float box = Detail.RailLayout.SatelliteSize)
        => Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = name, Glyph = 16f, Box = box })
            with { Key = "save:" + uri };

    /// <summary>The ⋯ as a satellite: the frame's lazily-built More menu at <see cref="Detail.RailLayout.SatelliteSize"/>.</summary>
    public static Element RailSatelliteMore(Func<ContextMenuModel?> menu, float size = Detail.RailLayout.SatelliteSize)
        => MoreButton(menu, size) with { Key = "rail:more" };
}
