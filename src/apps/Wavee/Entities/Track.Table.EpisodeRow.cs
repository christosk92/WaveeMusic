// ── Entities/Track.Table.EpisodeRow.cs ────────────────────────────────────────────────────────────────────────────
// The EPISODE row Track.Table's `TableSlot` mounts for an episode-kind display row — a playlist mixing episode
// members, or the Your Episodes listen-later playlist (`User.SavedEpisodes.cs`). A new NAMED partial member of
// `Track` (this file is a new partial declaration of that struct, alongside `TableSlot`/`TableRowContent` in
// Track.Table.cs).
//
// Role: UI
//
// An episode row inside a playlist is not a re-invented grid: it IS the show reader's own row,
// `Episode.ReaderRow` (Entities/Episode.UI.cs), unchanged — same art, title + badges, two-line description, date /
// duration-left / progress meta, now-playing treatment. `TableSlot` hands it straight through, with no `Skin`
// wrapper: `ReaderRow` is fully self-contained (click, hover, the check lane, the "…" menu) once its
// `Episode.RowContext` carries a `Selection` seam, which is exactly what `EpisodeRowContent` below builds from the
// table host's own tone/play/overlay/menu/multi-select state.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public readonly partial struct Track
{
    // `TableHost` (Track.Table.cs, "══ 3. THE HOST ══") is a PRIVATE nested class of `Track` — this component is
    // therefore a nested member of `Track` too (a new partial declaration of the SAME struct, same as
    // `TableSlot`/`TableRowContent`), not a top-level type, so it can reference `TableHost` at all.

    /// <summary>The episode row's Component wrapper: binds <see cref="Episode.RowItem"/> through the host's own
    /// episode projection (<c>TableHost.BindEpisodeItemFor</c>) and renders <see cref="Episode.ReaderRow"/> directly
    /// — the table's <c>Skin</c> never wraps an episode row (see <c>TableSlot.Render</c>).</summary>
    sealed class EpisodeRowContent(TableHost host, RowScope scope, IReadSignal<Episode.RowItem> item, int start) : Component
    {
        readonly TableHost _host = host;
        readonly RowScope _scope = scope;
        readonly IReadSignal<Episode.RowItem> _item = item;
        readonly int _start = start;

        // Resolves the CURRENT row at click time (`Peek`) — the host, the scope's index signal and `start` are mount-
        // stable for the slot's life, matching `TableRowContent.PlayCurrent`'s idiom.
        void PlayCurrent(Episode _) => _host.PlayRow(_scope.Index.Peek() - _start);

        public override Element Render()
        {
            var shape = _host.ShapeValue;
            bool narrow = shape.Density < 2;
            var bound = new BoundItemScope<Episode.RowItem>(_scope, _item);
            var ctx = new Episode.RowContext
            {
                Tone = _host.AccentRead,
                Play = PlayCurrent,
                Overlay = _host.OverlayService,
                Menu = _host.EpisodeMenuFor,
                Selection = _host.EpisodeSelection,
            };
            return Episode.ReaderRow(in bound, ctx, narrow);
        }
    }
}
