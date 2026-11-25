// ── Entities/Playlist.Dj.cs ───────────────────────────────────────────────────────────────────────────────────────────
// The AI DJ list's page body (plan-dj §2.8). The DJ list has no rows of its own (the service lists none): its page is the theme's own playlist
// head (the name, the cover, what the DJ says on its card, Play) over this body in place of "Nothing here yet" (the table's EmptyBody seam).
//
//   not started   one line: press play and the DJ picks the songs, a few at a time
//   playing       what the DJ plays next, by segment, as the queue shows it (Shell.QueueModel.WithSegments): each segment under its label, the one
//                 "Let DJ pick" lands on marked, the rows the theme's own media rows
//
// The two are keyed siblings that cross-fade, and the body's height eases between them (Design.Reveal.Resize): no jump between "not started" and
// "playing". A launch-restored DJ deck whose session is not resolved yet reads as not started (Playback.DjNow is inactive until the session is).
//
// Role: SHELL (a component: it reads the queue, the cursor and the DJ facts)

using FluentGpu.Controls;
using FluentGpu.Hooks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

/// <summary>The DJ list's page body (<see cref="Body"/>).</summary>
public static class DjPage
{
    /// <summary>The body of the DJ list <paramref name="list"/>'s page.</summary>
    public static Element Body(EntityUri list) => Embed.Comp(new Props(list.Id), static () => new BodyView());

    sealed record Props(EntityId List);

    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

    sealed class BodyView : Component
    {
        public override Element Render()
        {
            var p = UseProps<Props>();
            Playback.Dj.Facts dj = Playback.DjNow.Value;
            bool playing = dj.Active && !dj.Remote && Playback.ContextUri.Value.Equals(p.List);   // a DJ another device plays: its rows are not here
            _ = Entities.Current.Edges.Queue.Changed.Value;   // the rows the DJ queued and the cursor moving through them
            _ = Playback.Current.Value;
            return new BoxEl
            {
                Key = "dj-page", Direction = 1, MinWidth = 0f, MinHeight = DjPageRules.MinHeight, ClipToBounds = true, Animate = Design.Reveal.Resize,
                Children = [playing ? Upcoming(p.List) : NotStarted()],
            };
        }

        static Element NotStarted() => new BoxEl
        {
            Key = "dj-page:idle", Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinHeight = DjPageRules.MinHeight,
            Padding = new Edges4(Spacing.L, Spacing.XXL, Spacing.L, Spacing.XXL), Enter = s_fade, Exit = s_fade,
            Children = [Ui.Body(Loc.Get(Strings.Player.DjNotStarted)).Secondary() with { MaxLines = 3, Wrap = TextWrap.Wrap, MinWidth = 0f }],
        };

        static Element Upcoming(EntityId list)
        {
            var state = Playback.Snap();
            var model = Shell.QueueModel.Project(Queue.Rows, static i => Queue.RefAt(i), state.Cursor.Index, false, false, false, RepeatMode.Off,
                Playback.Current.Peek(), null);
            model = Shell.QueueModel.WithSegments(model, Playback.Dj.NextJumpTarget(Queue.Rows, state.Cursor.Index));
            var groups = new List<Element>(model.Segments.Length);
            int start = model.Counts.Queue;
            foreach (QueueSegment seg in model.Segments)
            {
                var kids = new List<Element>(seg.Count + 1) { Header(seg) };
                for (int k = 0; k < seg.Count; k++)
                {
                    int at = start + seg.Start + k;
                    if (at >= model.Rows.Length) break;
                    kids.Add(Row(model.Rows[at], list));
                }
                groups.Add(new BoxEl
                {
                    Key = "dj-page:" + seg.Key, Direction = 1, MinWidth = 0f, Layout = LayoutTransition.Slide, Enter = s_fade, Exit = s_fade,
                    Children = kids.ToArray(),
                });
            }
            return new BoxEl
            {
                // No top air of its own: the head's list gap (or the list bar's top air) above it is the theme's section gap.
                Key = "dj-page:upcoming", Direction = 1, MinWidth = 0f, Gap = Spacing.L, Padding = new Edges4(0f, 0f, 0f, Spacing.L),
                Enter = s_fade, Exit = s_fade, Children = groups.ToArray(),
            };
        }

        /// <summary>A segment's label in the theme's module type, and "Let DJ pick starts here" in the accent where the button lands.</summary>
        static Element Header(QueueSegment seg)
        {
            string mark = QueueProps.SegmentMark(seg);
            var title = R.Type.ModuleHeader(QueueProps.SegmentTitle(seg)) with
            {
                Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            };
            return new BoxEl
            {
                // Inset by the row's own padding (SurfaceGeometry.RowPad), so the heading starts where the rows' art does.
                Key = "dj-page:hdr", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                Padding = new Edges4(SurfaceGeometry.RowPad, 0f, SurfaceGeometry.RowPad, Spacing.S),
                Children = mark.Length > 0 ? [title, Ui.Caption(mark) with { Color = Tok.AccentTextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f }] : [title],
            };
        }

        /// <summary>One upcoming row as the theme's media row: a click plays it (the cursor moves to it inside the session).</summary>
        static Element Row(QueueRowProps row, EntityId list)
        {
            EntityRef r = row.Ref;
            int index = row.Index;
            // A click's own path (the queue panel's): the rows passed become history, the user's queued run moves behind it, the deck lands there.
            Action play = () =>
            {
                if (!Queue.SkipTo(index, Playback.Snap().Cursor, out QueueCursor cursor)) return;
                Entities.Publish();
                Playback.PlayNow(Queue.RefAt(cursor), list, cursor);
            };
            var data = r.Kind == EntityKind.Track && r.Slot > Table.None
                ? Track.RowData(new Track(r.Slot).ForDisplay, new Track.RowDataOptions(OnClick: play, OnPlay: play, Draggable: false, QueueItemId: row.ItemId))
                : Controls.CardData.Seed;
            return new BoxEl
            {
                Key = "dj-page:i" + row.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture), Direction = 1, MinWidth = 0f,
                Layout = LayoutTransition.Slide, Enter = s_fade, Exit = s_fade,
                Children = [Controls.Surface(data, Shape.Row())],
            };
        }
    }
}

/// <summary>The DJ page body's one number: the height "not started" holds (the table's empty item's own 160), so the page does not jump when the
/// first rows arrive.</summary>
public static class DjPageRules
{
    public const float MinHeight = 160f;
}
