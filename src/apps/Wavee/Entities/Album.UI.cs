// ── Entities/Album.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the album surface's self-subscribing components: the billed-artist FACE PILE and its every-artist flyout (ch 05 §0.5,
// W16, §6), the prerelease COUNTDOWN card in its three states incl. Bare (W13, §5's clock rule) and the trailing section
// STACK (W12: capped at 5, one-way "Show all N").
//
// T9b (themes plan 6.1): the PICTURES of the three components are Classic's (`Themes\Classic\Structure\Views\Detail\ClassicAlbumArtist.cs`,
// `ClassicAlbumRelease.cs`, `ClassicAlbumTrailing.cs`; the keys `Theming\Contracts\ViewKeys.DetailAlbumPlaylist.cs`). What stays here is each host's gate
// (the stamp over the tables it paints), its demands, its state (the countdown's clock, the stack's expand cell, the flyout's open handle) and its verbs;
// each builds one props record per render and mounts its key. The library pane's header and command row moved with the pane's picture (T10c1: `ClassicLibraryPaneKit.cs`).
//
// Role: UI
// Owner: M
// Wave: 5
// Budget: 800 lines
// Spec: ch 05 §9
//
// ── HOW DATA REACHES THESE (props freeze at mount) ───────────────────────────────────────────────────────────────────
//
// The frame invokes a page slot only when ITS spec changes, and the table invokes the trailing thunk only when ITS args
// change — so nothing here may depend on being re-invoked. Every component reads the tables it paints (a subscribing
// `.Changed.Value` read — INSIDE a `UseComputed` whose value is the stamp of exactly what it paints, so a publication
// that leaves the stamp equal never re-renders it; W2-A2) and takes only IDENTITY through its props: the pile (album
// slot + width), the countdown (the
// instant, keyed `prerelease:<uri>:<ticks>` because its wall-clock anchor freezes at mount), a stack (keyed
// `trail:<signature>` so a re-bound section remounts with a fresh expand state). Delegates in a props record are
// behaviour: Equals compares DATA only.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct Album
{
    // ══ 1. THE FACE PILE (ch 05 §0.5, W16, §6) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The billed-artist row: the rail's ONE artist-line look (the `Album.ArtistLine` view) fed from
    /// this host's gated stamp. The NAME is what survives the rail's width pressure: Wide paints a three-face pile + the
    /// <c>+N</c> overflow + the billed names, Medium the lead face + names on two lines + <c>+N</c>, Narrow the names alone,
    /// and the chevron follows the text. The whole line is ONE trigger: with more than one artist on the album it opens the
    /// every-artist flyout, with one it goes straight to that artist. Portraits land after the album, so the pile reads
    /// the artist table itself (props would freeze the placeholders).</summary>
    /// <summary>Keyed on the album slot: the host's gate memo folds the pile off <c>_albumSlot</c> (W2-A2), so a page
    /// whose display row moves — a <c>prerelease:</c> subject resolving to its album — mounts a fresh pile rather than
    /// painting the old row's faces until the next publication.</summary>
    static Element FacePile(Album a, float maxWidth)
        => Embed.Comp(new FacePileProps(a.Slot, maxWidth), static () => new FacePileHost())
            with { Key = "facepile:" + a.Slot.ToString(CultureInfo.InvariantCulture) };

    sealed record FacePileProps(int AlbumSlot, float MaxWidth);

    [CoreHost]
    sealed class FacePileHost : Component
    {
        int _albumSlot;
        int[] _all = new int[16];
        int _allCount;
        // The gate's outputs, written by Stamp() and read by Render (which runs only after a compute or a props change).
        bool _fromBilled;
        int _drawn, _overflow;
        Artist _lead;
        // The previous width look: Detail.RailArtistLine.Mode needs it for its hysteresis, so it is a plain field.
        Detail.RailArtistMode? _mode;
        NodeHandle _anchor;
        OverlayHandle? _handle;
        IOverlayService? _overlay;
        readonly List<Controls.Face> _faces = new(Controls.FaceMaxVisible);
        readonly Action _toggle, _goLead, _close, _clearHandle, _demand;
        readonly Action<KeyEventArgs> _key;
        readonly Action<NodeHandle> _realized;
        readonly Func<NodeHandle> _anchorFn;
        readonly Func<Element> _flyout;
        readonly Func<FaceStamp> _stamp;

        public FacePileHost()
        {
            _toggle = Toggle;
            _goLead = () => Track.GoToArtist(_lead);
            _close = () => _handle?.Close();
            _clearHandle = () => _handle = null;
            _demand = DemandPortraits;
            _key = e => { if (e.KeyCode is Keys.Down or Keys.F4) { Toggle(); e.Handled = true; } };
            _realized = h => _anchor = h;
            _anchorFn = () => _anchor;
            _flyout = Flyout;
            _stamp = Stamp;
        }

        /// <summary>What the pile paints (W2-A2): the member artists — the billed set, or every distinct contributor
        /// when nobody is billed — as their (slot, version) fold (names, portraits), how many are drawn, the +N and the
        /// source's length. The flyout is built at OPEN off <c>_all</c>, which the same compute keeps current.</summary>
        readonly record struct FaceStamp(uint Epoch, int Slot, int Drawn, int Overflow, int Source, ulong Rows);

        FaceStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Artists.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Edges.AlbumArtists.Changed.Value;
            _ = scope.Edges.AlbumTracks.Changed.Value;
            _ = scope.Edges.TrackArtists.Changed.Value;

            int slot = _albumSlot;
            var a = new Album(slot);
            if (!a.IsValid) { _allCount = 0; return new FaceStamp(epoch, slot, 0, 0, 0, RowFold.Seed); }
            var billed = a.ArtistSlots;
            var members = a.TrackSlots;
            int capacity = billed.Length;
            bool membersReady = scope.Edges.AlbumTracks.State(slot) == EdgeState.Complete;
            for (int i = 0; i < members.Length; i++)
            {
                var t = new Track(members[i]);
                capacity += t.ArtistSlots.Length;
                if (!t.Knows(TrackFields.Artists)) membersReady = false;
            }
            if (_all.Length < capacity) _all = new int[Math.Max(capacity, _all.Length * 2)];
            _allCount = PageRules.DistinctArtists(a, _all);
            _fromBilled = billed.Length > 0;
            ReadOnlySpan<int> source = _fromBilled ? billed : _all.AsSpan(0, _allCount);
            _drawn = Math.Min(Detail.RailArtistLine.WideFaces, source.Length);
            // Until every member's credits are in, an overflow would under-count — no "+N" frame at all (ch 05 §7).
            _overflow = membersReady ? PageRules.FaceOverflow(billed.Length, _allCount, _drawn) : 0;
            return new FaceStamp(epoch, slot, _drawn, _overflow, source.Length, RowFold.Rows(scope.Artists, source));
        }

        public override Element Render()
        {
            var p = UseProps<FacePileProps>();
            _overlay = UseContext(Overlay.Service);
            _albumSlot = p.AlbumSlot;
            // The gate (W2-A2): the five counters are subscribed inside the memo; the faces, the names run and the
            // tooltip below are rebuilt only when the stamp moved (or the width prop changed).
            var stamp = UseComputed(_stamp).Value;
            UseEffect(_demand);

            // An album with no resolvable artist has no attribution row at all (§6's empty-box arm).
            if (stamp.Source == 0) return ViewSlots.View(ViewKeys.AlbumArtistLine, new AlbumArtistLineProps(Detail.RailArtistMode.Wide, null));
            var a = new Album(p.AlbumSlot);
            ReadOnlySpan<int> source = _fromBilled ? a.ArtistSlots : _all.AsSpan(0, _allCount);
            int drawn = _drawn, overflow = _overflow;

            _faces.Clear();
            for (int i = 0; i < drawn; i++)
            {
                var artist = new Artist(source[i]);
                _faces.Add(new Controls.Face(artist.Name, Controls.ArtUrl(artist.ImageId)));
            }
            _lead = new Artist(source[0]);
            bool leadLive = _lead.IsValid && _lead.Uri.IsValid;

            // ONE trigger for the whole line. More than one artist on the album: the every-artist flyout; exactly one: straight
            // to that artist (a flyout of one row would be a detour). The faces carry no click of their own.
            bool menu = _allCount > 1;
            var mode = Detail.RailArtistLine.Mode(p.MaxWidth, _mode);
            _mode = mode;
            return ViewSlots.View(ViewKeys.AlbumArtistLine, new AlbumArtistLineProps(mode, new ArtistLineSpec(
                _faces, JoinNames(source), source.Length, overflow, p.MaxWidth, Loc.Get(Strings.Detail.ViewAllArtists), menu,
                menu ? _toggle : leadLive ? _goLead : null, menu ? _key : null, _realized)));
        }

        static string JoinNames(ReadOnlySpan<int> artists)
        {
            if (artists.Length == 1) return new Artist(artists[0]).Name;
            var sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < artists.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(new Artist(artists[i]).Name);
            }
            return sb.ToString();
        }

        /// <summary>Portraits do not ride the album answer: one Identity batch for the billed artists while any is still
        /// missing one (0.2.9's <c>NeedsPortraitFetch</c>), fired by the pile itself.</summary>
        void DemandPortraits()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.AlbumArtists.Changed.Value;
            var billed = new Album(_albumSlot).ArtistSlots;
            for (int i = 0; i < billed.Length; i++)
            {
                if (new Artist(billed[i]).Knows(ArtistFields.Identity)) continue;
                Entities.Ensure(scope.Artists, billed, (uint)ArtistFields.Identity);
                return;
            }
        }

        void Toggle()
        {
            var overlay = _overlay;
            if (Controls.IsNullOverlay(overlay)) return;
            if (_handle is { IsOpen: true } open) { open.Close(); return; }
            var handle = overlay.Open(_anchorFn, _flyout, FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.ClosedAction = _clearHandle;
            _handle = handle;
        }

        /// <summary>W16: every DISTINCT artist across the album (the `Album.ArtistFlyout` view: 44-DIP rows in a 280 × 360 flyout). Built at OPEN.</summary>
        Element Flyout()
        {
            var rows = new PersonFlyoutRow[_allCount];
            for (int i = 0; i < rows.Length; i++) rows[i] = FlyoutRow(new Artist(_all[i]), _close);
            return ViewSlots.View(ViewKeys.AlbumArtistFlyout, new PersonFlyoutProps(rows));
        }

        static PersonFlyoutRow FlyoutRow(Artist artist, Action close)
        {
            bool live = artist.IsValid && artist.Uri.IsValid;
            string name = artist.Name;
            return new PersonFlyoutRow(name, Controls.ArtUrl(artist.ImageId), live ? () => { Track.GoToArtist(artist); close(); } : null);
        }
    }

    // ══ 2. THE COUNTDOWN (ch 05 W13, §5's clock rule) ════════════════════════════════════════════════════════════════

    /// <summary>The prerelease card: ring + "Coming soon" in the page accent + four unit tiles; "Out now" once the instant
    /// passes (the ring goes inactive, the interval stops, nothing refetches). <paramref name="bare"/> returns only the
    /// tiles / "Out now" — the artist page's tone panels already carry a plate and an eyebrow. Keyed on the instant: its
    /// wall-clock anchor freezes at mount, so a new target must remount.</summary>
    public static Element Countdown(EntityUri subject, int releaseAtUnixSeconds, Func<ColorF> accent, bool bare = false)
        => Embed.Comp(new CountdownProps(releaseAtUnixSeconds, bare, accent), static () => new CountdownHost())
            with
            {
                Key = "prerelease:" + subject.Text + ":"
                    + DateTimeOffset.FromUnixTimeSeconds(releaseAtUnixSeconds).UtcTicks.ToString(CultureInfo.InvariantCulture),
            };

    sealed record CountdownProps(int ReleaseAt, bool Bare, Func<ColorF> Accent)
    {
        public bool Equals(CountdownProps? o) => o is not null && ReleaseAt == o.ReleaseAt && Bare == o.Bare;
        public override int GetHashCode() => HashCode.Combine(ReleaseAt, Bare);
    }

    sealed class CountdownHost : Component
    {
        const float TickMs = 1000f;

        // ONE wall-clock sample at first render and never again; every later "now" is that sample plus the FRAME clock's
        // own delta (FlipCountdown's fix for the stuck-timer bug — a per-tick wall-clock poll bakes every coarse OS-clock
        // step into the display). Plain fields: nothing re-renders off them.
        long _unixAnchorMs, _frameAnchorMs;
        readonly Signal<int> _tick = new(0);
        readonly Action _ping;

        public CountdownHost() => _ping = () => _tick.Value = _tick.Peek() + 1;

        public override Element Render()
        {
            var p = UseProps<CountdownProps>();
            if (_frameAnchorMs == 0)
            {
                _frameAnchorMs = Design.FrameTime.NowMs;
                _unixAnchorMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            _ = _tick.Value;                                        // a once-a-second ping, never the value itself
            long nowMs = _unixAnchorMs + (Design.FrameTime.NowMs - _frameAnchorMs);
            long leftMs = Math.Max(0L, p.ReleaseAt * 1000L - nowMs);
            bool released = leftMs == 0;
            UseInterval(_ping, TickMs, enabled: !released);
            // The accent is NOT a tracked read of this render (W2-A2): the eyebrow binds the thunk into its Color
            // channel — a mount-time effect that subscribes the cover's per-image palette signal and writes one scene
            // column when it grades — and the ring, whose foreground is a scalar, peeks it untracked here; the
            // once-a-second tick re-reads it, so a late grading reaches the ring within a second and never re-renders
            // the card on the palette's account.
            ColorF ringAccent = Reactive.Untrack(p.Accent);

            // The card (the ring with "Coming soon" and the four unit tiles, or "Out now") or, Bare, only its body: the `Album.Countdown` view.
            return ViewSlots.View(ViewKeys.AlbumCountdown, new AlbumCountdownProps(released, TimeSpan.FromMilliseconds(leftMs), p.Bare, p.Accent, ringAccent));
        }
    }

    // ══ 3. THE TRAILING STACK (ch 05 W12) ════════════════════════════════════════════════════════════════════════════

    /// <summary>Which relation a stack lists. The four list sections are the only stacks: Fans also like is a clipped chip
    /// row and About / Watch-video are single cards (W12's "states the sketch omits").</summary>
    enum TrailKind : byte { MoreBy, FeaturedOn, Merch, Similar }

    /// <summary>A capped vertical stack under a section header. "Show all N" lengthens it IN PLACE and is one-way. Keyed
    /// on a data signature: a re-bound section remounts with a fresh expand state.</summary>
    static Element Stack(string title, int count, TrailKind kind, int parent, string signature)
        => Embed.Comp(new StackProps(title, count, kind, parent), static () => new StackHost()) with { Key = "trail:" + signature };

    sealed record StackProps(string Title, int Count, TrailKind Kind, int Parent);

    [CoreHost]
    sealed class StackHost : Component
    {
        readonly Signal<bool> _expanded = new(false);
        readonly Action _expand;
        readonly Func<StackStamp> _stamp;
        StackProps? _props;

        public StackHost()
        {
            _expand = () => _expanded.Value = true;
            _stamp = Stamp;
        }

        /// <summary>The rows this stack shows (W2-A2): how many, the relation's own edge version (membership and
        /// order) and a fold over each shown row — the album or playlist's (slot, version), plus the row's subtitle
        /// source: the lead artist's version for an album row (via the billing edge), the owner's for a playlist row.
        /// A merch listing has no row version — it arrives whole with its edge (<c>MerchTable</c>), so the edge version
        /// is its stamp.</summary>
        readonly record struct StackStamp(uint Epoch, int Parent, TrailKind Kind, int Shown, uint Edge, ulong Rows);

        StackStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Albums.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Users.Changed.Value;
            _ = e.AlbumArtists.Changed.Value;
            var p = _props!;
            var edge = EdgeOf(e, p.Kind);
            _ = edge.Changed.Value;
            int shown = _expanded.Value ? p.Count : Math.Min(p.Count, PageRules.StackCap);
            var targets = edge.Targets(p.Parent);
            ulong rows = RowFold.Seed;
            for (int i = 0; i < shown; i++)
            {
                int slot = At(targets, i);
                switch (p.Kind)
                {
                    case TrailKind.MoreBy:
                    case TrailKind.Similar:
                        rows = RowFold.Row(rows, scope.Albums, slot);
                        rows = RowFold.Row(rows, scope.Artists, At(e.AlbumArtists.Targets(slot), 0));
                        break;
                    case TrailKind.FeaturedOn:
                        rows = RowFold.Row(rows, scope.Playlists, slot);
                        rows = RowFold.Row(rows, scope.Users, slot > Table.None && slot < scope.Playlists.Count ? scope.Playlists.Owner[slot] : Table.None);
                        break;
                    default:
                        rows = RowFold.Add(rows, slot);
                        break;
                }
            }
            return new StackStamp(epoch, p.Parent, p.Kind, shown, edge.Version(p.Parent), rows);
        }

        static EdgeTable<NoEdge> EdgeOf(Edges e, TrailKind kind) => kind switch
        {
            TrailKind.MoreBy => e.AlbumMoreBy,
            TrailKind.Similar => e.AlbumSimilar,
            TrailKind.FeaturedOn => e.AlbumRecommendations,
            _ => e.AlbumMerch,
        };

        public override Element Render()
        {
            var p = UseProps<StackProps>();
            _props = p;
            bool expanded = _expanded.Value;
            // The gate (W2-A2): the six counters are subscribed inside the memo; the rows — a CardData, a plated media
            // row and a drag source each — are rebuilt only when the stamp moved (or the title prop changed).
            _ = UseComputed(_stamp).Value;

            int shown = expanded ? p.Count : Math.Min(p.Count, PageRules.StackCap);
            var rows = new TrailRowData[shown];
            for (int i = 0; i < shown; i++) rows[i] = TrailRow(p.Kind, p.Parent, i);

            // The `Album.TrailStack` view: the title and the one-way "Show all N" over the rows ("Show all" lengthens the box in place — the new height reveals).
            return ViewSlots.View(ViewKeys.AlbumTrailStack, new AlbumTrailStackProps(p.Title, p.Count, shown, _expand, rows));
        }
    }
}
