// ── Themes/Classic/Structure/Views/Detail/ClassicDetailKit.Satellites.cs — the rail satellites of the show and episode pages (themes plan 6.1, T9c) ──
//
// The T9a frame left its page-facing builders in `Detail` as the seam for the per-kind pages. The ones ONLY the show and episode pages use moved
// here with T9c (the rail satellites); the pieces every detail view shares are in `ClassicDetailKit.cs` (the other half of this partial).
//
// A view may not name `Playback`, `Entities` or `Store` (WVTH002): the satellites take their verbs and facts as arguments.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee.Themes.Classic;

internal static partial class ClassicDetailKit
{
    // ══ T9c: THE RAIL SATELLITES (show, episode) ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A page's SATELLITE icon action (the <see cref="Detail.FrameSlots.Satellites"/> building block): the standard
    /// <see cref="Controls.IconAction"/> at <see cref="Detail.RailLayout.SatelliteSize"/>, named for its tooltip. A null
    /// <paramref name="onClick"/> draws it DISABLED (plan D-10: a write with no captured endpoint ships visible, with
    /// its reason as the name). Key it at the call site.
    /// <para>Workstream B: was a round 36/40 FAB; the satellite ladder is now the standard 32/r4 icon button, so it reads as an
    /// ordinary rail action rather than media chrome.</para></summary>
    public static Element RailSatellite(string glyph, string name, Action? onClick)
    {
        BoxEl fab = Controls.IconAction(glyph, onClick, size: Detail.RailLayout.SatelliteSize);
        return Controls.Named(onClick is null ? fab with { IsEnabled = false, Cursor = null } : fab, name);
    }

    /// <summary>The heart as a satellite: <c>Controls.SaveButton</c> at <see cref="Detail.RailLayout.SatelliteSize"/>, inked by the
    /// page's ambient accent (the frame provides it), keyed on the uri (props freeze at mount).</summary>
    public static Element RailSatelliteSave(string uri, string name)
        => Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = name, Glyph = 16f, Box = Detail.RailLayout.SatelliteSize })
            with { Key = "save:" + uri };

    /// <summary>The ⋯ as a satellite: the frame's lazily-built More menu at <see cref="Detail.RailLayout.SatelliteSize"/>.</summary>
    public static Element RailSatelliteMore(Func<ContextMenuModel?> menu)
        => Detail.MoreButton(menu, Detail.RailLayout.SatelliteSize) with { Key = "rail:more" };
}
