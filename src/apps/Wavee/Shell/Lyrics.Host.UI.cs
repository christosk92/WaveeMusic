// ── Shell/Lyrics.Host.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// `Lyrics.ViewCore` as the structure's `ILyricsHost`: the facts and verbs the lyrics view, its overlays and its rows read
//
// Role: UI (core: no picture; every member forwards to a field or a method of the view core in `Lyrics.UI.cs`)
// Spec: docs/themes/plan/technical-plan.md §5.1 C5 / T7b; docs/guide/theming.md §2b (T7b)
//
// WHY EXPLICIT. `ViewCore` already has `Large`, `InkMode`, `Metrics`, `Secondary`, `HaloScale`, `NowMs`, `Follow_`, ... as fields and
// internal members under the names the old row read; the contract's names are the theme's vocabulary, so each is forwarded here
// rather than renamed there (the frame driver, the probes and the transcript host keep reading the fields they always did).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using LyricsContracts = Wavee.Theming.Contracts;
using ViewRegistry = FluentGpu.Theming.ViewRegistry;

namespace Wavee;

public static partial class Lyrics
{
    internal sealed partial class ViewCore : LyricsContracts.ILyricsHost
    {
        // ── the structure's lines style ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The style registered under <c>StyleKeys.LyricsLines</c>, or null where no registry provides one (a bare host).</summary>
        LyricsContracts.LyricsLinesStyle? _linesStyle;

        static bool s_noStyleLogged;

        /// <summary>Resolve the lines style from the ambient registry (called from every render that builds a line: the host's, the
        /// document host's and the transcript host's, so each tracks a registry swap).</summary>
        internal void BindStyle(ViewRegistry? registry)
            => _linesStyle = registry is not null && registry.TryGetStyle(LyricsContracts.StyleKeys.LyricsLines, out var style) ? style : null;

        /// <summary>What a line draws where no style is registered: an empty box (the first miss is logged, as the track table does).</summary>
        static Element NoLines()
        {
            if (!s_noStyleLogged)
            {
                s_noStyleLogged = true;
                Log.Warn("ui", "lyrics: no LyricsLinesStyle is registered (StyleKeys.LyricsLines); the lines render nothing");
            }
            return new BoxEl();
        }

        // ── what this mount is ──────────────────────────────────────────────────────────────────────────────────────

        bool LyricsContracts.ILyricsHost.Large => Large;
        bool LyricsContracts.ILyricsHost.OnStage => OnStage;
        float LyricsContracts.ILyricsHost.TypeScale => TypeScale;
        Ink LyricsContracts.ILyricsHost.Ink => InkMode;
        IReadSignal<ColorF>? LyricsContracts.ILyricsHost.Accent => Accent;
        float LyricsContracts.ILyricsHost.Band => _band;

        // ── what is on screen ───────────────────────────────────────────────────────────────────────────────────────

        RowMetrics LyricsContracts.ILyricsHost.Metrics => Metrics;
        bool LyricsContracts.ILyricsHost.IsPodcast => IsPodcast;
        bool LyricsContracts.ILyricsHost.OwnsPlayback => OwnsPlayback;
        bool LyricsContracts.ILyricsHost.ObservedPlaybackOwner => ObservedPlaybackOwner;

        // ── the lines ───────────────────────────────────────────────────────────────────────────────────────────────

        IVirtualLayout LyricsContracts.ILyricsHost.EnsureLayout() => EnsureLayout();
        ScrollHandle LyricsContracts.ILyricsHost.Scroll => _scroll;
        void LyricsContracts.ILyricsHost.SetViewport(NodeHandle viewport) => SetViewport(viewport);
        string LyricsContracts.ILyricsHost.RowKey(int index) => RowKey(index);

        Signal<int> LyricsContracts.ILyricsHost.EmphasisOf(int index)
            => (uint)index < (uint)_lineEmphasis.Length ? _lineEmphasis[index] : _emphasisFallback;

        FloatSignal? LyricsContracts.ILyricsHost.GlowOf(int index)
            => (uint)index < (uint)_glowAlpha.Length ? _glowAlpha[index] : null;

        Signal<int> LyricsContracts.ILyricsHost.SecondaryLine => Secondary;
        FloatSignal LyricsContracts.ILyricsHost.HaloScale => HaloScale;
        FloatSignal LyricsContracts.ILyricsHost.Now => NowMs;
        Signal<FollowMode> LyricsContracts.ILyricsHost.Follow => Follow_;
        float LyricsContracts.ILyricsHost.DofShownFor(int index) => DofShownFor(index);
        float LyricsContracts.ILyricsHost.SoftnessOfLine(int index) => SoftnessOfLine(index);
        void LyricsContracts.ILyricsHost.ReportLineNode(int index, NodeHandle node) => ReportLineNode(index, node);
        void LyricsContracts.ILyricsHost.ReportGlowNode(int index, NodeHandle node) => ReportGlowNode(index, node);
        void LyricsContracts.ILyricsHost.ReportDofNode(int index, NodeHandle node) => ReportDofNode(index, node);
        void LyricsContracts.ILyricsHost.SeekToLine(int index) => SeekToLine(index);
        bool LyricsContracts.ILyricsHost.BlurEnabled => BlurPolicy.Enabled(_strength);

        /// <summary>THE GATE that sits on the peek's dissolve (podcast-episode-peek §2): the caption, the primary pill that starts this
        /// episode, and the quiet length · language line. Only ever reached from a reader (<c>_readerEpisode</c> valid): the rail
        /// always tracks whatever is playing, so it never lands here.</summary>
        LyricsContracts.LyricsPeekGate LyricsContracts.ILyricsHost.PeekGate()
        {
            var episode = Entities.Episode(_subject);
            string duration = episode.IsValid && episode.Knows(EpisodeFields.Duration) && episode.DurationMs > 0
                ? Episode.DurationLabel(episode.DurationMs) : "";
            string meta = TranscriptPeek.MetaLine(duration, TranscriptPeek.LanguageLabel(episode.TranscriptLanguage));
            return new LyricsContracts.LyricsPeekGate(meta, () => Episode.StartAt(episode, Playback.EpisodeStartKind.Resume));
        }

        // ── the overlays ────────────────────────────────────────────────────────────────────────────────────────────

        bool LyricsContracts.ILyricsHost.DotsShown() => _dotsShown.Value;
        IReadOnlyList<FloatSignal> LyricsContracts.ILyricsHost.DotAlpha => _dotAlpha;
        void LyricsContracts.ILyricsHost.OnDotsRealized(NodeHandle row) => _dotsRowNode = row;

        /// <summary>Read with <c>.Value</c> so the overlay re-renders the moment a video starts or stops; the frame lane peeks the same state.</summary>
        bool LyricsContracts.ILyricsHost.VideoNoteShown() => SyncGate.SyncSuppressed(Playback.VideoActive.Value, IsPodcast);

        bool LyricsContracts.ILyricsHost.ResyncShown() => M.ResyncNeeded;
        FloatSignal LyricsContracts.ILyricsHost.ResyncProgress => _resyncProgress;
        void LyricsContracts.ILyricsHost.Resync() => BeginResync(Context.Scene);
    }
}
