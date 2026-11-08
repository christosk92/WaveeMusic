// ── Shell/Rail.cs ──────────────────────────────────────────────────────────────────────────────────────────────────
// RailVideoCoupling, PlayerCatalog, PlayerPrefs, NpvDiagnostics
//
// Role: CORE
// Owner: K
// Wave: 4
// Budget: 200 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The right rail's four engine-free decisions. `Rail.UI.cs` and the themes' panel views render what these decide.
//
//   • `Rail.VideoCoupling` — the two-way coupling between the rail's own open/closed + mode and the
//     video surface's placement (`Shell/Video.cs`). Four rules that are each an edge case, collected here so they are
//     asserted once over plain values instead of re-derived — and re-broken — at each of the four call sites.
//   • `Rail.PlayerCatalog` — the ONE table of the eleven player styles behind the style chip's picker, the Settings row and
//     the palette. Ids and slugs are PERSISTED: never renumber, never rename.
//   • `Rail.PlayerPrefs` — a catalog-aware ADAPTER over owner L's `Prefs.NpvPlayer` (the ONE epoch); it clamps on READ
//     as well as write, so a hand-edited value can never crash a render: it degrades to Cover / the first finish.
//   • `Rail.NpvDiagnostics` — the always-on `npv` log category. A preference written from several surfaces is only
//     attributable if each write says which one it came from (CLAUDE.md: always-on logs, no env switches).
//   • `Rail.Hero` / `Rail.Friends` (stage B) — the pinned hero's derived geometry (the W7b width ladder) and the friends
//     feed's surface / live-window / relative-time / route rules plus its two host seams.
//
// Rules: no allocation after warm-up (P8); no LINQ, no closures, no async, no boxing (P9); UI thread only (C1).

using FluentGpu.Signals;

