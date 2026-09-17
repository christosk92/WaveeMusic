// ── Entities/Home.Artists.UI.cs ────────────────────────────────────────────────────────────────────────────────────
// the top-artist podium, its disclosure (top tracks | Mixview) and the Mixview ring / spine
//
// Role: UI
// Owner: P (stream P3)
// Wave: 5
// Budget: 650 lines
// Spec: ch 11 §1.1 E, W13-W17, §8 HomeArtistRowLayout, §10 podium items 21-30 (ported from 0.2.9
//       Features/Home/HomeModules.Artists.cs + HomeCards.RankedAvatar)
//
// DATA: the ranking is `Edges.UserTopArtists` on `Scope.MeSlot` (the ORDER IS THE RANK), the "In your top N" badge
// `Edges.UserTopTracks`, the demand `Home.Feeds.EnsureTopContent()`. The HUB's rows are `Edges.ArtistPopular`, its Mixview
// list `Edges.ArtistRelated` (one `artistOverview`); a warm artist paints at once, a cold one shows row bones (W17).
// D17: NEVER nest a `Responsive.Of` here (it froze hub/go at first mount); row and panel call `UseMeasuredWidth(4f)` and
// the row pushes its hysteresis TIER down. Selection and hub are artist SLOTS; the hub is LIFTED into the row (#83) and
// reaches `MixviewPanel` by props (keyed on the selection) through a stable forwarder. Delegates are cached per strip
// index. `Environment.TickCount64` is the podium's double-click INPUT timing, the one sanctioned read (no motion).

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Home
{
    /// <summary>The top-artist podium + disclosure + Mixview (ch 11 E). Mounted by the landing on <c>HomeRow.Artists</c>;
    /// renders pod-shaped bones while the ranking is unanswered and NOTHING once it answered empty.</summary>
    public static Element ArtistsRow() => Embed.Comp(static () => new ArtistPodiumRow());

    // ══ 1. THE ROW ═══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class ArtistPodiumRow : Component
    {
        const float PodChrome = Spacing.S;      // the "+8" a pod's own `w = max(art + 8, 60)` adds
        const int MaxPods = 16, TopTrackRows = 5, SkeletonPods = 10;

        // The canonical track cell (# · ♥ · art · title · plays · duration · badge + "…"): the SAME eager row the artist
        // page and Recents render, so a track on Home behaves like a track everywhere. The prototype's 34-px meter row
        // does not survive the shared cell (nothing renders a track under 40, the heart and "…" are 28-DIP targets).
        static readonly Track.ColumnSet TrackCols = new(Album: false, By: false, Date: false, Video: false,
                                                        Plays: true, Heart: true, Thumb: true);
        static readonly Track.ColumnSet TrackColsNoArt = TrackCols with { Thumb = false };
        static readonly TrackSize[] TrackColumns =
            [TrackSize.Px(36f), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Px(Track.RowMetrics.ThumbSize), TrackSize.Star(1f),
             TrackSize.Px(84f), TrackSize.Px(52f), TrackSize.Px(160f)];
        static readonly TrackSize[] TrackColumnsNoArt =
            [TrackSize.Px(36f), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(1f),
             TrackSize.Px(84f), TrackSize.Px(52f), TrackSize.Px(160f)];

        readonly int[] _slots = new int[MaxPods];
        readonly int[] _rowSlots = new int[TopTrackRows];
        readonly Action[] _podClicks = new Action[MaxPods];
        readonly Func<ContextMenuModel?>[] _podMenus = new Func<ContextMenuModel?>[MaxPods];
        readonly DragSource?[] _podDrags = new DragSource?[MaxPods];
        readonly Action[] _rowPlays = new Action[TopTrackRows];
        int _count, _selected, _hubSlot;
        bool _forward = true, _tierMeasured;
        int _tier = HomeArtistRowLayout.TierWide;
        string? _lastPodUri;
        long _lastPodTick;
        Action<int>? _setSelected, _setHub, _hubForward;
        Signal<bool>? _askedTop;
        Action? _ensureTop, _ensureRanked, _ensureHub, _playHub;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var measured = UseMeasuredWidth(4f);
            var (selected, setSelected) = UseState(Table.None);
            var (hub, setHub) = UseState(Table.None);
            var askedTop = UseSignal(false);
            _setSelected = setSelected;
            _setHub = setHub;
            _askedTop = askedTop;
            // STABLE across renders: the delegate is created once and always calls the LATEST setter.
            Action<int> onHubChanged = _hubForward ??= slot => _setHub?.Invoke(slot);

            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var edges = scope.Edges;
            _ = edges.UserTopArtists.Changed.Value;
            _ = edges.UserTopTracks.Changed.Value;
            _ = edges.ArtistPopular.Changed.Value;
            _ = scope.Artists.Changed.Value;
            var load = Feeds.TopContentState.Value;
            int me = scope.MeSlot;

            // The demand, once per scope. The signal write re-renders, so an Idle host (offline) concludes to nothing
            // instead of shimmering forever.
            UseEffect(_ensureTop ??= EnsureTop, DepKey.From((long)epoch));

            ReadOnlySpan<int> ranked = me > Table.None ? edges.UserTopArtists.Targets(me) : default;
            _count = Math.Min(ranked.Length, MaxPods);
            ranked[.._count].CopyTo(_slots);
            uint rankVersion = me > Table.None ? edges.UserTopArtists.Version(me) : 0u;
            UseEffect(_ensureRanked ??= EnsureRanked, DepKey.From((long)rankVersion, (long)epoch));

            // A selection whose artist left the ranking closes; the hub only means something while a pod is open.
            int sel = IndexOf(selected) >= 0 ? selected : Table.None;
            int effectiveHub = sel > Table.None && hub > Table.None ? hub : sel;
            _selected = sel;
            _hubSlot = effectiveHub;
            uint popV = effectiveHub > Table.None ? edges.ArtistPopular.Version(effectiveHub) : 0u;
            uint relV = effectiveHub > Table.None ? edges.ArtistRelated.Version(effectiveHub) : 0u;
            UseEffect(_ensureHub ??= EnsureHub, DepKey.From(effectiveHub, (int)popV, (int)relV, (int)epoch));

            float width = measured.Value;
            float w = width > 0.5f ? width : HomeModuleLayout.FallbackWidth;
            if (width > 0.5f) { _tier = HomeArtistRowLayout.TierFor(width, _tier, _tierMeasured); _tierMeasured = true; }
            else if (!_tierMeasured) _tier = HomeArtistRowLayout.NominalTierFor(w);

            if (me <= Table.None) return new BoxEl();
            var state = edges.UserTopArtists.Readiness(me);
            if (state is not (EdgeState.Complete or EdgeState.Partial))
            {
                bool pending = state != EdgeState.Failed && load != HomeLoad.Failed
                               && (load == HomeLoad.Pending || !askedTop.Value);
                return pending ? Frame(null, SkeletonPodium(w), w) : new BoxEl();
            }
            if (_count == 0) return new BoxEl();

            // #82 — the ramp is a function of the measured width: Fit solves the per-column width forced to exactly the
            // pod count (the podium shows every artist, nothing virtualizes), and the 76/60/46 ramp scales around it.
            var (scale, slotH) = Ramp(w, _count);
            IOverlayService? host = Controls.IsNullOverlay(overlay) ? null : overlay;
            var strip = new Element[_count];
            for (int i = 0; i < _count; i++)
            {
                // The ring follows the HUB, not the raw pick — re-centring onto another top artist lights that pod.
                var pod = Pod(new Artist(_slots[i]), i + 1, _slots[i] == effectiveHub,
                              HomeArtistRowLayout.ArtSize(i, scale), slotH, PodClick(i))
                          with { Key = "home-topartist:" + _slots[i].ToString(CultureInfo.InvariantCulture), Draggable = PodDrag(i) };
                strip[i] = host is null ? pod : pod.WithContextMenu(host, PodMenu(i));
            }

            Element podium = new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Spacing.S, MinWidth = 0f, Padding = Edges4.All(Spacing.M), Children = strip,
            };
            Element body = sel <= Table.None
                ? podium
                : new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        podium,
                        new BoxEl
                        {
                            Key = "home-artist-disclosure:" + sel.ToString(CultureInfo.InvariantCulture),
                            Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
                            Direction = 1, MinWidth = 0f,
                            Children =
                            [
                                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                                Disclosure(effectiveHub, sel, me, onHubChanged),
                            ],
                        },
                    ],
                };
            return Frame(Strings.Home.TopArtistsSub(_count), body, w);
        }

        /// <summary>Header + the ONE card (podium, and the disclosure below a divider; its height tweens) + this row's OWN
        /// bottom gap (<see cref="HomeArtistRowLayout.ModuleGap"/>, 32/24 — not the shared 40/32).</summary>
        static Element Frame(string? subtitle, Element body, float w) => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, HomeArtistRowLayout.ModuleGap(w)),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = HomeModuleLayout.HeadGap, MinWidth = 0f,
                    Children =
                    [
                        HomeModules.ModuleHeader(Loc.Get(Strings.Home.TopArtists), subtitle, null, null),
                        new BoxEl
                        {
                            Direction = 1, MinWidth = 0f, ClipToBounds = true,
                            Animate = MotionRecipes.CardResizeHeight,
                            Corners = Radii.CardAll, Fill = Tok.FillCardDefault,
                            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                            Children = [body],
                        },
                    ],
                },
            ],
        };

        static (float Scale, float Slot) Ramp(float w, int count)
        {
            float contentW = MathF.Max(0f, w - 2f * Spacing.M);
            var (_, podW) = FillRowVirtualLayout.Fit(contentW, HomeArtistRowLayout.BaseArtSize(0) + PodChrome, 9999f, Spacing.S,
                                                     perPageOverride: count);
            float scale = HomeArtistRowLayout.RampScaleFor(podW, count, PodChrome);
            // Every pod reserves the TALLEST avatar's height, so all labels land on one line (no staircase under wrap).
            return (scale, HomeArtistRowLayout.ArtSize(0, scale));
        }

        /// <summary>The unanswered podium: ten pod-shaped bones at the same ramp, so the landing never jumps on arrival.</summary>
        static Element SkeletonPodium(float w)
        {
            var (scale, slotH) = Ramp(w, SkeletonPods);
            var pods = new Element[SkeletonPods];
            for (int i = 0; i < SkeletonPods; i++)
            {
                float art = HomeArtistRowLayout.ArtSize(i, scale);
                float podW = MathF.Max(art + Spacing.S, 60f);
                pods[i] = new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.Center, Width = podW,
                    Padding = new Edges4(Spacing.XXS, Spacing.S, Spacing.XXS, Spacing.S), HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = slotH, Width = podW, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                            Children = [Bone(art, art, art / 2f)],
                        },
                        Bone(MathF.Min(podW, 56f), 12f, 4f),
                    ],
                };
            }
            return new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, MinWidth = 0f, Padding = Edges4.All(Spacing.M), Children = pods };
        }

        static BoxEl Bone(float w, float h, float radius) => new()
        {
            Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(radius),
            Fill = SkeletonStyle.Default.BarColor, HitTestVisible = false, IsEnabled = false,
        };

        // ── the disclosure (W15 / W16 / W17) ─────────────────────────────────────────────────────────────────────────

        /// <summary><c>.expander</c> — top tracks | 1-DIP divider | the 342-DIP Mixview pane at tier Wide; stacked below
        /// it. The tier is the row's, pushed in — no Responsive boundary here (D17).</summary>
        Element Disclosure(int hub, int owner, int me, Action<int> onHubChanged)
        {
            Element left = TopTracks(hub, me);
            Element right = Embed.Comp(new MixviewProps(hub, _tier, onHubChanged), static () => new MixviewPanel())
                            with { Key = "mixview:" + owner.ToString(CultureInfo.InvariantCulture) };
            return _tier == HomeArtistRowLayout.TierWide
                ? new BoxEl
                {
                    Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Children =
                    [
                        new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [left] },
                        new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                        new BoxEl { Direction = 1, Width = 342f, Shrink = 0f, MinWidth = 0f, Children = [right] },
                    ],
                }
                : new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = [left, new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }, right],
                };
        }

        /// <summary><c>.exp-l</c> — a head (the HUB's facts and Play), then five eager track rows; five 48-DIP row bones
        /// while the overview is unanswered (never a spinner, never an empty pane).</summary>
        Element TopTracks(int hub, int me)
        {
            var edges = Entities.Current.Edges;
            var artist = new Artist(hub);
            bool art = !Prefs.Appearance.TrackArtworkHidden();
            var popular = edges.ArtistPopular.Targets(hub);
            int rows = Math.Min(popular.Length, TopTrackRows);
            bool pending = rows == 0 && edges.ArtistPopular.Readiness(hub) == EdgeState.Unknown;
            int topCount = me > Table.None ? edges.UserTopTracks.Count(me) : 0;
            string facts = Facts(artist);

            var kids = new Element[1 + (rows > 0 ? rows : pending ? TopTrackRows : 0)];
            kids[0] = new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    BodyStrong(Loc.Get(Strings.Home.TopTracks)) with { MaxLines = 1, Shrink = 0f },
                    facts.Length > 0
                        ? Caption(facts) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f }
                        : new BoxEl(),
                    new BoxEl { Grow = 1f, MinWidth = 0f },
                    Controls.Pill(Loc.Get(Strings.Home.Play), _playHub ??= PlayHub, ButtonAppearance.Standard, glyph: Icons.Play),
                ],
            };
            for (int i = 0; i < rows; i++)
            {
                var t = new Track(popular[i]);
                _rowSlots[i] = t.Slot;
                bool inTop = topCount > 0 && edges.UserTopTracks.IndexOf(me, t.Slot) >= 0;
                // `i`, not `i + 1`: the row renders DisplayIndex + 1.
                kids[1 + i] = Track.EagerRow(t, i, art ? TrackCols : TrackColsNoArt, art ? TrackColumns : TrackColumnsNoArt,
                                  Track.RowMetrics.RowHeight, RowPlay(i),
                                  new Track.EagerRowOptions(ShowTrackArtist: false, ActionsCell: TrackActions(inTop, topCount)))
                              with { Key = "home-toptrack:" + t.Slot.ToString(CultureInfo.InvariantCulture) + (art ? ":art" : ":noart") };
            }
            if (rows == 0 && pending)
                for (int i = 0; i < TopTrackRows; i++)
                    kids[1 + i] = new BoxEl
                    {
                        Direction = 1, Height = Track.RowMetrics.RowHeight, MinWidth = 0f, Justify = FlexJustify.Center,
                        Children =
                        [
                            new BoxEl
                            {
                                Height = 14f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(4f),
                                Fill = SkeletonStyle.Default.BarColor, HitTestVisible = false, IsEnabled = false,
                            },
                        ],
                    };

            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Padding = Edges4.All(Spacing.M), Children = kids };
        }

        /// <summary>[in-your-top badge] + "…". The success green is SEMANTIC, outside the accent budget.</summary>
        static Element TrackActions(bool inTop, int topCount) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = 0f,
            Children =
            [
                inTop
                    ? new BoxEl
                    {
                        Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                        Corners = CornerRadius4.All(Radii.Full), Fill = Tok.SystemFillSuccessBackground,
                        Children = [Design.Type.Eyebrow(Strings.Home.InYourTop(topCount)) with
                            { Color = Tok.SystemFillSuccess, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                    }
                    : new BoxEl(),
                Track.MoreCell(true, false),
            ],
        };

        /// <summary>"6.3M monthly listeners · #4 in the world" — each half only when stated (a rank of 0 is "not
        /// ranked", and "#0" states a fact that is not one).</summary>
        static string Facts(Artist a)
        {
            if (!a.IsValid) return "";
            string listeners = a.MonthlyListeners > 0
                ? HomeCardText.CompactNumber(a.MonthlyListeners) + " " + Loc.Get(Strings.Artist.MetaMonthly) : "";
            string rank = a.WorldRank > 0 ? Strings.Artist.WorldRank(a.WorldRank.ToString(CultureInfo.CurrentCulture)) : "";
            return listeners.Length > 0 && rank.Length > 0 ? listeners + " · " + rank
                 : listeners.Length > 0 ? listeners : rank;
        }

        // ── demand ───────────────────────────────────────────────────────────────────────────────────────────────────

        void EnsureTop()
        {
            Feeds.EnsureTopContent();
            if (_askedTop is { } asked && !asked.Peek()) asked.Value = true;
        }

        void EnsureRanked()
        {
            if (_count > 0) Entities.Ensure(MemoryMarshal.Cast<int, Artist>(_slots.AsSpan(0, _count)), ArtistFields.Identity);
        }

        /// <summary>The hub's overview (facts + popular + related, one <c>artistOverview</c>), then the rows it names.</summary>
        void EnsureHub()
        {
            int slot = _hubSlot;
            if (slot <= Table.None) return;
            Entities.Ensure(new Artist(slot), ArtistFields.Identity | ArtistFields.Stats);
            var e = Entities.Current.Edges;
            if (e.ArtistPopular.State(slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistPopular, slot);
            if (e.ArtistRelated.State(slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistRelated, slot);
            var popular = e.ArtistPopular.Targets(slot);
            if (popular.Length > 0)
                Entities.Ensure(MemoryMarshal.Cast<int, Track>(popular[..Math.Min(popular.Length, TopTrackRows)]), TrackFields.Row);
            var related = e.ArtistRelated.Targets(slot);
            if (related.Length > 0)
                Entities.Ensure(MemoryMarshal.Cast<int, Artist>(related[..Math.Min(related.Length, MixviewPanel.MaxNodes)]),
                                ArtistFields.Identity);
        }

        // ── input ────────────────────────────────────────────────────────────────────────────────────────────────────

        int IndexOf(int slot)
        {
            if (slot <= Table.None) return -1;
            for (int i = 0; i < _count; i++) if (_slots[i] == slot) return i;
            return -1;
        }

        Action PodClick(int index) => _podClicks[index] ??= () => OnPod(index);
        Func<ContextMenuModel?> PodMenu(int index) => _podMenus[index] ??= () => GoToArtistMenu(index < _count ? _slots[index] : Table.None);
        DragSource? PodDrag(int index) => _podDrags[index] ??= Drag.Source(() => index < _count ? DragOf(_slots[index]) : null);
        Action RowPlay(int index) => _rowPlays[index] ??= () => PlayRow(index);

        /// <summary>Left-click selects / closes (the surface's own gesture); a second click on the same pod inside the
        /// window opens the artist page instead (the menu is the primary route, this is the accelerator).</summary>
        void OnPod(int index)
        {
            if ((uint)index >= (uint)_count) return;
            int slot = _slots[index];
            string uri = new Artist(slot).Uri.Text;
            long now = Environment.TickCount64;
            if (HomeArtistRowLayout.IsDoubleClick(uri, _lastPodUri, _lastPodTick, now))
            {
                _lastPodUri = null;
                _lastPodTick = 0;
                OpenArtist(slot);
                return;
            }
            _lastPodUri = uri;
            _lastPodTick = now;
            int current = IndexOf(_selected);
            int next = slot == _selected ? Table.None : slot;
            _forward = next > Table.None && (current < 0 || index > current);
            _setSelected?.Invoke(next);
            _setHub?.Invoke(Table.None);   // re-hub onto the newly open pod (or nothing, once closed)
        }

        void PlayHub()
        {
            var a = new Artist(_hubSlot);
            if (a.IsValid) Playback.PlayContext(a.Id);
        }

        void PlayRow(int index)
        {
            var a = new Artist(_hubSlot);
            if (!a.IsValid || (uint)index >= TopTrackRows) return;
            Playback.PlayContext(a.Id, new Track(_rowSlots[index]).Id);
        }

        static DragPayload? DragOf(int slot)
        {
            var a = new Artist(slot);
            if (!a.IsValid || !a.Uri.IsValid) return null;
            string uri = a.Uri.Text;
            return new DragPayload(Drag.KindOf(EntityKind.Artist), uri, uri, a.Name, new EntityRef(EntityKind.Artist, slot),
                                   ArtUrl: Controls.ArtUrl(a.ImageId));
        }

        // ── E · the pod (0.2.9 HomeCards.RankedAvatar) ───────────────────────────────────────────────────────────────

        /// <summary><c>.pod</c> — [a slot as tall as the LARGEST avatar holding the art bottom-aligned, the rank plate on
        /// the art's own top-left] over a centred 2-line Caption 600 label, at width <c>max(art + 8, 60)</c>. Selected (=
        /// the hub): a 3-DIP accent ring with the art inset BY HAND (BorderWidth has no layout effect).</summary>
        static BoxEl Pod(Artist a, int rank, bool selected, float artSize, float slotHeight, Action onSelect)
        {
            float w = MathF.Max(artSize + Spacing.S, 60f);
            float inner = selected ? artSize - 6f : artSize;
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.Center, Width = w,
                Padding = new Edges4(Spacing.XXS, Spacing.S, Spacing.XXS, Spacing.S),
                Corners = Radii.ControlAll,
                OnClick = onSelect, Cursor = CursorId.Hand, Role = AutomationRole.Tab, Focusable = true,
                Children =
                [
                    new BoxEl
                    {
                        Height = slotHeight, Width = w, ZStack = true, AlignItems = FlexAlign.End, Justify = FlexJustify.Center,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = artSize, Height = artSize, ZStack = true,
                                AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Center,
                                Corners = Radii.Circle(artSize),
                                BorderWidth = selected ? 3f : 0f, BorderColor = Tok.AccentDefault,
                                Padding = selected ? Edges4.All(3f) : default,
                                Children =
                                [
                                    Controls.Artwork(Controls.ArtUrl(a.ImageId), inner, inner, Radii.Full, decodePx: 128),
                                    // Width is explicit: a ZStack stretches an AUTO child into a column-wide capsule.
                                    new BoxEl
                                    {
                                        Width = rank >= 10 ? 28f : 20f, Height = Spacing.XL,
                                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                        Corners = Radii.Circle(Spacing.XL),
                                        Fill = selected ? Tok.AccentDefault : Tok.FillControlSolid,
                                        BorderWidth = selected ? 0f : 1f, BorderColor = Tok.StrokeCardDefault,
                                        HitTestVisible = false,
                                        Children =
                                        [
                                            Caption(rank.ToString(CultureInfo.CurrentCulture)) with
                                            {
                                                Weight = 600, MaxLines = 1,
                                                Color = selected ? Tok.TextOnAccentPrimary : Tok.TextSecondary,
                                            },
                                        ],
                                    },
                                ],
                            },
                        ],
                    },
                    Caption(a.Name) with
                    {
                        Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2,
                        Trim = TextTrim.CharacterEllipsis, MaxWidth = w, MinWidth = 0f,
                    },
                ],
            }.Interactive(Interaction.Subtle);
        }
    }

    // ══ 2. SHARED: the one-item "Go to artist" menu and the navigation (ch 11 §6.1) ═══════════════════════════════════

    /// <summary>ONE item, "Go to artist": the podium and Mixview's own left-click is select / re-hub, so the one thing
    /// this surface cannot otherwise reach is the artist page.</summary>
    static ContextMenuModel? GoToArtistMenu(int slot)
    {
        if (slot <= Table.None) return null;
        return new ContextMenuModel(
        [
            new MenuFlyoutItem(Loc.Get(Strings.Detail.GoToArtist), ActionIcons.Resolve(ActionIcons.Artist), true, () => OpenArtist(slot)),
        ]);
    }

    /// <summary>Open an artist page with Home's origin (ch 10 §0.13: the masthead reads <c>Home › Artist</c>).</summary>
    static void OpenArtist(int slot)
    {
        var a = new Artist(slot);
        if (!a.IsValid || !a.Uri.IsValid) return;
        Shell.GoTo(Shell.For(a.Uri, a.Name), HomeCardNav.HomeOrigin);
    }

    // ══ 3. MIXVIEW (.exp-r) ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The panel's re-pushed props: a record, so an unchanged re-render coalesces — which holds only because
    /// the row passes a STABLE <see cref="OnHubChanged"/>.</summary>
    sealed record MixviewProps(int Hub, int Tier, Action<int> OnHubChanged);

    /// <summary>A head, then the node graph: the radial ring at tier Wide, a vertical spine list below it. A thin
    /// presentational shell — the hub lives in the row; a node click reports back through the props.</summary>
    sealed class MixviewPanel : Component
    {
        internal const int MaxNodes = 6;
        const float SpineHubD = 40f, SpineNodeD = 32f;

        readonly int[] _nodes = new int[MaxNodes];
        readonly Action[] _nodeClicks = new Action[MaxNodes];
        readonly Func<ContextMenuModel?>[] _nodeMenus = new Func<ContextMenuModel?>[MaxNodes];
        Action? _hubClick;
        Func<ContextMenuModel?>? _hubMenu;
        MixviewProps? _p;
        string? _lastUri;
        long _lastTick;

        public override Element Render()
        {
            var p = UseProps<MixviewProps>();
            _p = p;
            var overlay = UseContext(Overlay.Service);
            var measured = UseMeasuredWidth(4f);
            var scope = Entities.Current;
            _ = scope.Edges.ArtistRelated.Changed.Value;
            _ = scope.Artists.Changed.Value;
            var related = scope.Edges.ArtistRelated.Targets(p.Hub);
            // The header REPORTS the drawn count: a ~318-DIP pane fits ≈ 6.6 of the 104-DIP captions round the ring.
            int drawn = Math.Min(related.Length, MaxNodes);
            related[..drawn].CopyTo(_nodes);
            IOverlayService? host = Controls.IsNullOverlay(overlay) ? null : overlay;

            Element graph = drawn > 0
                ? (p.Tier == HomeArtistRowLayout.TierWide ? Ring(p.Hub, drawn, measured.Value, host) : Spine(p.Hub, drawn, host))
                : scope.Edges.ArtistRelated.Readiness(p.Hub) == EdgeState.Unknown
                    ? new BoxEl { Height = 120f, MinWidth = 0f, Corners = Radii.ControlAll, Fill = SkeletonStyle.Default.BarColor, HitTestVisible = false }
                    : new BoxEl();

            return new BoxEl
            {
                Direction = 1, Gap = Spacing.M, MinWidth = 0f, Padding = Edges4.All(Spacing.M),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                        Children =
                        [
                            BodyStrong(Loc.Get(Strings.Home.Mixview)) with { MaxLines = 1, Shrink = 0f },
                            drawn > 0
                                ? Caption(Strings.Home.RelatedCount(drawn)) with
                                  { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f }
                                : new BoxEl(),
                        ],
                    },
                    graph,
                ],
            };
        }

        Action NodeClickAt(int i) => _nodeClicks[i] ??= () => OnNode(i);
        Func<ContextMenuModel?> NodeMenuAt(int i) => _nodeMenus[i] ??= () => GoToArtistMenu(_nodes[i]);
        Action HubClick => _hubClick ??= OnHub;
        Func<ContextMenuModel?> HubMenu => _hubMenu ??= () => GoToArtistMenu(_p?.Hub ?? Table.None);

        /// <summary>Re-centre, don't navigate (the graph AND the left pane follow); a second click on the SAME node inside
        /// the window opens the artist page.</summary>
        void OnNode(int i)
        {
            if (_p is not { } p || (uint)i >= MaxNodes) return;
            int slot = _nodes[i];
            if (slot <= Table.None) return;
            if (Doubled(slot)) { OpenArtist(slot); return; }
            p.OnHubChanged(slot);
        }

        /// <summary>The hub's single click is a deliberate no-op (it is already the centre); kept wired so a second click
        /// can complete the double-click.</summary>
        void OnHub()
        {
            if (_p is { Hub: > Table.None } p && Doubled(p.Hub)) OpenArtist(p.Hub);
        }

        bool Doubled(int slot)
        {
            string uri = new Artist(slot).Uri.Text;
            long now = Environment.TickCount64;
            if (HomeArtistRowLayout.IsDoubleClick(uri, _lastUri, _lastTick, now))
            {
                _lastUri = null;
                _lastTick = 0;
                return true;
            }
            _lastUri = uri;
            _lastTick = now;
            return false;
        }

        /// <summary><c>.mix</c> — the hub centred, the related artists on a ring, one 1-DIP connector per edge (solid:
        /// <c>PolylineStrokeEl</c> carries no dash). Positions are computed (a radial arrangement has no flex expression):
        /// a ZStack whose children carry centre-minus-radius margins.</summary>
        Element Ring(int hub, int n, float measuredWidth, IOverlayService? host)
        {
            // The measured width is the component's OUTER width, before its padding.
            float w = measuredWidth > 2f * Spacing.M ? measuredWidth - 2f * Spacing.M : 314f;
            float h = w * 0.92f + 18f;                 // aspect 1/.92 plus one caption line under the bottom node
            float cx = w * 0.5f, cy = h * 0.46f;
            const float hubR = 34f, nodeR = 21f;
            float ringR = MathF.Min(cx, cy) - nodeR - 12f;

            var layers = new Element[n * 3 + 2];
            int k = 0;
            for (int i = 0; i < n; i++)                // connectors first, so every node paints over its own edge
            {
                float ang = -MathF.PI / 2f + i * (MathF.Tau / n);
                layers[k++] = new PolylineStrokeEl
                {
                    P0 = new Point2(cx, cy), P1 = new Point2(cx + MathF.Cos(ang) * ringR, cy + MathF.Sin(ang) * ringR),
                    PointCount = 2, Color = Tok.TextTertiary with { A = 0.26f }, Thickness = 1f, Width = w, Height = h,
                };
            }
            var hubArtist = new Artist(hub);
            layers[k++] = Node(hubArtist, cx, cy, hubR, isHub: true, HubClick, host, HubMenu);
            layers[k++] = NodeCap(hubArtist.Name, cx, cy, hubR, isHub: true);
            for (int i = 0; i < n; i++)
            {
                float ang = -MathF.PI / 2f + i * (MathF.Tau / n);
                float x = cx + MathF.Cos(ang) * ringR, y = cy + MathF.Sin(ang) * ringR;
                var r = new Artist(_nodes[i]);
                layers[k++] = Node(r, x, y, nodeR, isHub: false, NodeClickAt(i), host, NodeMenuAt(i));
                layers[k++] = NodeCap(r.Name, x, y, nodeR, isHub: false);
            }
            return new BoxEl { ZStack = true, Width = w, Height = h, MinWidth = 0f, Children = layers };
        }

        /// <summary>The narrow arm: the hub, then the related artists hanging off ONE full-height 1-DIP rule (a box the row
        /// cross-stretches, not six strokes), inset so the rule falls under the hub avatar's centre.</summary>
        Element Spine(int hub, int n, IOverlayService? host)
        {
            var rows = new Element[n];
            for (int i = 0; i < n; i++)
            {
                var r = new Artist(_nodes[i]);
                var row = new BoxEl
                {
                    Key = "mixview-spine:" + _nodes[i].ToString(CultureInfo.InvariantCulture),
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.XS, Spacing.XS),
                    OnClick = NodeClickAt(i), Cursor = CursorId.Hand, Role = AutomationRole.Button,
                    Children =
                    [
                        Avatar(r, SpineNodeD, ring: false),
                        Caption(r.Name) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                    ],
                };
                rows[i] = host is null ? row : row.WithContextMenu(host, NodeMenuAt(i));
            }
            var hubArtist = new Artist(hub);
            var hubRow = new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                OnClick = HubClick, Cursor = CursorId.Hand, Role = AutomationRole.Button,
                Children =
                [
                    Avatar(hubArtist, SpineHubD, ring: true),
                    BodyStrong(hubArtist.Name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                ],
            };
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    host is null ? hubRow : hubRow.WithContextMenu(host, HubMenu),
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                        Padding = new Edges4(SpineHubD * 0.5f, 0f, 0f, 0f),
                        Children =
                        [
                            new BoxEl { Width = 1f, Shrink = 0f, AlignSelf = FlexAlign.Stretch, Fill = Tok.TextTertiary with { A = 0.26f } },
                            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = rows },
                        ],
                    },
                ],
            };
        }

        /// <summary>A round avatar; the hub's 3-DIP accent halo says "everything below hangs off this".</summary>
        static BoxEl Avatar(Artist a, float d, bool ring) => new()
        {
            Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d),
            BorderWidth = ring ? 3f : 0f, BorderColor = Tok.AccentDefault,
            Children = [Controls.Artwork(Controls.ArtUrl(a.ImageId), d, d, Radii.Full, decodePx: 128)],
        };

        /// <summary>A node placed by its CENTRE: the ZStack anchors top-left, so the margin carries centre − radius.</summary>
        static Element Node(Artist a, float cx, float cy, float r, bool isHub, Action onClick, IOverlayService? host,
                            Func<ContextMenuModel?> menu)
        {
            float d = r * 2f;
            var node = new BoxEl
            {
                Width = d, Height = d, Shrink = 0f,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(cx - r, cy - r, 0f, 0f),
                Corners = Radii.Circle(d),
                BorderWidth = isHub ? 3f : 0f, BorderColor = Tok.AccentDefault,
                OnClick = onClick, Cursor = CursorId.Hand, Role = AutomationRole.Button,
                Children = [Controls.Artwork(Controls.ArtUrl(a.ImageId), d, d, Radii.Full, decodePx: 128)],
            };
            return host is null ? node : node.WithContextMenu(host, menu);
        }

        /// <summary><c>.node-cap</c> — width 104, centred under the node, Caption 12/16 (the hub's 600 primary). The
        /// prototype's second "via" line has no payload field and is omitted rather than invented.</summary>
        static Element NodeCap(string name, float cx, float cy, float r, bool isHub) => new BoxEl
        {
            Width = 104f, Shrink = 0f, HitTestVisible = false,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
            Direction = 0, Justify = FlexJustify.Center,
            Margin = new Edges4(cx - 52f, cy + r + 5f, 0f, 0f),
            Children =
            [
                Caption(name) with
                {
                    Weight = (ushort)(isHub ? 600 : 400), Color = isHub ? Tok.TextPrimary : Tok.TextSecondary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
            ],
        };
    }
}
