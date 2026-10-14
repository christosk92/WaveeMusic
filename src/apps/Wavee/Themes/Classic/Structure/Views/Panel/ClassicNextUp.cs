// ── Themes/Classic/Structure/Views/Panel/ClassicNextUp.cs — Classic's `Panel.NextUp` (themes plan 6.1, P3) ──────────────────────
//
// From `Shell/Rail.UI.cs` (`NextUpHost`, `NextUpRow`): the "Next up" rows the now-playing body and the video body place. Each row is the
// shared media surface in its row shape (`Shape.Row` over the core's `Track.RowData`: the row plate, the hand, the play FAB over the art,
// a track drag and the right-click menu), at the 52-DIP floor. The rows' data, their play and click verbs and their keys (the queue item
// and the artwork flag, so a settings flip REMOUNTS rather than re-binds) are the core's (`Rail.NextUpRows`).
//
// The template reads the rows inside its render, so the queue edge, the track table and the artwork setting subscribe THIS view (as they
// subscribed the core's own component): a table edge re-renders these rows and not the panel that holds them.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's "Next up" rows.</summary>
internal static class ClassicNextUp
{
    /// <summary>The registered template of <see cref="ViewKeys.PanelNextUp"/>.</summary>
    public static Element Template(NextUpProps props, in ViewContext ctx)
    {
        var rows = props.Read();
        var els = new Element[rows.Length];
        for (int k = 0; k < rows.Length; k++)
        {
            var row = rows[k];
            var shape = Shape.Row(TrackRowRules.ArtEdge(Design.Size.ArtThumb, row.Art)) with { MinHeight = ClassicHeroLayout.NextUpRowH };
            els[k] = Controls.Surface(row.Data, shape) with { Key = row.Key };
        }
        return new BoxEl { Direction = 1, Gap = ClassicHeroLayout.NextUpGap, Children = els };
    }
}