using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Rail
{
    // ── 1. the video coupling ───────────────────────────────────────────────────────────────────────────────────────
    //
    // The rail's MODE vocabulary is owner I's `Shell.RailMode` (`Shell/Shell.cs` §6, beside `Shell.Ui.Mode`), because
    // the player bar's toggles write it and the session document persists it — `NowPlaying` is 0.2.9's `Details`.
    // `Video` is the video-first body (the docked card's takeover face, track meta, Up next), entered only when the
    // user docks a video into a CLOSED rail — never by a track change alone.

    /// <summary>The PURE rail ↔ docked-video coupling. Plain values in, a decision out — no <c>Signal&lt;T&gt;</c>, no
    /// FluentGpu type — so it is verifiable without a GPU or a window.</summary>
    public static class VideoCoupling
    {
        /// <summary>Docking was just requested (the user picked "Dock in rail", or a fresh profile's default resolved
        /// to Docked). Which rail mode should be showing? <c>null</c> means leave the rail exactly as it is — this
        /// only fires when the rail was CLOSED, so opening it for video does not clobber a mode the user was already
        /// looking at.</summary>
        /// <param name="host">Which docked host would own the surface (<see cref="Video.DockedHosting.HostFor"/>).
        /// Rail-only: when the watch page's in-page stage is what will host the video, opening the rail into the
        /// video-first body would open a rail whose card is mounted somewhere else entirely — an empty takeover body
        /// next to the real picture.</param>
        public static Shell.RailMode? ModeOnDock(bool railOpen, Video.DockedHost host)
            => host != Video.DockedHost.Rail || railOpen ? null : Shell.RailMode.Video;

        /// <summary>The user closed the rail. What should happen to a docked video? Returns the DEMOTE target, or
        /// <see cref="Video.SurfacePlacement.None"/> for "nothing to do". Only Docked is ever demoted here — the rail
        /// closing has no opinion about a floating or detached video.
        ///
        /// <para>INERT while the PAGE STAGE is hosting: the rail is not where that video lives, so closing the rail
        /// has no more to say about it than closing it says about a detached window. Without this term the rule would
        /// demote a watch page's in-page stage to the mini player and yank the video off the page being read.</para></summary>
        public static Video.SurfacePlacement OnRailClosed(in Video.PlacementState s, Video.DockedHost host)
            => host == Video.DockedHost.Rail && Video.PlacementCore.Resolve(s) == Video.SurfacePlacement.Docked
                ? Video.SurfacePlacement.Floating : Video.SurfacePlacement.None;

        /// <summary>The rail was opened again. Should video return to the dock? Keyed on
        /// <see cref="Video.PlacementState.Preferred"/> being Docked — exactly what distinguishes "the rail took my
        /// video away" (re-dock it) from "I deliberately chose the mini player from the menu" (leave it floating,
        /// since THAT write set <c>Preferred = Floating</c>). Excludes Fullscreen: entering fullscreen from the dock
        /// must not re-fire this the moment the rail happens to still be open underneath it.</summary>
        public static bool ReDockOnRailOpen(in Video.PlacementState s)
            => s.Preferred == Video.SurfacePlacement.Docked
            && s.Requested != Video.SurfacePlacement.None
            && s.Requested != Video.SurfacePlacement.Docked
            && s.Requested != Video.SurfacePlacement.Fullscreen;

        /// <summary>Video left the dock — turned off, its content lost availability, or it moved elsewhere — while the
        /// rail was showing the video-first body. Should the rail close? Yes: <see cref="Shell.RailMode.Video"/> has nothing
        /// left to show once the card it hosts is gone.
        ///
        /// <para><paramref name="hostBefore"/>, not the host NOW: once the video has left the dock the derivation
        /// always reads <see cref="Video.DockedHost.Rail"/> (nothing is docked, so the rail is the resting owner),
        /// which would make a current-host term unconditionally true and close the rail on behalf of a card the page
        /// stage was holding. The question is about the body that WAS on screen.</para></summary>
        public static bool CloseRailOnVideoLeft(Shell.RailMode mode, bool videoTurnedOff, Video.DockedHost hostBefore)
            => hostBefore == Video.DockedHost.Rail && mode == Shell.RailMode.Video && videoTurnedOff;

        /// <summary>What the rail's BODY renders, given the user's chosen mode and whether the page stage owns the one
        /// video surface. Video-first and NowPlaying (0.2.9 "Details") are the two bodies that HOST the docked card; when the stage owns
        /// the surface neither has anything left to host — a takeover body with no takeover, and a pinned hero square
        /// that would render an empty tile above the credits — so both fall back to Queue, the one body that is always
        /// meaningful while something is playing.
        ///
        /// <para>A substitution at RENDER time, never a write to the rail's mode. The user's chosen mode is untouched,
        /// so the moment the stage yields the rail is back exactly where it was — no displaced-mode memory to restore,
        /// and therefore no ordering between "the stage released it" and "the rail restored itself" to get wrong.</para></summary>
        public static Shell.RailMode BodyModeFor(Shell.RailMode mode, bool stageHostsVideo)
            => stageHostsVideo && mode is Shell.RailMode.Video or Shell.RailMode.NowPlaying ? Shell.RailMode.Queue : mode;

        /// <summary>What occupies the NowPlaying arm's pinned slot — ONE slot, two possible occupants, never both. The page stage hosting
        /// the video leaves the hero tile alone (the stage owns the one surface); a docked video IS the slot; otherwise the tile, with
        /// the Art | Video switch over its corner when the video could be docked here.</summary>
        /// <param name="stageHosts">The page stage hosts the docked video.</param>
        /// <param name="dockedResolved">The video's resolved placement is Docked.</param>
        /// <param name="dockAllowed">Docking is among the placements available right now.</param>
        public static PanelPinned PinnedFor(bool stageHosts, bool dockedResolved, bool dockAllowed)
            => stageHosts ? PanelPinned.Art
             : dockedResolved ? PanelPinned.DockedVideo
             : !dockAllowed ? PanelPinned.Art
             : PanelPinned.ArtWithVideoToggle;
    }

    // ── 2. the player-style catalog (ids and slugs are PERSISTED) ───────────────────────────────────────────────────

    /// <summary>The ONE table behind the style chip's picker, the Settings row, the palette's "next style" and the deck's dispatch: eleven
    /// styles, and the one finish row that exactly two of them carry (Vinyl, Pocket). Nothing else is a choice: a vinyl's speed follows the
    /// release (an album spins at 33⅓, a single at 45), and no style has a size, a sleeve or a background. Ids and slugs are PERSISTED (the
    /// store keeps the id; the finish keys carry the slug): never renumber, never rename. Names and lines are Loc KEYS. The DEFAULT finish is
    /// always listed FIRST, so a stored 0 is always valid.</summary>
    public static class PlayerCatalog
    {
        /// <param name="Swatch">Packed ARGB, or <see cref="FromCover"/> to paint the playing cover (the picture disc).</param>
        public readonly record struct Finish(string Slug, string LabelKey, uint Swatch);
        public readonly record struct Style(int Id, string Slug, string NameKey, string LineKey, Finish[] Finishes)
        {
            /// <summary>Does this style offer a finish (Vinyl, Pocket)?</summary>
            public bool HasFinish => Finishes.Length > 0;
        }

        public const int Cover = 0, Canvas = 1, Visualizer = 2, Vinyl = 3, Cd = 4, Cassette = 5, Reel = 6, Pocket = 7, Hifi = 8,
                         Skin = 9, Metro = 10;
        public const int DefaultStyleId = Cover;
        /// <summary>Swatch 0 = paint the playing cover (the picture disc's swatch is the cover itself).</summary>
        public const uint FromCover = 0;

        /// <summary>Vinyl's finishes; the picture disc prints the cover on the record (the indices are persisted).</summary>
        public const int VinylBlack = 0, VinylSmoke = 1, VinylRuby = 2, VinylPicture = 3;
        /// <summary>Pocket's finishes.</summary>
        public const int PocketGraphite = 0, PocketSilver = 1, PocketWhite = 2;

        static readonly Finish[] None = [];
        static readonly Finish[] VinylFinishes =
        [
            new("black", "player.finish.black", 0xFF151515), new("smoke", "player.finish.smoke", 0xFF5A5A64),
            new("ruby", "player.finish.ruby", 0xFF8A1C30), new("picture", "player.finish.picture", FromCover),
        ];
        static readonly Finish[] PocketFinishes =
        [
            new("graphite", "player.finish.graphite", 0xFF2C2D32), new("silver", "player.finish.silver", 0xFFC9CCD2),
            new("white", "player.finish.white", 0xFFF4F4F6),
        ];

        public static readonly Style[] Styles =
        [
            new(Cover, "cover", "player.style.cover", "player.style.coverLine", None),
            new(Canvas, "canvas", "player.style.canvas", "player.style.canvasLine", None),
            new(Visualizer, "visualizer", "player.style.visualizer", "player.style.visualizerLine", None),
            new(Vinyl, "vinyl", "player.style.vinyl", "player.style.vinylLine", VinylFinishes),
            new(Cd, "cd", "player.style.cd", "player.style.cdLine", None),
            new(Cassette, "cassette", "player.style.cassette", "player.style.cassetteLine", None),
            new(Reel, "reel", "player.style.reel", "player.style.reelLine", None),
            new(Pocket, "pocket", "player.style.pocket", "player.style.pocketLine", PocketFinishes),
            new(Hifi, "hifi", "player.style.hifi", "player.style.hifiLine", None),
            new(Skin, "skin", "player.style.skin", "player.style.skinLine", None),
            new(Metro, "metro", "player.style.metro", "player.style.metroLine", None),
        ];

        /// <summary>How many styles there are (the picker's dots, the carousel's ring).</summary>
        public static int Count => Styles.Length;

        public static bool IsStyleId(int id) => (uint)id < (uint)Styles.Length && Styles[id].Id == id;

        /// <summary>The style for an id, falling back to <see cref="DefaultStyleId"/>: a stored id this build no longer defines degrades
        /// rather than throwing inside a render.</summary>
        public static Style ById(int id) => Styles[IsStyleId(id) ? id : DefaultStyleId];

        /// <summary>Is <paramref name="id"/> drawn by the deck (a device with a clock), rather than the cover or the canvas?</summary>
        public static bool IsDeck(int id) => id is not (Cover or Canvas) && IsStyleId(id);

        /// <summary>The ring distance from <paramref name="from"/> to <paramref name="to"/>: the shorter way round, in (−n/2, n/2].</summary>
        public static int RingDelta(int from, int to)
        {
            int n = Styles.Length, d = ((to - from) % n + n) % n;
            return d > n / 2 ? d - n : d;
        }
    }

    // ── 3. the player prefs — an ADAPTER over `Prefs.NpvPlayer` (owner L), never a second epoch ─────────────────────

    /// <summary>The style and finish preferences as the RAIL speaks them: catalog-aware, and with the always-on <c>source</c> attribution
    /// every write carries.
    ///
    /// <para><b>No state lives here.</b> The epoch and the persisted choices are owner L's `Platform/Prefs.Player.cs` (`Prefs.NpvPlayer`),
    /// the one cross-surface epoch every writer bumps once. Every member forwards; the only thing this class adds is what `Prefs` deliberately
    /// does not know: the CATALOG (the style count, the finish counts and slugs) and the diagnostic line. It clamps on READ as well as write,
    /// so a hand-edited value can never crash a render: it degrades to Cover / the first finish.</para></summary>
    public static class PlayerPrefs
    {
        /// <summary>The ONE epoch — `Prefs.NpvPlayer.Epoch`, the same instance.</summary>
        public static Signal<int> Epoch => Prefs.NpvPlayer.Epoch;

        public static int ClampStyle(int id) => PlayerCatalog.IsStyleId(id) ? id : PlayerCatalog.DefaultStyleId;

        /// <summary>Reactive read (subscribes the epoch) of the chosen style id, clamped against the catalog.</summary>
        public static int Style() => ClampStyle(Prefs.NpvPlayer.Style());

        /// <summary>The chosen style id, peeked (a click handler).</summary>
        public static int StylePeek() => ClampStyle(Prefs.NpvPlayer.StylePeek());

        /// <summary>Reactive read of the chosen style's catalog row.</summary>
        public static PlayerCatalog.Style CurrentStyle() => PlayerCatalog.ById(Style());

        /// <summary>Reactive read of a style's finish index (0 for a style without a finish row).</summary>
        public static int Finish(int styleId)
        {
            var s = PlayerCatalog.ById(styleId);
            return s.HasFinish ? Prefs.NpvPlayer.Finish(s.Slug, s.Finishes.Length) : 0;
        }

        /// <summary>A style's finish index, peeked.</summary>
        public static int FinishPeek(int styleId)
        {
            var s = PlayerCatalog.ById(styleId);
            return s.HasFinish ? Prefs.NpvPlayer.FinishPeek(s.Slug, s.Finishes.Length) : 0;
        }

        /// <summary>Persist a style and bump once.</summary>
        public static void SetStyle(int id, string source)
        {
            int from = StylePeek(), to = ClampStyle(id);
            if (from == to) return;
            Prefs.NpvPlayer.SetStyle(to);
            NpvDiagnostics.StyleSet(PlayerCatalog.ById(from).Slug, PlayerCatalog.ById(to).Slug, source);
        }

        /// <summary>The palette's "next style": wraps at the end of the table.</summary>
        public static void NextStyle(string source) => SetStyle((StylePeek() + 1) % PlayerCatalog.Count, source);

        /// <summary>Persist one style's finish and bump once (a style without a finish row ignores it).</summary>
        public static void SetFinish(int styleId, int index, string source)
        {
            var s = PlayerCatalog.ById(styleId);
            if (!s.HasFinish) return;
            int to = (uint)index < (uint)s.Finishes.Length ? index : 0;
            if (to == FinishPeek(styleId)) return;
            Prefs.NpvPlayer.SetFinish(s.Slug, to);
            NpvDiagnostics.FinishSet(s.Slug, s.Finishes[to].Slug, source);
        }
    }

    // ── 4. the always-on `npv` log category ─────────────────────────────────────────────────────────────────────────

    /// <summary>How a preference written from several surfaces is attributed. Always on: CLAUDE.md bans env-var switches for behaviour AND
    /// verification, and without the <c>source</c> field "the style changed" is unattributable.</summary>
    public static class NpvDiagnostics
    {
        public const string Category = "npv";
        public const string SourcePicker = "picker", SourceSettings = "settings", SourcePalette = "palette";

        public static void StyleSet(string from, string to, string source) => Log.Event(
            WaveeLogLevel.Info, Category, "style.set",
            "player style " + from + " -> " + to + " via " + source,
            fields: [WaveeLogField.Of("from", from), WaveeLogField.Of("to", to), WaveeLogField.Of("source", source)]);

        public static void FinishSet(string style, string finish, string source) => Log.Event(
            WaveeLogLevel.Info, Category, "finish.set",
            "player finish " + style + "=" + finish + " via " + source,
            fields: [WaveeLogField.Of("style", style), WaveeLogField.Of("finish", finish), WaveeLogField.Of("source", source)]);

        public static void PickerClosed(bool committed) => Log.Debug(Category, committed ? "player style picker: done" : "player style picker: cancelled");
    }

    // ── 5. the now-playing hero's geometry (stage B; NowPlayingPanel.cs:512, :541-542) ──────────────────────────────

    /// <summary>The pinned hero's derived numbers. There is no width BRANCH anywhere in the rail (ch 21 W7b): every
    /// number is a derivation of the live rail width, and these are the ones the tile, the toggle and the deck key
    /// share.</summary>
    public static class Hero
    {
        /// <summary>The header strip's fixed height (the "Now playing" eyebrow and the style chip; while picking, "Player style", Cancel, Done).</summary>
        public const float HeaderRowH = 36f;
        /// <summary>The tile's own inset on its left, top and right (the app's Spacing.S, restated engine-free).</summary>
        public const float Inset = 8f;
        /// <summary>Top of the ART inside the tile — DERIVED, never a literal, so the Art|Video toggle follows the strip.</summary>
        public const float ArtTop = Inset + HeaderRowH + Inset;
        /// <summary>The deck's side is part of its remount key, so it is quantised: one remount per 4 DIP of drag.</summary>
        public const float SideQuantum = 4f;

        /// <summary>The art / deck side: <c>round((railW − 2·S) / 4) · 4</c> — 184 / 324 / 484 at 200 / 340 / 500.</summary>
        public static float Side(float railWidth)
            => MathF.Round(MathF.Max(1f, railWidth - 2f * Inset) / SideQuantum) * SideQuantum;
    }

    // ── 6. the friends feed (stage B; FriendsPanel.cs, FriendsBridge.cs; ch 21 G6, §9.6 Q5) ─────────────────────────

    /// <summary>The friend-activity feed as the panel reads it: the rows are <c>Edges.Friends</c> under ONE synthetic
    /// parent, and the coarse state and the two host verbs are seams the Spotify host fills. With no host attached the
    /// feed reads OFFLINE — the 0.2.9 <c>NullFriendActivityService</c> answer `--fake` shows ("Sign in to see…").</summary>
    public static class Friends
    {
        /// <summary>The synthetic parent every friend edge hangs off (slot 0 is "none" everywhere).</summary>
        public const int Feed = 1;
        /// <summary>"Listening now": activity at most this old shows the presence dot and the equalizer.</summary>
        public const long LiveWindowMs = 120_000;
        /// <summary>The relative-time / live-window refresh cadence while the panel is visible.</summary>
        public const float TickMs = 30_000f;

        public enum FeedState : byte { Idle, Loading, Ready, Offline, Error }

        /// <summary>Which surface the panel shows.</summary>
        public enum Surface : byte { Rows, Skeleton, Empty, Offline, Error }

        /// <summary>The coarse feed state. Written by the host; read by the panel.</summary>
        public static readonly Signal<FeedState> State = new(FeedState.Offline);

        /// <summary>Host seam: the panel is visible (true) / hidden (false) — mount/unmount drives it.</summary>
        public static Action<bool>? SetActive { get; set; }

        /// <summary>Host seam: force a full re-seed (the error surface's Retry).</summary>
        public static Action? Refresh { get; set; }

        /// <summary>ROWS WIN whenever there are any — stale rows stay visible through a refresh or a transient error.</summary>
        public static Surface SurfaceFor(int rowCount, FeedState state) => rowCount > 0 ? Surface.Rows : state switch
        {
            FeedState.Offline => Surface.Offline,
            FeedState.Error => Surface.Error,
            FeedState.Idle or FeedState.Loading => Surface.Skeleton,
            _ => Surface.Empty,
        };

        /// <summary>Is this activity "listening now"? A zero/unknown timestamp never is.</summary>
        public static bool IsLive(long nowMs, long timestampMs)
            => timestampMs > 0 && nowMs - timestampMs <= LiveWindowMs;

        public enum AgeUnit : byte { Now, Minutes, Hours, Days }

        /// <summary>The relative-time bucket: under a minute "now", then minutes, hours, days. A timestamp from the
        /// future (clock skew) reads as now.</summary>
        public static AgeUnit Age(long ageMs, out long count)
        {
            long min = Math.Max(0L, ageMs) / 60_000L;
            if (min < 1) { count = 0; return AgeUnit.Now; }
            if (min < 60) { count = min; return AgeUnit.Minutes; }
            long hr = min / 60;
            if (hr < 24) { count = hr; return AgeUnit.Hours; }
            count = hr / 24;
            return AgeUnit.Days;
        }

        /// <summary>Where a row navigates: the listening context first, then the album, then the artist; a row with
        /// none of the three is INERT (no role, no hover plate, not focusable). The context slot is a PLAYLIST slot —
        /// the kind the feed's context resolves to.</summary>
        public enum Target : byte { None, Context, Album, Artist }

        public static Target TargetOf(int contextSlot, int albumSlot, int artistSlot)
            => contextSlot > 0 ? Target.Context : albumSlot > 0 ? Target.Album : artistSlot > 0 ? Target.Artist : Target.None;
    }
}
