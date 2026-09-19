// ── Entities/Album.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the album surface's self-subscribing components: the billed-artist FACE PILE and its every-artist flyout (ch 05 §0.5,
// W16, §6), the prerelease COUNTDOWN card in its three states incl. Bare (W13, §5's clock rule), the trailing section
// STACK (W12: capped at 5, one-way "Show all N"), and the artist page's album DRAWER PANEL (W19: header, 32-pitch rows in
// one or two column-major columns, shimmer cells, ready-empty + Retry, the two-node caret, selection + selection bar) —
// plus the LIBRARY PANE STATICS (§5, library rework §5.5): the hero ⋯ menu over a handle, the 36-px command circle,
// PaneHeader and PaneCommands, which Album.Pane and Artist.Reader paint
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
// `trail:<signature>` so a re-bound section remounts with a fresh expand state) and the drawer (keyed `drawer:<uri>`
// so its SelectionModel is per album). Delegates in a props record are behaviour: Equals compares DATA only.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Album
{
    // ══ 1. THE FACE PILE (ch 05 §0.5, W16, §6) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The billed-artist row: up to four framed portraits + the <c>+N</c> overflow + a chevron (ONE button that
    /// opens the every-artist flyout), then the billed names as one accent run to the lead artist. Portraits land after
    /// the album, so the pile reads the artist table itself (props would freeze the placeholders).</summary>
    /// <summary>Keyed on the album slot: the host's gate memo folds the pile off <c>_albumSlot</c> (W2-A2), so a page
    /// whose display row moves — a <c>prerelease:</c> subject resolving to its album — mounts a fresh pile rather than
    /// painting the old row's faces until the next publication.</summary>
    static Element FacePile(Album a, float maxWidth)
        => Embed.Comp(new FacePileProps(a.Slot, maxWidth), static () => new FacePileHost())
            with { Key = "facepile:" + a.Slot.ToString(CultureInfo.InvariantCulture) };

    sealed record FacePileProps(int AlbumSlot, float MaxWidth);

    sealed class FacePileHost : Component
    {
        int _albumSlot;
        int[] _all = new int[16];
        int _allCount;
        // The gate's outputs, written by Stamp() and read by Render (which runs only after a compute or a props change).
        bool _fromBilled;
        int _drawn, _overflow;
        Artist _lead;
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
            _drawn = Math.Min(Controls.FaceMaxVisible, source.Length);
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
            if (stamp.Source == 0) return new BoxEl();
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

            Element button = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f,
                Padding = new Edges4(6f, 4f, 6f, 4f), Corners = CornerRadius4.All(Radii.Card),
                Fill = ColorF.Transparent, HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = _toggle, OnKeyDown = _key, OnRealized = _realized,
                Children =
                [
                    Controls.FacePile(_faces, Controls.FaceMaxVisible, overflow),
                    Icon(Icons.ChevronDownSmall, 8f, Tok.TextTertiary),
                ],
            };
            Element names = new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
                OnClick = leadLive ? _goLead : null,
                Cursor = leadLive ? CursorId.Hand : (CursorId?)null,
                Role = leadLive ? AutomationRole.Hyperlink : AutomationRole.Text,
                Children =
                [
                    new TextEl(JoinNames(source))
                    {
                        // 14/700 is ch 05 §3's rung for the billed names (the chapter wins on look; see the report).
                        Size = 14f, LineHeight = 20f, Weight = 700, Color = Tok.AccentTextPrimary,
                        Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MaxWidth = p.MaxWidth,
                // The loc drift fixed (ch 05 §6): the tooltip was a literal beside an existing key.
                Children = [ToolTip.Wrap(button, Loc.Get(Strings.Detail.ViewAllArtists)), names],
            };
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

        /// <summary>W16: every DISTINCT artist across the album, 44-DIP rows in a 280 × 360 flyout. Built at OPEN.</summary>
        Element Flyout()
        {
            var rows = new Element[_allCount];
            for (int i = 0; i < rows.Length; i++) rows[i] = FlyoutRow(new Artist(_all[i]), _close);
            var list = new BoxEl { Direction = 1, Gap = 2f, Width = Design.Size.FacePileListW, Children = rows };
            return new BoxEl
            {
                Direction = 1, Width = Design.Size.FacePileFlyoutW, MaxHeight = Design.Size.FacePileFlyoutH,
                Padding = Edges4.All(Spacing.S),
                Children =
                [
                    ScrollView(list) with
                    {
                        Width = Design.Size.FacePileListW, MaxHeight = Design.Size.FacePileListH,
                        ContentSized = true, AutoEdgeFade = true, Grow = 0f,
                    },
                ],
            };
        }

        static Element FlyoutRow(Artist artist, Action close)
        {
            bool live = artist.IsValid && artist.Uri.IsValid;
            string name = artist.Name;
            return new BoxEl
            {
                Direction = 0, Height = Design.Size.FacePileRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = CornerRadius4.All(6f),
                Role = AutomationRole.MenuItem, Focusable = true,
                Cursor = live ? CursorId.Hand : (CursorId?)null,
                OnClick = live ? () => { Track.GoToArtist(artist); close(); } : null,
                Children =
                [
                    PersonPicture.Create("", 32f, displayName: name, imageSourcePath: Controls.ArtUrl(artist.ImageId)),
                    new TextEl(name)
                    {
                        Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary,
                        Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            }.Interactive(Interaction.Subtle);
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
        const float RingSize = 34f, TickMs = 1000f;

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

            Element body = released
                ? new TextEl(Loc.Get(Strings.Detail.PreReleaseOut))
                {
                    Size = 15f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1,
                    Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                }
                : Controls.PreReleaseCountdown(TimeSpan.FromMilliseconds(leftMs));
            if (p.Bare) return body;

            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                Padding = Edges4.All(Spacing.M), Corners = CornerRadius4.All(Radii.Card),
                Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    // isActive: !released — the ring's own Inactive state fades it; it is never removed.
                    ProgressRing.Indeterminate(size: RingSize, isActive: !released, foreground: ringAccent),
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Grow = 1f,
                        Children =
                        [
                            Design.Type.Eyebrow(Loc.Get(Strings.Detail.PreReleaseEyebrow)) with { Color = Prop.Of(p.Accent), MaxLines = 1 },
                            body,
                        ],
                    },
                ],
            };
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
            var rows = new Element[shown];
            for (int i = 0; i < shown; i++) rows[i] = TrailRow(p.Kind, p.Parent, i);

            Element title = Design.Type.RailHeader(p.Title) with
            {
                Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            Element header = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children = p.Count > shown
                    ? [title, HyperlinkButton.Create(Strings.Home.ShowAllCount(p.Count), _expand, size: ControlSize.Small)]
                    : [title],
            };
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.M, Grow = 1f, AlignSelf = FlexAlign.Stretch,
                Children = [header, new BoxEl { Direction = 1, Gap = Spacing.XS, Children = rows }],
            };
        }
    }

    // ══ 4. THE ARTIST-PAGE ALBUM DRAWER (ch 05 W19, parity 46, 55-58, 65-67) ═════════════════════════════════════════

    /// <summary>The artist page's inline album drawer body: the caret over the clicked card, the header (play circle ·
    /// cover · "Name · meta" · open-album), the tracks at a 32 pitch in one or two column-major columns, a "Show all N
    /// tracks" cell that NAVIGATES, shimmer cells while loading, the ready-empty note with Retry, selection + its bar.
    /// <paramref name="caretCenterX"/> is the clicked card's centre, relative to the panel's left edge. The slot around
    /// it (<c>DrawerVerdict.SlotHeight</c>, the open/close motion, the reveal peek) is the grid's — owner N.</summary>
    public static Element DrawerPanel(Album album, int gridColumns, float caretCenterX, Func<ColorF> accent)
        => Embed.Comp(new DrawerProps(album, gridColumns, caretCenterX, accent), static () => new DrawerHost())
            with { Key = "drawer:" + album.Uri.Text };

    sealed record DrawerProps(Album Album, int GridColumns, float CaretCenterX, Func<ColorF> Accent)
    {
        public bool Equals(DrawerProps? o)
            => o is not null && Album == o.Album && GridColumns == o.GridColumns && CaretCenterX == o.CaretCenterX;
        public override int GetHashCode() => HashCode.Combine(Album.Slot, GridColumns, CaretCenterX);
    }

    const float CaretW = 16f, CaretH = 8f, CaretOverlap = 1f, DrawerRowContentH = 28f;

    sealed class DrawerHost : Component
    {
        /// <summary>The wedge is built ONCE; only its offsets move per open card.</summary>
        static readonly PathData s_caret = BuildCaret();

        readonly SelectionModel _sel = new() { Mode = ItemsSelectionMode.Extended };
        DrawerProps? _latest;
        DrawerVerdict _verdict;
        readonly Signal<float> _panelW = new(0f);
        readonly Func<int, Element> _cell, _shimmer, _commands;
        readonly Action _retry, _demand, _demandRows, _syncSelection, _goAlbum, _playAlbum, _exitSelection;
        readonly Action<RectF> _measure;

        public DrawerHost()
        {
            _cell = Cell;
            _shimmer = ShimmerCell;
            _commands = Commands;
            _retry = () => { if (_latest is { } p) Entities.RefreshEdge(FetchEdge.AlbumTracks, p.Album.Slot); };
            _demand = Demand;
            _demandRows = DemandRows;
            _syncSelection = () => { int shown = _verdict.Shown; if (_sel.ItemCount != shown) _sel.ItemCount = shown; };
            _goAlbum = () => { if (_latest is { } p) Track.GoToAlbum(p.Album); };
            _playAlbum = () => { if (_latest is { } p && p.Album.IsValid) Playback.PlayContext(p.Album.Id); };
            _exitSelection = () => _sel.DeselectAll();
            _measure = r => { if (r.W > 0f && MathF.Abs(r.W - _panelW.Peek()) > 0.5f) _panelW.Value = r.W; };
        }

        static PathData BuildCaret()
        {
            var b = new FluentGpu.Render.PathBuilder();
            b.MoveTo(0f, CaretH);
            b.LineTo(CaretW * 0.5f, 0f);
            b.LineTo(CaretW, CaretH);
            b.Close();
            return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
        }

        public override Element Render()
        {
            var p = UseProps<DrawerProps>();
            _latest = p;
            uint scopeEpoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Albums.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Edges.AlbumTracks.Changed.Value;
            var a = p.Album;
            UseEffect(_demand, DepKey.From(a.Slot, (int)scopeEpoch));
            UseEffect(_demandRows);

            var v = DrawerVerdict.Of(a, p.GridColumns);
            _verdict = v;
            UseEffect(_syncSelection, DepKey.From(v.Shown));
            _ = _sel.Version.Value;                                  // the bar's count re-renders the panel, never a row
            int selected = _sel.SelectedCount;

            Element body = v.ReadyEmpty ? EmptyNote(_retry)
                : v.Loading ? BuildColumns(v.Shown, v.Columns, _shimmer)
                : BuildColumns(v.Shown + (v.ShowAllRow ? 1 : 0), v.Columns, _cell);

            Element panel = new BoxEl
            {
                Direction = 1, ClipToBounds = true,
                Padding = new Edges4(12f, 6f, 12f, 6f),
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                OnBoundsChanged = _measure,
                Children =
                [
                    Head(a, p.Accent()),
                    ZStack(body, Controls.SelectionBar(selected, _commands, standalone: true, bottomPadding: Spacing.S)),
                ],
            };

            // The caret: a fill wedge PLUS a 1-DIP outline, apex clamped inside the panel's rounded corners, painted LAST
            // and sunk 1 DIP into the panel so its fill hides the panel's top stroke across the wedge's base.
            float panelW = _panelW.Value;
            float apex = panelW > 2f * Radii.Card
                ? Math.Clamp(p.CaretCenterX, Radii.Card, panelW - Radii.Card)
                : MathF.Max(Radii.Card, p.CaretCenterX);
            float caretX = apex - CaretW / 2f;
            float caretY = DrawerVerdict.TopGap - CaretH + CaretOverlap;
            return new BoxEl
            {
                ZStack = true, Direction = 1,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1,
                        Children = [new BoxEl { Height = DrawerVerdict.TopGap, HitTestVisible = false }, panel],
                    },
                    new PathEl
                    {
                        OffsetX = caretX, OffsetY = caretY, Width = CaretW, Height = CaretH,
                        Geometry = s_caret, Fill = Tok.FillCardSecondary, Rule = FillRule.NonZero,
                    },
                    new PolylineStrokeEl
                    {
                        OffsetX = caretX, OffsetY = caretY, Width = CaretW, Height = CaretH,
                        P0 = new Point2(0f, CaretH), P1 = new Point2(CaretW * 0.5f, 0f), P2 = new Point2(CaretW, CaretH),
                        PointCount = 3, Color = Tok.StrokeCardDefault, Thickness = 1f, RoundCaps = false,
                    },
                ],
            };
        }

        /// <summary>28 tall: the accent play circle (ink picked off the FILL's luminance — a lifted cover accent is often
        /// pale), the 28 cover, "Name · meta" as one paragraph, the open-album action.</summary>
        Element Head(Album a, ColorF accent) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = 28f,
            Children =
            [
                new BoxEl
                {
                    Width = 26f, Height = 26f, Shrink = 0f, Corners = CornerRadius4.All(13f), Fill = accent,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = _playAlbum,
                    Children = [Icon(Icons.Play, 11f, ColorContrast.PickContrast(accent))],
                },
                Controls.Artwork(Controls.ArtUrl(a.ImageId), 28f, 28f, Radii.Control),
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Cursor = CursorId.Hand, OnClick = _goAlbum,
                    Children =
                    [
                        new SpanTextEl([
                            new TextSpan(a.Knows(AlbumFields.Title) ? a.Title : ""),
                            new TextSpan(" · " + DrawerMeta(a), Weight: 400, Color: Tok.TextSecondary, Size: 12f),
                        ])
                        {
                            Size = 13f, Weight = 600, Color = Tok.TextPrimary,
                            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
                        },
                    ],
                },
                ToolTip.Wrap(new BoxEl
                {
                    Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(14f), BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = _goAlbum,
                    Children = [Icon(Icons.OpenInNewWindow, 12f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle), Loc.Get(Strings.Menu.GoToAlbum)),
            ],
        };

        /// <summary>"Nov 4, 2014 · 14 tracks" — the discography card's OWN subtitle rule (0.2.9 <c>DiscoGrid.AlbumMeta</c>,
        /// owner N's <c>DiscoCardText</c>), so the header and the card it opened from can never disagree.</summary>
        static string DrawerMeta(Album a) => DiscoCardText.AlbumMeta(a);

        Element Cell(int i)
        {
            var v = _verdict;
            if (i >= v.Shown) return ShowAllRow(v.Total);
            var p = _latest!;
            var slots = p.Album.TrackSlots;
            if ((uint)i >= (uint)slots.Length)
                return new BoxEl { Key = "row:#" + i.ToString(CultureInfo.InvariantCulture), Height = DrawerVerdict.RowPitch };
            var t = new Track(slots[i]);
            return Embed.Comp(new DrawerRowProps(p.Album, t, i, _sel, p.Accent), static () => new DrawerRowHost())
                with { Key = "row:" + t.Slot.ToString(CultureInfo.InvariantCulture) };
        }

        /// <summary>Past the cap the last cell NAVIGATES to the album (never lengthens the drawer) — counted in the
        /// verdict's rows so the reserved slot never guesses.</summary>
        Element ShowAllRow(int total) => new BoxEl
        {
            Key = "row:show-all", Height = DrawerVerdict.RowPitch, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Cursor = CursorId.Hand, Role = AutomationRole.Button, OnClick = _goAlbum,
            Children =
            [
                new TextEl(Strings.Detail.Discography.ShowAllTracks(total))
                    { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1 },
            ],
        };

        /// <summary>A 16×11 number block and one bar capped at 240 — never a heart, a time or a "…".</summary>
        static Element ShimmerCell(int i) => new BoxEl
        {
            Key = "shimmer:" + i.ToString(CultureInfo.InvariantCulture),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = DrawerVerdict.RowPitch,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children =
            [
                new BoxEl { Width = 16f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                new BoxEl { Grow = 1f, Basis = 0f, Height = 11f, MaxWidth = 240f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            ],
        };

        /// <summary>Ready but empty: the one error affordance on the surface — the note beside a stock Retry.</summary>
        static Element EmptyNote(Action retry) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.M),
            Children =
            [
                new TextEl(Loc.Get(Strings.Detail.Empty.NoTracks))
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Size = 13f, Color = Tok.TextTertiary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                Button.Standard(Loc.Get(Strings.Common.Retry), retry),
            ],
        };

        /// <summary>ONE splitter for the real rows and the shimmer: column-major, the first ⌈n/2⌉ cells left, 20 apart.</summary>
        static Element BuildColumns(int cellCount, int columns, Func<int, Element> cell)
        {
            if (cellCount <= 0) return new BoxEl();
            if (columns <= 1)
            {
                var kids = new Element[cellCount];
                for (int i = 0; i < cellCount; i++) kids[i] = cell(i);
                return new BoxEl { Direction = 1, Children = kids };
            }
            int perColumn = (cellCount + columns - 1) / columns;
            var cols = new Element[columns];
            for (int c = 0; c < columns; c++)
            {
                int start = c * perColumn, end = Math.Min(cellCount, start + perColumn);
                var kids = end > start ? new Element[end - start] : Array.Empty<Element>();
                for (int i = start; i < end; i++) kids[i - start] = cell(i);
                cols[c] = new BoxEl
                {
                    Key = "col:" + c.ToString(CultureInfo.InvariantCulture),
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = kids,
                };
            }
            return new BoxEl { Direction = 0, Gap = Spacing.XL, Children = cols };
        }

        /// <summary>The selection bar's commands: count · Play (+ Play next · Add to queue · Like above the essentials fit)
        /// · ✕, over the registered verbs; every verb exits selection after it runs.</summary>
        Element Commands(int fit)
        {
            _ = _sel.Version.Value;
            int count = _sel.SelectedCount;
            if (count <= 0 || _latest is not { } p) return new BoxEl();
            var slots = p.Album.TrackSlots;
            var tracks = new List<Track>(count);
            for (int i = 0; i < slots.Length && i < _sel.ItemCount; i++)
                if (_sel.IsSelected(i)) tracks.Add(new Track(slots[i]));
            var ctx = new ActionContext(ActionTarget.ForTracks(tracks), Actions.Services);
            var kids = new List<Element>(7)
            {
                new TextEl(Strings.Detail.SelectedCount(count))
                    { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            };
            AddVerb(kids, ActionId.Play, in ctx);
            if (fit <= 1)
            {
                AddVerb(kids, ActionId.PlayNext, in ctx);
                AddVerb(kids, ActionId.AddToQueue, in ctx);
                AddVerb(kids, ActionId.ToggleLike, in ctx);
            }
            kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
            kids.Add(GlyphCommand(Icons.Cancel, null, Loc.Get(Strings.Detail.ClearSelection), true, _exitSelection));
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 3f, Grow = 1f, MinWidth = 0f, ClipToBounds = true,
                Children = kids.ToArray(),
            };
        }

        void AddVerb(List<Element> kids, ActionId id, in ActionContext ctx)
        {
            if (AppActions.Find(id) is not { } action) return;
            var c = ctx;
            bool enabled = action.EnabledFor(in c);
            var icon = ActionIcons.Resolve(action.IconKey, action.CheckedFor(in c));
            var exit = _exitSelection;
            Action invoke = enabled ? () => { action.Execute(c); exit(); } : static () => { };
            kids.Add(GlyphCommand(icon.Glyph ?? "", icon.Font, action.Label(c), enabled, invoke));
        }

        static Element GlyphCommand(string glyph, string? font, string label, bool enabled, Action invoke)
            => ToolTip.Wrap(new BoxEl
            {
                Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                IsEnabled = enabled, Focusable = enabled, Role = AutomationRole.Button, OnClick = invoke,
                Children = [Icon(glyph, 13f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font)],
            }.Interactive(Interaction.Subtle), label);

        void Demand()
        {
            if (_latest is not { } p || !p.Album.IsValid) return;
            var a = p.Album;
            Entities.Ensure(a, AlbumFields.Card);
            if (Entities.Current.Edges.AlbumTracks.State(a.Slot) == EdgeState.Unknown)
                Entities.EnsureEdge(FetchEdge.AlbumTracks, a.Slot);
        }

        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Edges.AlbumTracks.Changed.Value;
            if (_latest is not { } p) return;
            var slots = p.Album.TrackSlots;
            if (slots.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots), TrackFields.Row);
        }
    }

    sealed record DrawerRowProps(Album Album, Track Track, int Index, SelectionModel Selection, Func<ColorF> Accent)
    {
        public bool Equals(DrawerRowProps? o)
            => o is not null && Album == o.Album && Track == o.Track && Index == o.Index && ReferenceEquals(Selection, o.Selection);
        public override int GetHashCode() => HashCode.Combine(Album.Slot, Track.Slot, Index);
    }

    /// <summary>One drawer row — its own component so a play / pause / like anywhere re-skins THIS row only. Single click
    /// selects (Ctrl toggles, Shift extends), double click plays, right-click / the "…" opens the selection-aware menu,
    /// the row drags the selection when the gesture starts on a selected row.</summary>
    sealed class DrawerRowHost : Component
    {
        /// <summary>The drawer's lanes: # 26 · ♥ · Title★ · time 44 · "…" 32 — no thumb, Plays, Album, Date or Video.</summary>
        static readonly TrackSize[] s_tracks =
            [TrackSize.Px(26f), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(Track.Lane.TitleStar), TrackSize.Px(44f), TrackSize.Px(32f)];
        static readonly Track.ColumnSet s_set =
            new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: true, Thumb: false, Actions: true);
        static readonly Func<int, int> s_identityIndex = static i => i;

        DrawerRowProps? _p;
        IOverlayService? _overlay;
        Signal<bool>? _hovered;
        DragSource? _dragSource;
        int _likeSlot;
        bool _likeSaved;
        readonly Action _play, _like, _exit;
        readonly Action<PointerEventArgs> _released;
        readonly Action<Point2> _hoverMove;
        readonly Func<float> _pill;
        readonly Func<object?> _drag;
        readonly Func<ContextMenuModel?> _menu;
        readonly Func<int, Track> _trackAt;

        public DrawerRowHost()
        {
            _play = Play;
            _like = Like;
            _released = Released;
            _hoverMove = _ => { if (_hovered is { } h && !h.Peek()) h.Value = true; };
            _exit = () => { if (_hovered is { } h && h.Peek()) h.Value = false; };
            _pill = () => _p is { } p && p.Selection.Version.Value >= 0 && p.Selection.IsSelected(p.Index) ? 1f : 0f;
            _drag = DragPayloadNow;
            _menu = MenuNow;
            _trackAt = TrackAt;
        }

        public override Element Render()
        {
            var p = UseProps<DrawerRowProps>();
            _p = p;
            var hovered = UseSignal(false);
            _hovered = hovered;
            var overlay = UseContext(Overlay.Service);
            _overlay = overlay;
            _ = Entities.Current.Tracks.Changed.Value;

            var t = p.Track;
            var st = Track.StateOf(t);
            bool pop = Track.LikeEdge(ref _likeSlot, ref _likeSaved, t, st.Saved);
            var set = s_set;
            Element title = new TextEl(t.Title)
            {
                Size = 13f, Weight = 600, Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
            var options = new Track.GridOptions(OnPlay: _play, OnLike: _like, LikePop: pop,
                ActionsCell: Track.MoreCell(true, false), HoverPaused: hovered, Accent: p.Accent);
            Element grid = Track.Grid(t, p.Index, in st, in set, s_tracks, DrawerRowContentH, title, in options);

            _dragSource ??= Drag.Source(_drag);
            BoxEl row = new BoxEl
            {
                ZStack = true, Height = DrawerVerdict.RowPitch, ClipToBounds = true, Corners = Radii.ControlAll,
                Fill = ColorF.Transparent, HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                Draggable = _dragSource, OnPointerReleased = _released,
                OnHoverMove = _hoverMove, OnPointerExit = _exit,
                Children =
                [
                    new BoxEl { Direction = 1, Height = DrawerVerdict.RowPitch, Justify = FlexJustify.Center, Children = [grid] },
                    // The 3×16 accent selection pill: always mounted, revealed by a bound opacity (compositor-only).
                    new BoxEl
                    {
                        Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f), AlignSelf = FlexAlign.Center,
                        Corners = CornerRadius4.All(1.5f), Fill = p.Accent(), HitTestVisible = false, Opacity = _pill,
                    },
                ],
            };
            return Controls.IsNullOverlay(overlay) ? row : ContextMenu.Attach(row, overlay, _menu);
        }

        void Play()
        {
            if (_p is not { } p) return;
            var t = p.Track;
            // The ONE not-yet-out predicate refuses here exactly as the table's activation funnel does.
            if (!t.IsValid || t.NotYetOut(Store.ToUnix(Entities.Now))) return;
            var album = p.Album;
            Track.Invoke(t, () => Playback.PlayContext(album.Id, t.Id));
        }

        void Like()
        {
            if (_p is not { } p) return;
            var me = User.Me;
            if (me.Slot <= 0 || !p.Track.IsValid) return;
            if (me.Likes(p.Track)) me.Unlike(p.Track); else me.Like(p.Track);
        }

        void Released(PointerEventArgs args)
        {
            if (_p is not { } p || args.Button != 0) return;
            if (args.ClickCount >= 2) { Play(); return; }
            bool ctrl = (args.Mods & KeyModifiers.Ctrl) != 0, shift = (args.Mods & KeyModifiers.Shift) != 0;
            p.Selection.OnInteractedAction(p.Index, ctrl, shift);
            if (!shift) p.Selection.AnchorIndex = p.Index;
        }

        Track TrackAt(int i)
        {
            if (_p is not { } p) return default;
            var slots = p.Album.TrackSlots;
            return (uint)i < (uint)slots.Length ? new Track(slots[i]) : default;
        }

        ContextMenuModel? MenuNow()
            => _p is { } p
                ? Track.RowMenu(p.Selection, p.Index, _trackAt, s_identityIndex,
                                new Track.MenuOptions(ShowGoToAlbum: true, PickerOverlay: _overlay))
                : null;

        /// <summary>The whole selection when the gesture starts on a selected row, else that one track (a COPY source).</summary>
        object? DragPayloadNow()
        {
            if (_p is not { } p || !p.Track.IsValid) return null;
            Track[] tracks;
            if (p.Selection.IsSelected(p.Index) && p.Selection.SelectedCount > 1)
            {
                var slots = p.Album.TrackSlots;
                var picked = new List<Track>(p.Selection.SelectedCount);
                for (int i = 0; i < slots.Length && i < p.Selection.ItemCount; i++)
                    if (p.Selection.IsSelected(i)) picked.Add(new Track(slots[i]));
                tracks = picked.ToArray();
            }
            else tracks = [p.Track];
            if (tracks.Length == 0) return null;
            var first = tracks[0];
            string uri = first.Uri.Text;
            return new DragPayload(DragKind.Track, uri, uri, first.Title, new EntityRef(EntityKind.Track, first.Slot), Tracks: tracks);
        }
    }

    // ══ 5. THE LIBRARY PANE STATICS (library rework §5.5, wave L1) ════════════════════════════════════════════════════
    //
    // What `Album.Pane` (and `Artist.Reader`, which paints the same commands over one release block) calls: the hero ⋯ as
    // a function of a HANDLE, the 36-px command circle, and the pane's header + command row. Every one is a VALUE over its
    // arguments — no hooks, no signals, nothing read but the album it is handed — so a selection change re-skins both
    // panes in place and neither remounts. Accent-NEUTRAL: nothing here reads a palette (ch 15 §0.8).

    /// <summary>The pane covers' one edge — 128, the show twin's too (<c>Show.PaneHeader</c>).</summary>
    public const float PaneCover = 128f;

    /// <summary>The pane header's STATED height: the cover plus its own padding (128 + 20 + 12 = 160). Stated, not
    /// content-derived, because a content-derived header is a header that MOVES: the text column reaches past the cover
    /// the moment a title wraps to two lines, and everything under it — the command row, the column header, the first
    /// row — slid ~9 DIP down on those albums and back up on the next one. With one number the rows below start on the
    /// same DIP for every release, and the column that has to fit inside it is sized to fit (see
    /// <see cref="PaneHeader"/>'s own note).</summary>
    public const float PaneHeaderHeight = PaneCover + Spacing.XL + Spacing.M;

    // ── the header's TYPE BUDGET ──────────────────────────────────────────────────────────────────────────────────────
    // A stated height only helps if what goes in it FITS: an overflowing column would move the clipping instead of the
    // layout. So the four lines and the gaps between them are numbers, `PaneHeader` lays itself out FROM those numbers,
    // and `PaneHeaderTextHeight` adds them up — which makes "a two-line title cannot push the tracklist down" a pure
    // assertion (AlbumPageRulesTests) rather than a screenshot. Raising the title back to the prototype's 28/34 now
    // fails that test instead of shipping a 9-DIP jump.

    /// <summary>Between the header's text lines.</summary>
    public const float PaneHeaderGap = 3f;
    /// <summary>The eyebrow's line box — <c>Design.Type.Eyebrow</c> is <c>Ui.Caption</c>, 12 / 16.</summary>
    public const float PaneHeaderEyebrowLine = 16f;
    /// <summary>The title link. 24 / 30 and not the prototype's 28 / 34: at 34 two lines alone spent 129 of the 128 the
    /// cover leaves. A one-line title — the common case — reads the same at either size.</summary>
    public const float PaneTitleSize = 24f;
    /// <inheritdoc cref="PaneTitleSize"/>
    public const float PaneTitleLine = 30f;
    /// <summary>The title wraps to two lines and ellipsizes after them.</summary>
    public const int PaneTitleMaxLines = 2;
    /// <summary>The attribution's line box — <c>Detail.ArtistLine</c> sets its names 14 / 20.</summary>
    public const float PaneHeaderAttributionLine = 20f;
    /// <summary>The meta line, 12.5 / 16.</summary>
    public const float PaneHeaderMetaSize = 12.5f;
    /// <inheritdoc cref="PaneHeaderMetaSize"/>
    public const float PaneHeaderMetaLine = 16f;

    /// <summary>What the block leaves its text column: everything it states, less its own padding — the cover's 128.</summary>
    public const float PaneHeaderTextBudget = PaneHeaderHeight - Spacing.XL - Spacing.M;

    /// <summary>The text column's height for a title of <paramref name="titleLines"/> lines: the four line boxes and the
    /// three gaps. The title link's 2-DIP inset is cancelled by its own negative margin, so it contributes nothing.</summary>
    public static float PaneHeaderTextHeight(int titleLines)
        => PaneHeaderEyebrowLine + PaneTitleLine * Math.Clamp(titleLines, 1, PaneTitleMaxLines)
           + PaneHeaderAttributionLine + PaneHeaderMetaLine + 3f * PaneHeaderGap;

    /// <summary>The pane's ⋯ (the album page's W20 hero menu over a handle), built at OPEN from the live model:
    /// Add to playlist ▸ (the track menu's own deposit submenu over the album's rows) · Play next · Add to queue (the
    /// container verbs). No owner rows on an album. Null when nothing is offerable — the caller draws no menu.</summary>
    // TODO(library-rework): Album.Page.MoreMenu() duplicates this; fold when that file is free.
    public static ContextMenuModel? MoreMenu(Album a, IOverlayService? overlay)
    {
        if (!a.IsValid) return null;
        var slots = a.TrackSlots;
        var rows = new List<MenuFlyoutItem>(3);
        if (slots.Length > 0)
        {
            var tracks = new Track[slots.Length];
            for (int i = 0; i < tracks.Length; i++) tracks[i] = new Track(slots[i]);
            if (Track.Menu(tracks, new Track.MenuOptions(ShowGoToAlbum: false, PickerOverlay: overlay)) is { } model)
            {
                string add = Loc.Get(Strings.Detail.AddToPlaylist);
                for (int i = 0; i < model.Rows.Count; i++)
                    if (string.Equals(model.Rows[i].Label, add, StringComparison.Ordinal)) { rows.Add(model.Rows[i]); break; }
            }
        }
        var ctx = new ActionContext(ActionTarget.ForAlbum(a.Uri, a.Title), Actions.Services);
        if (Actions.Menu.Row(ActionId.PlayContextNext, in ctx) is { } next) rows.Add(next);
        if (Actions.Menu.Row(ActionId.AddContextToQueue, in ctx) is { } queue) rows.Add(queue);
        return rows.Count == 0 ? null : new ContextMenuModel(rows);
    }

    /// <summary>A 36-px subtle circle with a glyph and a tooltip name — the pane's and the reader's secondary verb. The
    /// rest face is the recipe's (<c>Interactive</c> OWNS Fill: transparent at rest, subtle on hover — the same ghost
    /// circle the detail rail's FAB is), and the Standard scale tier answers the press.</summary>
    public static Element CommandCircle(string glyph, string name, Action tap) => Controls.Named(new BoxEl
    {
        Width = 36f, Height = 36f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(36f),
        HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = tap,
        Children = [Icon(glyph, 16f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle), name);

    /// <summary>Cover 128 (r 6, <c>Elevation.Card</c>) · eyebrow · the title LINK 24/30/600 (2 lines, accent on hover) ·
    /// the attribution (<c>Detail.ArtistLine(id.Artists)</c>, or a show's publisher line) · meta 12.5/16.
    /// <c>AlignItems End</c>: the text block sits on the cover's baseline, the prototype's stance.
    /// <para>The block is <see cref="PaneHeaderHeight"/> tall, ALWAYS, and the text column is budgeted to fit inside the
    /// 128 the cover leaves: 16 eyebrow + 60 title (two 30-DIP lines; the 2-DIP link inset is cancelled by its own
    /// negative margin) + 20 attribution + 16 meta + three 3-DIP gaps = 121. That budget is why the title is 24/30 and
    /// not the prototype's 28/34 — at 34 a two-line title alone put the column at 129 and pushed the whole tracklist
    /// down. A one-line title is the common case and reads the same; a wrapped one now costs nothing below it. The
    /// <c>MinHeight 0</c> / <c>ClipToBounds</c> on the column and on the block are the BACKSTOP for a locale whose
    /// metrics overrun the budget anyway: it clips, it never reflows.</para></summary>
    public static Element PaneHeader(string? cover, string eyebrow, string title, Action open, Element attribution, string meta) => new BoxEl
    {
        Direction = 0, Gap = 18f, AlignItems = FlexAlign.End, Shrink = 0f,
        Height = PaneHeaderHeight, ClipToBounds = true,
        Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
        Children =
        [
            new BoxEl
            {
                Width = PaneCover, Height = PaneCover, Shrink = 0f, Corners = Radii.CardAll, ClipToBounds = true, Shadow = Elevation.Card,
                Children = [Controls.Artwork(cover, PaneCover, PaneCover, Radii.Card, decodePx: 256)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = PaneHeaderGap, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                Children =
                [
                    Design.Type.Eyebrow(eyebrow) with { Color = Tok.TextTertiary },
                    new BoxEl
                    {
                        Corners = Radii.ControlAll, Direction = 1,
                        Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS),
                        Margin = new Edges4(-Spacing.S, -Spacing.XXS, -Spacing.S, -Spacing.XXS),
                        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = open,
                        Children =
                        [
                            new TextEl(title)
                            {
                                Size = PaneTitleSize, LineHeight = PaneTitleLine, Weight = 600,
                                Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                                BrushTransitionMs = Design.Motion.Faster, MaxLines = PaneTitleMaxLines,
                                Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                            },
                        ],
                    }.Interactive(Interaction.Subtle),
                    attribution,
                    new TextEl(meta)
                    {
                        Size = PaneHeaderMetaSize, LineHeight = PaneHeaderMetaLine, Color = Tok.TextTertiary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            },
        ],
    };

    /// <summary>Play (the SYSTEM accent — the one stated exception) · shuffle · save · more (36 circles each) · spacer ·
    /// "Open album ↗" (a 13-px accent text link with the OpenInNewWindow glyph — a hyperlink, not a capsule). Never a
    /// second accent CTA (ch 15 §0.8). <paramref name="save"/> and <paramref name="more"/> are the caller's hosts (the
    /// SaveButton is keyed per uri, the ⋯ needs the overlay), so this row stays a pure value.</summary>
    public static Element PaneCommands(Action play, Action shuffle, Element save, Element more, Action open) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, Shrink = 0f,
        Padding = new Edges4(Spacing.XL, Spacing.M, Spacing.XL, Spacing.S),
        Children =
        [
            Controls.Play(Tok.AccentDefault, play),
            CommandCircle(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), shuffle),
            save, more,
            new BoxEl { Grow = 1f },
            new BoxEl
            {
                Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Corners = Radii.ControlAll, Shrink = 0f,
                Padding = new Edges4(Spacing.S, 6f, Spacing.S, 6f),
                Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Library.OpenAlbum))
                    {
                        Size = 13f, LineHeight = 18f, Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary,
                    },
                    Icon(Icons.OpenInNewWindow, 14f, Tok.AccentTextPrimary),
                ],
            },
        ],
    };
}
