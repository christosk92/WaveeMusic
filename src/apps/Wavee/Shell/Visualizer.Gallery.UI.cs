// ── Shell/Visualizer.Gallery.UI.cs ─────────────────────────────────────────────────────────────────────────────────
// The visualizer gallery's words: the face names, blurbs, tips and group labels
//
// Role: UI (the words; the pane, its tiles and the settings rows are the structure's `Stage.Gallery` view, T7a1)
// Owner: K
// Wave: 7
// Spec: viz-app-plan §4.1-§4.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The gallery pane (grouped sections of tiles with live or frozen previews, keyboard navigation, the settings rows) is drawn by
// the active structure: Classic's is `Themes\Classic\Structure\Views\Stage\ClassicStageGallery.cs` (+ `ClassicStageGalleryTiles.cs`),
// numbers in `Rules\StageGeometry.Gallery.cs`. What stays here is vocabulary every structure shares: `NameKey` (also the options
// popover's), `SubKey`, `TipKey` and `GroupKey`. The tile subtitle rule (`SubtitleOf`) and the grid's pure keyboard rule
// (`GalleryNav`) are in `Stage.Rules.cs`.

namespace Wavee;

public static partial class Visualizer
{
    // ── the words ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A shown face's name key (a legacy kind names its successor).</summary>
    public static string NameKey(Kind k) => Catalog.Successor(k) switch
    {
        Kind.Verse => Strings.Stage.Viz.Verse, Kind.Bars => Strings.Stage.Viz.Bars, Kind.Ring => Strings.Stage.Viz.Ring,
        Kind.Orbit => Strings.Stage.Viz.Orbit, Kind.Aurora => Strings.Stage.Viz.Aurora, Kind.Timeline => Strings.Stage.Viz.Timeline,
        Kind.Classic => Strings.Stage.Viz.Classic, Kind.Warp => Strings.Stage.Viz.Warp, Kind.Tunnel => Strings.Stage.Viz.Tunnel,
        Kind.Ambience => Strings.Stage.Viz.Ambience, Kind.Kaleido => Strings.Stage.Viz.Kaleido, Kind.Scope => Strings.Stage.Viz.Scope,
        Kind.Drift => Strings.Stage.Viz.Drift, Kind.Type => Strings.Stage.Viz.Type, Kind.Mosaic => Strings.Stage.Viz.Mosaic,
        Kind.Spotlight => Strings.Stage.Viz.Spotlight, Kind.Magneto => Strings.Stage.Viz.Magneto, Kind.Flow => Strings.Stage.Viz.Flow,
        _ => Strings.Stage.Viz.Bloom,
    };

    internal static string SubKey(Kind k) => Catalog.Successor(k) switch
    {
        Kind.Verse => Strings.Stage.VizSub.Verse, Kind.Bars => Strings.Stage.VizSub.Bars, Kind.Ring => Strings.Stage.VizSub.Ring,
        Kind.Orbit => Strings.Stage.VizSub.Orbit, Kind.Aurora => Strings.Stage.VizSub.Aurora, Kind.Timeline => Strings.Stage.VizSub.Timeline,
        Kind.Classic => Strings.Stage.VizSub.Classic, Kind.Warp => Strings.Stage.VizSub.Warp, Kind.Tunnel => Strings.Stage.VizSub.Tunnel,
        Kind.Ambience => Strings.Stage.VizSub.Ambience, Kind.Kaleido => Strings.Stage.VizSub.Kaleido, Kind.Scope => Strings.Stage.VizSub.Scope,
        Kind.Drift => Strings.Stage.VizSub.Drift, Kind.Type => Strings.Stage.VizSub.Type, Kind.Mosaic => Strings.Stage.VizSub.Mosaic,
        Kind.Spotlight => Strings.Stage.VizSub.Spotlight, Kind.Magneto => Strings.Stage.VizSub.Magneto, Kind.Flow => Strings.Stage.VizSub.Flow,
        _ => Strings.Stage.VizSub.Bloom,
    };

    internal static string TipKey(Kind k) => Catalog.Successor(k) switch
    {
        Kind.Verse => Strings.Stage.VizTip.Verse, Kind.Bars => Strings.Stage.VizTip.Bars, Kind.Ring => Strings.Stage.VizTip.Ring,
        Kind.Orbit => Strings.Stage.VizTip.Orbit, Kind.Aurora => Strings.Stage.VizTip.Aurora, Kind.Timeline => Strings.Stage.VizTip.Timeline,
        Kind.Classic => Strings.Stage.VizTip.Classic, Kind.Warp => Strings.Stage.VizTip.Warp, Kind.Tunnel => Strings.Stage.VizTip.Tunnel,
        Kind.Ambience => Strings.Stage.VizTip.Ambience, Kind.Kaleido => Strings.Stage.VizTip.Kaleido, Kind.Scope => Strings.Stage.VizTip.Scope,
        Kind.Drift => Strings.Stage.VizTip.Drift, Kind.Type => Strings.Stage.VizTip.Type, Kind.Mosaic => Strings.Stage.VizTip.Mosaic,
        Kind.Spotlight => Strings.Stage.VizTip.Spotlight, Kind.Magneto => Strings.Stage.VizTip.Magneto, Kind.Flow => Strings.Stage.VizTip.Flow,
        _ => Strings.Stage.VizTip.Bloom,
    };

    internal static string GroupKey(Group g) => g switch
    {
        Group.Lyrics => Strings.Stage.VizGroup.Lyrics, Group.Classics => Strings.Stage.VizGroup.Classics,
        Group.Zune => Strings.Stage.VizGroup.Zune, Group.ITunes => Strings.Stage.VizGroup.Itunes, _ => Strings.Stage.VizGroup.Fluent,
    };

}
