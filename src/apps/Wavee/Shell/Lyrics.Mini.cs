// ── Shell/Lyrics.Mini.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the mini player's lyrics face (#197): the ONE store document, a line clock, and the template's drawing
//
// Role: CORE HOST (it builds no element of its own: the mini player's template draws every line through its theme)
//
// The mini player's Lyrics face is a reading window, not the full view: the strip's ticker (the current line and the next, rolling up)
// and a short scrolling column elsewhere, each theme marking the active line its own way. Like the now-playing panel's reel (`PeekReel`)
// it plans no fetch of its own beyond `Store.Ensure`, reads the one document, and keeps its active line on the document's own timeline:
// one timer armed for the next line's start (at the playback rate), re-armed on a play, pause, seek or position report, and only while
// the face shows (the mini player is open), so a hidden or paused face wakes nothing and a playing one wakes once per line, not every
// 100 ms (mini player power pass, 2026-11-09). The active line is a signal: the drawer re-renders once per line.

using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Lyrics
{
    /// <summary>The mini player's lyrics face: the playing track's document as lines and an active index, drawn by <paramref name="draw"/>
    /// (re-pushed: the drawing follows the template's hover, accent and shape), ticking while <paramref name="visible"/> reads true.</summary>
    public static Element Mini(Func<MiniLyricsFrame, Element> draw, Func<bool> visible)
        => Embed.Comp(new MiniDraw(draw, visible), static () => new MiniHost());

    sealed record MiniDraw(Func<MiniLyricsFrame, Element> Draw, Func<bool> Visible);

    /// <summary>The track switch: one reel per track (its clock and its active line start fresh), keyed under an unkeyed box so the key holds.</summary>
    sealed class MiniHost : Component
    {
        public override Element Render()
        {
            var d = UseProps<MiniDraw>();
            var current = Playback.Current.Value;
            string trackId = current.Kind == EntityKind.Track && !current.IsNone ? Store.IdOf(new Track(current.Slot)) : "";
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch, Direction = 1, ClipToBounds = true, HitTestVisible = false,
                Children = [Embed.Comp(new MiniReelProps(trackId, d.Draw, d.Visible), static () => new MiniReel()) with { Key = "mini-lyrics:" + trackId }],
            };
        }
    }

    sealed record MiniReelProps(string TrackId, Func<MiniLyricsFrame, Element> Draw, Func<bool> Visible);

    /// <summary>One track's reel: the document, the state, the line clock.</summary>
    sealed class MiniReel : Component
    {
        static readonly string[] s_none = [];

        readonly Signal<int> _active = new(0);
        Doc? _doc;
        Action? _tick, _onTransport;
        bool _seeded, _running;
        TimerHandle _next;

        public override Element Render()
        {
            var p = UseProps<MiniReelProps>();
            string trackId = p.TrackId;
            var tick = _tick ??= Tick;
            _onTransport ??= OnTransport;
            UseLayoutEffect(() => { if (trackId.Length > 0) Store.Ensure(trackId); }, DepKey.Empty);
            _ = Store.Changed.Value;
            var doc = trackId.Length > 0 ? Store.Doc(trackId) : null;
            bool synced = PeekClock.ShouldShow(doc) && !SyncGate.SyncSuppressed(Playback.VideoActive.Value);
            _doc = synced ? doc : null;
            bool visible = p.Visible();
            _running = synced && visible;
            _next = UseTimeout(tick, 1f, DepKey.Empty);
            UseSignalEffect(_onTransport);   // a play, a pause, a seek, a report: the next line's time moved
            UseLayoutEffect(() => { if (_doc is not null) Tick(); else _next.Cancel(); }, DepKey.From(synced ? 1 : 0, visible ? 1 : 0));

            var state = doc is null ? MiniLyricsState.None
                : doc.Lines.Count == 0 ? MiniLyricsState.Instrumental
                : synced ? MiniLyricsState.Lines
                : MiniLyricsState.Unsynced;
            if (state == MiniLyricsState.Lines && !_seeded)
            {
                // Seed once, before the drawer mounts (nothing reads the signal yet), so a face shown mid-song opens on the sung line.
                _seeded = true;
                _active.SetIfChanged(Math.Max(0, PeekClock.ActiveAndPeek(doc, PositionNow()).Active));
            }
            IReadOnlyList<string> lines = state is MiniLyricsState.Lines or MiniLyricsState.Unsynced ? TextsOf(doc!) : s_none;
            return p.Draw(new MiniLyricsFrame(state, lines, _active));
        }

        /// <summary>The lines' texts, built once per document publication.</summary>
        static string[] TextsOf(Doc doc)
        {
            var lines = doc.Lines;
            var texts = new string[lines.Count];
            for (int i = 0; i < texts.Length; i++) texts[i] = lines[i].Text;
            return texts;
        }

        static long PositionNow() => Playback.Snap().Position(Playback.FrameNowMs());

        /// <summary>The transport's edges re-time the next line (subscribed here; the tick itself only peeks).</summary>
        void OnTransport()
        {
            _ = Playback.IsPlaying.Value;
            _ = Playback.PositionMs.Value;
            _ = Playback.LastSeekLandedGen.Value;
            Tick();
        }

        /// <summary>The active line now, and a timer for the next line's start on the playback clock (none while paused, after the last
        /// line or while the face is hidden).</summary>
        void Tick()
        {
            if (_doc is not { } doc || !_running) { _next.Cancel(); return; }
            long now = Playback.FrameNowMs();
            var snap = Playback.Snap();
            long position = snap.Position(now);
            var lines = doc.Lines;
            int active = ResolveLine(lines, position + PeekClock.LeadMs);
            _active.SetIfChanged(Math.Max(0, active));
            int next = active + 1;
            double perMs = (snap.Position(now + 1000L) - position) / 1000d;
            if (next >= lines.Count || perMs <= 0d) { _next.Cancel(); return; }
            double dueMs = (lines[next].StartMs - PeekClock.LeadMs - position) / perMs;
            _next.RestartIn((float)Math.Max(1d, dueMs + 1d));
        }
    }
}
