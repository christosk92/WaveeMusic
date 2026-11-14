// ── Shell/Lyrics.Mini.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the mini player's lyrics face (#197): the ONE store document, a line clock, and the template's drawing
//
// Role: CORE HOST (it builds no element of its own: the mini player's template draws every line through its theme)
//
// The mini player's Lyrics face is a reading window, not the full view: the strip's ticker (the current line and the next, rolling up)
// and a short scrolling column elsewhere, each theme marking the active line its own way. Like the now-playing panel's reel (`PeekReel`)
// it plans no fetch of its own beyond `Store.Ensure`, reads the one document, and ticks a 100 ms line clock only while the face shows
// (the mini player is open), so a hidden face costs nothing. The active line is a signal: the drawer re-renders once per line.

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
        const float TickMs = 100f;
        static readonly string[] s_none = [];

        readonly Signal<int> _active = new(0);
        Doc? _doc;
        Action? _tick;
        bool _seeded;

        public override Element Render()
        {
            var p = UseProps<MiniReelProps>();
            string trackId = p.TrackId;
            var tick = _tick ??= Tick;
            UseLayoutEffect(() => { if (trackId.Length > 0) Store.Ensure(trackId); }, DepKey.Empty);
            _ = Store.Changed.Value;
            var doc = trackId.Length > 0 ? Store.Doc(trackId) : null;
            bool synced = PeekClock.ShouldShow(doc) && !SyncGate.SyncSuppressed(Playback.VideoActive.Value);
            _doc = synced ? doc : null;
            bool visible = p.Visible();
            UseInterval(tick, TickMs, enabled: synced && visible);
            UseLayoutEffect(() => { if (_doc is not null) Tick(); }, DepKey.From(synced));

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

        void Tick()
        {
            if (_doc is not { } doc) return;
            int active = PeekClock.ActiveAndPeek(doc, PositionNow()).Active;
            _active.SetIfChanged(Math.Max(0, active));
        }
    }

    /// <summary>The mini player's lines as a picture (<see cref="Wavee.Theming.Contracts.ILyricsHost.FlatRows"/>): the active line in full ink and
    /// every other line at <see cref="Ink"/> of white, near or far, sung or not, so a few lines in a small window read as one calm column
    /// instead of a ladder that leaves the line above or below half visible.</summary>
    public static class MiniRows
    {
        /// <summary>The share of white every line but the active one shows.</summary>
        public const float Ink = 0.42f;

        /// <summary>The opacity a style gives a row whose own ink is <paramref name="rowInk"/> (its alpha: the secondary ink, the word wipe's unsung
        /// alpha, or 1 for a row drawn in full ink) so it shows <see cref="Ink"/>; NaN where the mount keeps its ladder (not flat, a podcast reader
        /// that does not play, or the active line).</summary>
        public static float Opacity(Wavee.Theming.Contracts.ILyricsHost host, bool podcast, bool ownsPlayback, int emphasis, float rowInk)
            => host.FlatRows && !TranscriptPresentation.Neutral(podcast, ownsPlayback) && Emphasis.DistOf(emphasis) > 0
                ? MathF.Min(1f, Ink / MathF.Max(rowInk, 0.01f))
                : float.NaN;

        /// <summary>The alpha of a music row's own ink in the shared styles: a word-synced line already sung shows its sung (full) ink, one not yet
        /// sung its unsung ink (<see cref="Wipe.UnsungAlpha"/>), a line-synced row <paramref name="secondary"/>.</summary>
        public static float RowInk(Line line, int emphasis, float secondary)
            => line.IsWordByWord && line.Syllables.Count > 0 ? (emphasis & Emphasis.PastBit) != 0 ? 1f : Wipe.UnsungAlpha : secondary;
    }
}
