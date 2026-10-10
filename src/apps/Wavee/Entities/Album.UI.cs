// ── Entities/Album.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the album surface's self-subscribing components: the billed-artist FACE PILE and its every-artist flyout (ch 05 §0.5,
// W16, §6), the prerelease COUNTDOWN card in its three states incl. Bare (W13, §5's clock rule) and the trailing section
// STACK (W12: capped at 5, one-way "Show all N") — plus the LIBRARY PANE STATICS (§4, library rework §5.5): the hero ⋯
// menu over a handle, the 36-px command circle, PaneHeader and PaneCommands, which Album.Pane and Artist.Reader paint
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
                // "Show all" lengthens the box in place — the new height reveals.
                Children = [header, new BoxEl { Direction = 1, Gap = Spacing.XS, Animate = Design.Reveal.Resize, Children = rows }],
            };
        }
    }

    // ══ 4. THE LIBRARY PANE STATICS (library rework §5.5, wave L1) ════════════════════════════════════════════════════
    //
    // What `Album.Pane` (and `Artist.Reader`, which paints the same commands over one release block) calls: the hero ⋯ as
    // a function of a HANDLE, the 36-px command circle, and the pane's header + command row. Every one is a VALUE over its
    // arguments — no hooks, no signals, nothing read but the album it is handed — so a selection change re-skins both
    // panes in place and neither remounts. Accent-NEUTRAL: nothing here reads a palette (ch 15 §0.8).

    /// <summary>The pane covers' one edge — 128, the show pane's too (it renders this same header).</summary>
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

    /// <summary>The pane's and the reader's secondary verb: the standard 32/r4 <see cref="Controls.IconAction"/>, named
    /// by its tooltip.
    /// <para><c>// Workstream B</c>: was a 36-px subtle CIRCLE — the detail rail's round FAB shape. Every non-media icon
    /// affordance is now this same square ladder, so a pane's Shuffle and the rail's satellites read as one grammar.</para></summary>
    public static Element CommandCircle(string glyph, string name, Action tap)
        => Controls.Named(Controls.IconAction(glyph, tap), name);

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
        // ONE target: cover + eyebrow + title + attribution + meta open the album (the show pane's: the show).
        // A title-only hyperlink left the cover inert in the library pane.
        Direction = 0, Gap = 18f, AlignItems = FlexAlign.End, Shrink = 0f,
        Height = PaneHeaderHeight, ClipToBounds = true,
        Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
        Corners = Radii.CardAll, Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = open,
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
                    new TextEl(title)
                    {
                        Size = PaneTitleSize, LineHeight = PaneTitleLine, Weight = 600,
                        Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                        BrushTransitionMs = Design.Motion.Faster, MaxLines = PaneTitleMaxLines,
                        Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    attribution,
                    // The SAME rung for the show pane, which renders this header: "one geometry for both kinds, so
                    // a selection crossing album -> show moves nothing but the words" (that file's header, and this
                    // one's). A typography alias here made the album pane's meta 13/18 secondary against the show
                    // pane's 12.5/16 tertiary, and put PaneHeaderTextHeight's arithmetic 2 DIP out.
                    new TextEl(meta)
                    {
                        Size = PaneHeaderMetaSize, LineHeight = PaneHeaderMetaLine, Color = Tok.TextTertiary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary><see cref="PaneHeader"/>'s loading twin, for the album pane AND the show pane (both render <see cref="PaneHeader"/>,
    /// rung for rung): the cover square and three stand-in bars — eyebrow, title, attribution — in the SAME
    /// stated <see cref="PaneHeaderHeight"/>, padding, gap and bottom alignment, so the crossfade into the real header is a
    /// dissolve and never a reflow. The header's STATED height, not this tree's content height, is the point: the two must
    /// occupy the same band. Inert — no click, no role, no focus (a header that cannot say what it opens offers nothing).</summary>
    public static BoxEl PaneHeaderSkeleton() => new()
    {
        Direction = 0, Gap = 18f, AlignItems = FlexAlign.End, Height = PaneHeaderHeight, ClipToBounds = true,
        Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
        Children =
        [
            new BoxEl { Width = PaneCover, Height = PaneCover, Shrink = 0f, Corners = Radii.CardAll, Fill = Tok.FillSubtleSecondary },
            new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [Controls.PendingBar(80f, 11f), Controls.PendingBar(220f, 26f), Controls.PendingBar(120f, 11f)],
            },
        ],
    };

    /// <summary>Play (the SYSTEM accent — the one stated exception) · shuffle · save · more (32/r4 icon actions each) ·
    /// spacer · "Open album ↗" (a 13-px accent text link with the OpenInNewWindow glyph — a hyperlink, not a capsule).
    /// Never a second accent CTA (ch 15 §0.8). <paramref name="save"/> and <paramref name="more"/> are the caller's
    /// hosts (the SaveButton is keyed per uri, the ⋯ needs the overlay), so this row stays a pure value.
    /// <para>The link is the row's one SHRINKING child (#158): the verbs before it are ~300 DIP that never shrink, and
    /// with the link `Shrink 0` too a pane under ~400 DIP clipped it off the edge. Now the spacer gives way first, then
    /// the label ellipsizes, and a trimmed label — only a trimmed one — gets its whole text as a tooltip
    /// (<c>Controls.TrimTip</c>). The tooltip wraps the LABEL, in a column slot of its own, rather than the whole link:
    /// that slot is exactly the text's box, so the trim test compares like with like (around the link it would measure
    /// a box 36 DIP wider than the text — padding, gap and glyph), and a ToolTip wrapper is `Shrink 0`, so it can only
    /// be sized as a COLUMN child (the engine's rule 11).</para></summary>
    public static Element PaneCommands(Action play, Action shuffle, Element save, Element more, Action open)
    {
        string openLabel = Loc.Get(Strings.Library.OpenAlbum);
        TextEl label = Design.Type.DenseMeta(openLabel) with
        {
            Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, Shrink = 0f,
            Padding = new Edges4(Spacing.XL, Spacing.M, Spacing.XL, Spacing.S),
            Children =
            [
                Controls.PlayButton(Tok.AccentDefault, play),
                CommandCircle(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), shuffle),
                save, more,
                new BoxEl { Grow = 1f },
                new BoxEl
                {
                    Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Corners = Radii.ControlAll, Shrink = 1f, MinWidth = 0f,
                    Padding = new Edges4(Spacing.S, 6f, Spacing.S, 6f),
                    Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, BrushTransitionMs = Design.Motion.Faster,
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children =
                    [
                        new BoxEl { Direction = 1, Shrink = 1f, MinWidth = 0f, Children = [Controls.TrimTip(label, openLabel, label)] },
                        Icon(Icons.OpenInNewWindow, 14f, Tok.AccentTextPrimary),
                    ],
                },
            ],
        };
    }
}
