// ── Shell/NowPlayingFace.cs ────────────────────────────────────────────────────────────────────────────────────────
// the now-playing face's HOLD as one core model (themes plan C2, X4): which identity the face (art, title, artists, heart,
// both routes) states while a skip resolves, plus the three facts the model does not carry (the last identity that fully
// resolved, when the deck last changed under it, when the current load began) and the identity READERS every surface
// shares
//
// Role: CORE
// Spec: ch 20 §0, §7; the rule it applies is `Shell.PlayerBarRules.FaceOf` (`Shell.PlayerBar.cs`)
//
// Before this, the store was `BarFaceHold` in `Shell.PlayerBar.UI.cs`: plain statics written from the bar's layout effects.
// The semantics are unchanged and are kept as plain mutable fields with ONE signal, deliberately: the shell mounts exactly
// one player dock, the dock owns every write (from layout effects, after the render that read them), and the only thing a
// reader must RE-RUN for is a deadline passing, which is what `Wake` is. Making the values themselves signals would publish
// them a frame late to the bound title channel and flash "Loading…" for exactly the frame this whole model exists to remove.
//
// Everything that decides is pure and takes its clock as an argument (`nowMs`), so `NowPlayingFaceTests` drives it with a
// fake clock and no engine.

using System;

using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    public sealed class NowPlayingFace
    {
        /// <summary>The one face the shell's dock mounts. UI thread only.</summary>
        public static readonly NowPlayingFace Shared = new();

        EntityRef _held;
        EntityId _heldContext;
        EntityRef _identity;
        long _changedAtMs;
        long _loadingSinceMs;
        bool _playingBeforeLoad;

        /// <summary>Bumped by the bar's two grace timers (<see cref="Bump"/>): the ONLY thing that re-renders a reader for
        /// a deadline. A deadline that nothing is watching would leave the bar showing the held face (or no sweep) forever.</summary>
        public readonly Signal<int> Wake = new(0);

        /// <summary>The last playable whose title was known: the face the bar keeps through a short load.</summary>
        public EntityRef Held => _held;

        /// <summary>The playback context <see cref="Held"/> was resolved under, so a held art click still lands on the
        /// playlist the held track came from rather than the one being loaded into.</summary>
        public EntityId HeldContext => _heldContext;

        /// <summary>The last SETTLED answer to "is it playing", i.e. the model's <c>IsPlaying</c> outside a load: the user's
        /// intent through the gap a skip opens (<see cref="PlayerBarRules.ShowsPauseGlyph"/>). <c>IsPlaying</c> is
        /// <c>Phase == Playing</c> and therefore FALSE for the 150 ms a skip takes; the primary's face must not read that,
        /// or it flips pause → play → pause on every track change.</summary>
        public bool PlayingBeforeLoad => _playingBeforeLoad;

        /// <summary>Frame-clock stamp of the open loading/buffering window, or 0: the sweep grace's origin.</summary>
        public long LoadingSinceMs => _loadingSinceMs;

        /// <summary>Wake every reader of <see cref="Wake"/> (a hold or sweep deadline passed).</summary>
        public void Bump() => Wake.Value = Wake.Peek() + 1;

        // ── the decision ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>WHICH identity the face shows, and the arm that chose it. Every part of the face reads THIS (the render
        /// for the art, the heart and the routes, the bound title channel, the artists line), so the six things that used to
        /// change at once on a skip change together or not at all.
        ///
        /// <para><paramref name="titleKnown"/> answers "has this row's title landed?". It is a callback, not a bool, so the
        /// HELD row's title is read only when there is a held face to ask about: the read subscribes to a catalog table in
        /// a reactive caller, and an eager one would wake it for tables it never needed.</para></summary>
        public (FaceArm Arm, EntityRef Ref) Resolve(PlayerState state, EntityRef current, Func<EntityRef, bool> titleKnown,
            long nowMs)
        {
            var held = _held;
            // A held row that has itself been evicted (its title no longer reads) is not a face worth showing.
            bool hasHeldFace = !held.IsNone && !held.Equals(current) && titleKnown(held);
            float sinceMs = current.Equals(_identity)
                ? (float)(nowMs - _changedAtMs)
                : 0f;                                   // the change is landing in THIS render; the stamp follows it
            var arm = PlayerBarRules.FaceOf(state, titleKnown(current), hasHeldFace, sinceMs);
            return (arm, arm == FaceArm.Hold ? held : current);
        }

        // ── the bookkeeping, committed AFTER the render that read it ────────────────────────────────────────────────

        /// <summary>The deck row (or whether its title landed) changed: stamp the hold window's origin, and keep the held
        /// face. An empty deck CLEARS the held face: "Nothing playing" is its own authored copy, and pressing play from idle
        /// must not flash the previous session's track for the hold on the way in.</summary>
        public void Observe(EntityRef current, bool titleKnown, EntityId contextUri, long nowMs)
        {
            if (!current.Equals(_identity))
            {
                _identity = current;
                _changedAtMs = nowMs;
            }
            if (current.IsNone) { _held = default; _heldContext = default; }
            else if (titleKnown) { _held = current; _heldContext = contextUri; }
        }

        /// <summary>The loading/buffering window opened or closed.</summary>
        public void ObserveLoading(bool loadingNow, long nowMs) => _loadingSinceMs = loadingNow ? nowMs : 0L;

        /// <summary>The fold's state or the model's playing bit changed: a LOAD keeps whatever the answer settled on last.</summary>
        public void ObserveSettled(PlayerState state, bool playing)
        {
            if (state != PlayerState.Loading) _playingBeforeLoad = playing;
        }

        /// <summary>How long the current loading window has been open, for <see cref="PlayerBarRules.TopEdgeSweeps"/>. The
        /// render that OPENS the window reads 0 (the layout effect stamps it after), and the wake timer brings the sweep in
        /// at <see cref="PlayerBarRules.SweepDelayMs"/> if the load is still running.</summary>
        public float MsInLoading(bool loadingNow, long nowMs)
            => loadingNow && _loadingSinceMs > 0L ? (float)(nowMs - _loadingSinceMs) : 0f;

        // ── the identity readers (every one peeks or reads the model LIVE; never a render-time capture) ─────────────

        /// <summary>The playable's title, or "" while it has not landed. Reads the row's own table, so a reactive caller
        /// re-runs when it changes; the bar NEVER renders a uri (ch 20 §7).</summary>
        public static string TitleOf(EntityRef r)
        {
            if (r.IsNone) return "";
            switch (r.Kind)
            {
                case EntityKind.Track:
                {
                    _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
                    _ = Entities.Current.Tracks.Changed.Value;
                    var t = new Track(r.Slot);
                    return t.IsValid && t.Knows(TrackFields.Title) ? t.Title : "";
                }
                case EntityKind.Episode:
                {
                    _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
                    _ = Entities.Current.Episodes.Changed.Value;
                    var e = new Episode(r.Slot);
                    return e.IsValid && e.Knows(EpisodeFields.Title) ? e.Title : "";
                }
                default:
                    return "";
            }
        }

        /// <summary>Has <paramref name="r"/>'s title landed? The callback <see cref="Resolve"/> asks.</summary>
        public static bool TitleKnown(EntityRef r) => TitleOf(r).Length > 0;

        /// <summary>The playable's uri (the save target), or null for a row with none.</summary>
        public static string? UriOf(EntityRef r)
        {
            if (r.IsNone) return null;
            return r.Kind switch
            {
                EntityKind.Track when new Track(r.Slot) is { IsValid: true } t => t.Uri.Text,
                EntityKind.Episode when new Episode(r.Slot) is { IsValid: true } e => e.Uri.Text,
                _ => null,
            };
        }

        /// <summary>The deck's track, peeked, or <c>default</c> when the deck holds none (or holds an episode).</summary>
        public static Track TrackPeek()
        {
            var r = Playback.Current.Peek();
            return !r.IsNone && r.Kind == EntityKind.Track ? new Track(r.Slot) : default;
        }

        /// <summary>Does the deck's track have a video, peeked?</summary>
        public static bool HasVideoPeek() => TrackPeek() is { IsValid: true } t && t.HasVideo;
    }
}
