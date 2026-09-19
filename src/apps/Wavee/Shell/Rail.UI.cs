// ── Shell/Rail.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// rail frame (docked + floating), header, the docked video cap's body, NPV panel, hero tile, header row, lyrics
// peek, friends panel, artwork context menu
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 1400 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE RIGHT RAIL (ch 21 §1-§6). One panel, two mutually exclusive ARMS (never one tree with a flag):
//
//   NowPlaying arm   surface · column[ PinnedHero (the docked cap | the hero tile + Art|Video toggle), NPV body ] · edge
//   every other arm  surface · column[ DockedCap (mounts UNCONDITIONALLY; the card's own gate hides it), header, body ]
//
// ONE coat per rung (§4.1): docked the surface is Transparent and the shell's reservation band paints FileArea; closing
// takes the coat back for the 300 ms slide-out (the Fill bind includes RailOpen); floating paints its own FileArea, a
// uniform ring and Elevation.Flyout. Open/close is ONE TranslateX track on the host node — never an opacity track.
//
// Mounted here from other stage-B files: `Video.DockedCap()` (K3), `Deck.View()` (K2), `Lyrics.NpvPeek()` and
// `Lyrics.View()` (K3). The queue body is owner Q's (Wave 5) and installs itself through `Rail.QueueBody`.
// The style flyout, the mini-arts, the art menu and the swatches are the named partial `Rail.Styles.UI.cs`.

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Rail
{
    // MOUNT POINT (stage B contract)
    /// <summary>The right rail panel. The shell's panel host owns the reservation spacer, the seam splitter and the fixed
    /// clip; this owns the translate, the coats, the header and the bodies.</summary>
    public static Element Frame() => Embed.Comp(static () => new FrameCore());

    /// <summary>The Queue body — owner Q's rail queue panel (`Entities/Queue.UI.cs`, Wave 5) installs itself here at
    /// composition. Until then the rail shows the queue's empty grammar.</summary>
    public static Func<Element>? QueueBody { get; set; }

    /// <summary>Row 1 of the icon-button table pairs the 32-square with a 16-DIP glyph (never 12).</summary>
    internal const float HeaderGlyph = 16f;

    static readonly CornerRadius4 RailCorners = new(Radii.Card, 0f, 0f, 0f);

    // ── the frame ────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FrameCore : Component
    {
        bool _motionSeeded;

        public override Element Render()
        {
            bool open = Shell.Ui.RailOpen.Value;
            float railWidth = Shell.Ui.RailWidth.Value;
            var mode = Shell.Ui.Mode.Value;
            bool floating = !Shell.Ui.RailFits.Value;
            var state = Video.State.Surface.Value;
            var resolved = Video.PlacementCore.Resolve(state);

            // The rail YIELDS to a watch page's in-page stage: the host is DERIVED, never claimed.
            string stagePlayable = Shell.Ui.ActiveStagePlayable.Value;
            bool stageHosts = stagePlayable.Length > 0
                && Video.DockedHosting.HostFor(resolved, stagePlayable, Playback.CurrentId.Value.Text) == Video.DockedHost.PageStage;
            bool dockedHere = resolved == Video.SurfacePlacement.Docked && !stageHosts;
            var body = VideoCoupling.BodyModeFor(mode, stageHosts);

            // ONE retained translate track on the host node; a rapid reversal continues from the PRESENTED position.
            UseLayoutEffect(() =>
            {
                if (Context.Anim is not { } anim || Context.HostNode.IsNull) return;
                float x = open ? 0f : railWidth;
                if (!_motionSeeded)
                {
                    _motionSeeded = true;                                  // seed closed off-canvas: no startup fly-in
                    anim.Animate(Context.HostNode, AnimChannel.TranslateX, x, x, 1f, Easing.Linear);
                    return;
                }
                float from = Context.Scene is { } scene ? scene.Paint(Context.HostNode).LocalTransform.Dx : (open ? railWidth : 0f);
                anim.Animate(Context.HostNode, AnimChannel.TranslateX, from, x, 300f, EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f));
            }, DepKey.From(open ? 1 : 0, (int)MathF.Round(railWidth)));

            Element surface = new BoxEl
            {
                Grow = 1f, Corners = RailCorners, ClipToBounds = true,
                Fill = Prop.Of(static () => Shell.Ui.RailFits.Value && Shell.Ui.RailOpen.Value ? ColorF.Transparent : Design.Colors.FileArea),
            };
            // The ONE docked hairline, LEFT + TOP, drawn topmost so content cannot cover it. Floating: the ring is the arm's.
            Element edge = new BoxEl
            {
                Margin = new Edges4(0f, 0f, -1f, -1f), HitTestVisible = false, Corners = RailCorners,
                BorderWidth = floating ? 0f : 1f, BorderColor = Tok.StrokeCardDefault,
            };

            if (body == Shell.RailMode.NowPlaying)
                return Arm(open, floating, surface, edge,
                    PinnedHero(stageHosts, state, resolved, railWidth), GrowSlot(Embed.Comp(static () => new NowPlayingBody())));

            Element header = body == Shell.RailMode.Lyrics ? Embed.Comp(static () => new LyricsHeader()) : Header(body);
            return Arm(open, floating, surface, edge, DockedCap(dockedHere, railWidth), header, GrowSlot(Body(body)));
        }
    }

    static BoxEl Arm(bool open, bool floating, Element surface, Element edge, params Element[] column) => new()
    {
        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Corners = RailCorners,
        HitTestVisible = open,                                            // a closed rail is laid out, off-clip, unhittable
        BorderColor = floating ? Tok.StrokeCardDefault : ColorF.Transparent,
        BorderWidth = floating ? 1f : 0f,
        Shadow = floating ? Elevation.Flyout : (ShadowSpec?)null,         // NO shadow docked
        Children = [surface, new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = column }, edge],
    };

    static BoxEl GrowSlot(Element body) => new() { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] };

    static Element Body(Shell.RailMode body) => body switch
    {
        Shell.RailMode.Lyrics => Lyrics.View(),
        Shell.RailMode.Queue => QueueBody?.Invoke()
            ?? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                title: Loc.Get(Strings.Player.QueueEmpty), subtitle: ""),
        Shell.RailMode.Friends => Embed.Comp(static () => new FriendsBody()),
        Shell.RailMode.Video => Embed.Comp(static () => new VideoBody()),
        _ => Embed.Comp(static () => new NowPlayingBody()),
    };

    // ── the docked cap (the card is K3's; this is the height, the letterbox and the splitter) ───────────────────────

    /// <summary>The rail's ONE docked card, reached from both arms. Its Height is the SAME FloatSignal the splitter
    /// writes — a stable bind, never a fresh thunk (that left the height NaN and collapsed the ZStack to the strip).</summary>
    static Element DockedCap(bool docked, float railWidth)
    {
        Element video = Video.DockedCap();
        if (!docked) return video;                                         // the card's own gate: zero size, no reflow
        return new BoxEl
        {
            Direction = 1, ZStack = true, Shrink = 0f, ClipToBounds = true,
            Height = Shell.Ui.DockedVideoHeight, Fill = Tok.MediaLetterbox,
            Children =
            [
                video,
                new BoxEl
                {
                    Grow = 1f, Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true,
                    Children =
                    [
                        Splitter.Create(Shell.Ui.DockedVideoHeight, CommitCap, new Splitter.SplitterOptions
                        {
                            Min = Shell.DockedVideoNaturalH(railWidth), Max = Shell.DockedVideoMaxH,
                            Axis = SplitterAxis.Vertical, ShowIndicator = false,
                        }),
                    ],
                },
            ],
        };
    }

    /// <summary>A COMMITTED drag pins the height against the content fit for as long as this source plays (the card
    /// clears the pin at the next source) and persists it.</summary>
    static void CommitCap()
    {
        float h = Shell.ClampDockedVideoHeight(Shell.Ui.DockedVideoHeight.Peek(), Shell.Ui.RailWidth.Peek());
        Shell.Ui.DockedVideoHeight.Value = h;
        Shell.Ui.DockedVideoHeightPinned.Value = true;
        Platform.Settings.Set(Platform.Keys.ShellDockedVideoHeight, h);
    }

    /// <summary>The NowPlaying arm's pinned slot — ONE slot, two possible occupants, never both.</summary>
    static Element PinnedHero(bool stageHosts, in Video.PlacementState state, Video.SurfacePlacement resolved, float railWidth)
    {
        Element art = Embed.Comp(static () => new HeroTile());
        if (stageHosts) return art;                                        // the page stage owns the one surface
        if (resolved == Video.SurfacePlacement.Docked) return DockedCap(true, railWidth);
        if (!Video.PlacementCore.Allows(state.Available, Video.SurfacePlacement.Docked)) return art;
        return new BoxEl { ZStack = true, Shrink = 0f, Children = [art, ArtVideoToggle()] };
    }

    /// <summary>The 2-state Art|Video toggle over the ART's top-right corner. Its offset is DERIVED from
    /// <see cref="Hero.ArtTop"/>; each half's tooltip names the action IT performs.</summary>
    static Element ArtVideoToggle() => new BoxEl
    {
        Height = 24f, Shrink = 0f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End,
        Margin = new Edges4(0f, Hero.ArtTop + Spacing.XS, Spacing.S + Spacing.XS, 0f),
        Direction = 0, Corners = Radii.ControlAll, ClipToBounds = true, Fill = Design.OnMedia.GlassHover,
        Children =
        [
            ToggleHalf(Icons.Picture, selected: true, Loc.Get(Strings.Player.SwitchToAudio),
                static () => Video.State.ReportClosed(Video.SurfacePlacement.Docked)),
            ToggleHalf(Icons.Movie, selected: false, Loc.Get(Strings.Player.SwitchToVideo),
                static () => ShowVideoAt(Video.SurfacePlacement.Docked)),
        ],
    };

    static Element ToggleHalf(string glyph, bool selected, string tip, Action onClick) => ToolTip.Wrap(new BoxEl
    {
        Width = 24f, Height = 24f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll, Fill = selected ? Tok.OnMediaPrimary with { A = 0.16f } : ColorF.Transparent,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick,
        Children = [new TextEl(glyph) { Size = 11f, FontFamily = Theme.IconFont, Color = selected ? Tok.OnMediaPrimary : Tok.OnMediaSecondary }],
    }, tip);

    /// <summary>A placement intent: re-stamp the availability THIS playable has, then open there.</summary>
    static void ShowVideoAt(Video.SurfacePlacement at)
    {
        var r = Playback.Current.Peek();
        Video.State.FoldAvailability(r.Kind == EntityKind.Track && new Track(r.Slot).HasVideo);
        Video.State.OpenAt(at);
    }

    // ── the header ───────────────────────────────────────────────────────────────────────────────────────────────────

    static BoxEl HeaderBox(Element[] kids) => new()
    {
        Direction = 0, Height = Design.Size.NavItemH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
        Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f), Children = kids,
    };

    static Element Header(Shell.RailMode body)
    {
        if (body != Shell.RailMode.Video) return HeaderBox([TitleText(body), CloseButton()]);
        // The header's ▣ / ⛶ are the one ALWAYS-visible way to move a docked video (W23b) — keep all three.
        return HeaderBox(
        [
            TitleText(body),
            HeaderButton(Icons.BackToWindow, Loc.Get(Strings.Player.VideoMiniPlayer), static () =>
            {
                FluentGpu.Input.Announcer.Say(Loc.Get(Strings.Player.VideoMiniPlayer));
                ShowVideoAt(Video.SurfacePlacement.Floating);
            }),
            HeaderButton(Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), static () =>
            {
                FluentGpu.Input.Announcer.Say(Loc.Get(Strings.Player.VideoFullScreen));
                ShowVideoAt(Video.SurfacePlacement.Fullscreen);
            }),
            CloseButton(),
        ]);
    }

    /// <summary>Title · (secondary-line toggle) · expand · close. Its own component so the capability and the prefs epoch
    /// subscribe the HEADER, not the whole rail. The toggle is composed only when the document on screen carries a
    /// second layer; the expand button is returned in BOTH arms — a track with no lyrics still opens a full stage.</summary>
    sealed class LyricsHeader : Component
    {
        public override Element Render()
        {
            int available = Prefs.Lyrics.Available.Value;
            int secondary = Prefs.Lyrics.SecondaryLine();
            var kids = new List<Element>(4) { TitleText(Shell.RailMode.Lyrics) };
            if (available != 0)
                kids.Add(HeaderButton(Icons.Globe, Prefs.Lyrics.Tooltip(secondary),
                    () => Prefs.Lyrics.SetSecondaryLine(Prefs.Lyrics.Next(secondary, available)),
                    active: (available & Prefs.Lyrics.BitFor(secondary)) != 0));
            kids.Add(HeaderButton(Icons.FullScreen, Loc.Get(Playback.CurrentId.Value.Kind == EntityKind.Episode ? Strings.Podcast.Reader.Transcript : Strings.Player.ExpandLyrics), static () => Shell.Ui.ImmersiveLyrics.Value = true));
            kids.Add(Diagnostics.LyricsInspector.Button());
            kids.Add(CloseButton());
            return HeaderBox(kids.ToArray());
        }
    }

    static Element TitleText(Shell.RailMode mode) => Design.Type.RailHeader(Title(mode)) with
    {
        Grow = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    static string Title(Shell.RailMode m) => m switch
    {
        Shell.RailMode.Lyrics => Loc.Get(Playback.CurrentId.Value.Kind == EntityKind.Episode ? Strings.Podcast.Reader.Transcript : Strings.Player.Lyrics),
        Shell.RailMode.Queue => Loc.Get(Strings.Player.Queue),
        Shell.RailMode.Friends => Loc.Get(Strings.Friends.Title),
        Shell.RailMode.Video => Loc.Get(Strings.Player.Video),
        _ => Loc.Get(Strings.Player.NowPlaying),
    };

    /// <summary>A glyph button in the header. <paramref name="active"/> is the stateful arm (the accent tint is the only
    /// affordance a 32-DIP glyph has to say "on").</summary>
    internal static Element HeaderButton(string glyph, string tip, Action onClick, bool active = false) => ToolTip.Wrap(GlyphBox(glyph, onClick, active), tip);

    static BoxEl GlyphBox(string glyph, Action onClick, bool active) => new BoxEl
    {
        Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick,
        Children =
        [
            new TextEl(glyph)
            {
                Size = HeaderGlyph, FontFamily = Theme.IconFont,
                Color = active ? Tok.AccentTextPrimary : Tok.TextSecondary,
                HoverColor = active ? Tok.AccentTextPrimary : Tok.TextPrimary,
            },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>✕ closes the RAIL for every mode, video included — the coupling demotes a docked video. No tooltip: a close
    /// glyph is universal.</summary>
    static Element CloseButton() => GlyphBox(Icons.ChromeClose, static () => Shell.Ui.RailOpen.Value = false, active: false);

    // ── shared reads ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playing TRACK — subscribes the playable and the track table — or an invalid handle (an episode, a
    /// module playable or nothing).</summary>
    internal static Track NowTrack()
    {
        var r = Playback.Current.Value;
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        _ = Entities.Current.Tracks.Changed.Value;
        return r.Kind == EntityKind.Track ? new Track(r.Slot) : default;
    }

    /// <summary>The playing track's cover url, read LIVE (for a bound thunk that must follow a track change).</summary>
    static string? NowArtUrl()
    {
        var r = Playback.Current.Value;
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        _ = Entities.Current.Tracks.Changed.Value;
        return r.Kind == EntityKind.Track ? Controls.ArtUrl(new Track(r.Slot).ImageId) : null;
    }

    /// <summary>THE hero wash — one function, three surfaces (the cover placeholder, the deck faces' art and the flyout's
    /// "Album colour" swatch), so the three can never grade the artwork differently. The palette watch is read INSIDE, so
    /// a late grading repaints a bound placeholder without re-rendering anything.</summary>
    public static ColorF HeroWashColor(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = Design.SchemeFor(url) is { } s ? Design.Palette.Lift(Design.Palette.Accent(s)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, Tok.Theme == ThemeKind.Dark ? 0.18f : 0.10f);
    }

    static void GoTo(EntityUri uri, string name) => Shell.GoTo(Shell.For(uri, name));

    // ── the hero tile (pinned; never inside the scroller) ────────────────────────────────────────────────────────────

    /// <summary>The header strip over a KEYED hero slot: "cover" or "&lt;slug&gt;@&lt;side&gt;". A preset change REMOUNTS (a
    /// Cassette must not inherit a Record's mounted deck), an option flip restyles in place off the epoch, and the side
    /// is quantised to 4 DIP because it is part of the key. The WRAPPER owns the art menu for both occupants.</summary>
    sealed class HeroTile : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var track = NowTrack();
            int presentation = PlayerPrefs.Presentation();
            var preset = PlayerPrefs.CurrentPreset();
            float side = Hero.Side(Shell.Ui.RailWidth.Value);
            if (!track.IsValid) return new BoxEl();                        // nothing playing: no square, no strip

            Element hero = presentation == PlayerPrefs.Player
                ? new BoxEl { Key = preset.Slug + "@" + (int)side, Direction = 1, Shrink = 0f, Children = [Deck.View()] }
                : new ImageEl
                {
                    Key = "cover",
                    Source = Prop.Of(static () => NowArtUrl() ?? ""), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = 512f,
                    Corners = Radii.CardAll, AlignSelf = FlexAlign.Stretch,
                    Placeholder = Prop.Of(static () => HeroWashColor(NowArtUrl())),
                };

            var heroBox = new BoxEl { Direction = 1, Shrink = 0f, Children = [hero] }
                .WithContextMenu(overlay, () => ArtMenu(presentation, preset));

            return new BoxEl
            {
                Direction = 1, Shrink = 0f, Gap = Hero.Inset,
                Padding = new Edges4(Hero.Inset, Hero.Inset, Hero.Inset, 0f),
                Children = [Embed.Comp(static () => new HeaderRow()), heroBox],
            };
        }
    }

    /// <summary>Eyebrow · Cover|‹Player› · gear. The SelectorBar's index is a component-owned MIRROR written by the bar on
    /// click (the pill moves in the press frame) and re-synced by an effect when another surface moved the pref — never
    /// written during Render.</summary>
    sealed class HeaderRow : Component
    {
        static readonly string?[] ModeIcons = [Icons.Picture, Icons.Album];

        /// <summary>WinUI's SelectorBarItem builds a 47-DIP bar; the strip is 36. Compress the ITEM's content box to the
        /// 30-DIP pill (shape-guarded: an item tree change makes this a no-op instead of a mangle).</summary>
        static readonly TemplateParts CompactBar = new()
        {
            [SelectorBar.PartItem] = static item =>
            {
                if (item.Children is not [BoxEl content, var pill]) return item;
                var kids = new Element[content.Children.Length];
                for (int i = 0; i < kids.Length; i++)
                    kids[i] = content.Children[i] is TextEl t && t.FontFamily is null ? t with { Size = 13f } : content.Children[i];
                return item with { Children = [content with { Padding = new Edges4(12f, 3f, 12f, 2f), Gap = 7f, Children = kids }, pill] };
            },
        };

        public override Element Render()
        {
            int epoch = PlayerPrefs.Epoch.Value;
            int stored = PlayerPrefs.Presentation();
            var preset = PlayerPrefs.CurrentPreset();
            var presentation = UseSignal(stored);
            UseEffect(() => { presentation.SetIfChanged(stored); }, DepKey.From(epoch));
            bool open = PlayerPrefs.StyleFlyoutOpen.Value;              // the gear lights wherever the flyout was opened from

            string[] items = [Loc.Get(Strings.Player.PresentationCover), Loc.Get(preset.ShortLabelKey)];
            Element gear = HeaderButton(Icons.Settings, Loc.Get(Strings.Player.PlayerStyle),
                static () => PlayerPrefs.StyleFlyoutOpen.Value = !PlayerPrefs.StyleFlyoutOpen.Peek(), active: open);

            return new BoxEl
            {
                Direction = 0, Height = Hero.HeaderRowH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Children =
                [
                    // Sentence case, never .ToUpper() on a localized string.
                    Design.Type.Eyebrow(Loc.Get(Strings.Player.NowPlaying)) with
                    {
                        Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    SelectorBar.Create(items, presentation,
                        onChange: static i => PlayerPrefs.SetPresentation(i, NpvDiagnostics.SourceHeader),
                        parts: CompactBar, icons: ModeIcons),
                    // A plain wrapper takes the row's Center: the popup's own anchor wrapper is AlignSelf=Start.
                    new BoxEl
                    {
                        Direction = 0, Shrink = 0f,
                        Children =
                        [
                            Popup.Create(gear, static () => StyleFlyout(), PlayerPrefs.StyleFlyoutOpen,
                                onOpenChanged: static nowOpen => { if (!nowOpen) NpvDiagnostics.FlyoutClosed(); },
                                placement: FlyoutPlacement.BottomEdgeAlignedRight,
                                options: new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)),
                        ],
                    },
                ],
            };
        }
    }

    // ── the NowPlaying body (scrolls under the pinned hero) ──────────────────────────────────────────────────────────

    sealed class NowPlayingBody : Component
    {
        public override Element Render()
        {
            var track = NowTrack();
            int trackSlot = track.Slot;
            var artistSlots = track.IsValid ? track.ArtistSlots : default;
            int artistSlot = artistSlots.Length > 0 ? artistSlots[0] : 0;

            // The rail is not a page and never mounts on a route: its demand is keyed on the PLAYING track (§9.3 #6).
            UseEffect(() =>
            {
                if (trackSlot > 0) Entities.Ensure(new Track(trackSlot), TrackFields.Identity);
                if (artistSlot > 0) Entities.Ensure(new Artist(artistSlot), ArtistFields.Overview);
            }, DepKey.From(trackSlot, artistSlot));

            if (!track.IsValid)
                return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                        title: Loc.Get(Strings.Player.NothingPlaying), subtitle: "");

            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            _ = Entities.Current.Edges.ArtistCities.Changed.Value;
            _ = Entities.Current.Edges.Queue.Changed.Value;

            var sections = new List<Element>(5);
            if (artistSlot > 0)
            {
                var artist = new Artist(artistSlot);
                if (artist.Knows(ArtistFields.Stats))
                {
                    sections.Add(AboutArtist(artist));
                    if (artist.TopCities.Length > 0) sections.Add(CitiesSection(artist.TopCities));
                }
                else sections.Add(LoadingSection());                      // pending: under the SAME header, never alone
                var merch = track.Album.MerchSlots.Length > 0 ? track.Album.MerchSlots : artist.MerchSlots;
                if (merch.Length > 0) sections.Add(MerchSection(merch));
            }
            if (UpNextRows(out Element[] next)) sections.Add(Section(Loc.Get(Strings.Player.NextUp), new BoxEl { Direction = 1, Gap = 2f, Children = next }));

            return ScrollView(new BoxEl
            {
                Direction = 1,
                Children =
                [
                    HeroMeta(track),
                    sections.Count == 0 ? new BoxEl()
                        : new BoxEl { Direction = 1, Gap = Spacing.L, Padding = new Edges4(Spacing.M, 0f, Spacing.M, Spacing.M), Children = sections.ToArray() },
                ],
            }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
        }
    }

    /// <summary>Title · artist links · album link · heart, then the lyrics peek (K3's, keyed per track).</summary>
    static Element HeroMeta(Track track)
    {
        string uri = track.Uri.Text;
        string title = track.Title;
        var meta = new List<Element>(2);
        var artists = track.ArtistSlots;
        if (artists.Length > 0)
        {
            var spans = new TextSpan[artists.Length * 2 - 1];
            for (int i = 0, n = 0; i < artists.Length; i++)
            {
                if (i > 0) spans[n++] = new TextSpan(", ");
                var a = new Artist(artists[i]);
                string name = a.Name;
                spans[n++] = new TextSpan(name, OnClick: () => GoTo(a.Uri, name));
            }
            meta.Add(new SpanTextEl(spans)
            {
                Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        }
        var album = track.Album;
        if (album.IsValid && album.Title.Length > 0)
        {
            string albumTitle = album.Title;
            meta.Add(new SpanTextEl([new TextSpan(albumTitle, OnClick: () => GoTo(album.Uri, albumTitle))])
            {
                Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        }

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.L),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 4f,
                            Children =
                            [
                                Ui.Subtitle(title) with { Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis },
                                new BoxEl { Direction = 1, Gap = 2f, Children = meta.ToArray() },
                            ],
                        },
                        Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = 16f, Box = 36f }) with { Key = "save:" + uri },
                    ],
                },
                Lyrics.NpvPeek() with { Key = "npv-lyrics:" + uri },
            ],
        };
    }

    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S,
        Children = [Design.Type.RailHeader(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, body],
    };

    static Element AboutArtist(Artist artist)
    {
        const float heroH = 132f;
        string name = artist.Name;
        var facts = new List<Element>(3);
        if (artist.MonthlyListeners > 0) facts.Add(Fact(artist.MonthlyListeners.ToString("N0"), Loc.Get(Strings.Artist.MetaMonthly)));
        if (artist.Followers > 0) facts.Add(Fact(artist.Followers.ToString("N0"), Loc.Get(Strings.Artist.MetaFollowers)));
        if (artist.WorldRank > 0) facts.Add(Fact("#" + artist.WorldRank.ToString("N0"), Strings.Artist.WorldRank("").Trim()));

        string? heroUrl = Controls.ArtUrl(artist.HeroImageId);
        Element band = heroUrl is { Length: > 0 }
            ? new BoxEl
            {
                Height = heroH, ZStack = true, ClipToBounds = true, Corners = Radii.CardAll,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 56f),
                Children =
                [
                    Ui.Image(heroUrl, ImageFit.Cover, 2.6f, 320f, Radii.Card, Tok.FillSubtleSecondary),
                    new BoxEl
                    {
                        Height = heroH, Corners = Radii.CardAll,
                        Gradient = GradientDown(new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 0)),
                            new GradientStop(0.55f, ColorF.FromRgba(0, 0, 0, 31)), new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 158))),
                    },
                ],
            }
            : new BoxEl
            {
                Height = 72f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = Tok.FillSubtleSecondary,
                Corners = Radii.CardAll, ClipToBounds = true,
                Children = [PersonPicture.Create("", 56f, displayName: name, imageSourcePath: Controls.ArtUrl(artist.ImageId))],
            };

        var body = new List<Element>(4)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Children =
                [
                    new TextEl(name) { Size = 18f, LineHeight = 24f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    artist.IsVerified ? Icon(Icons.Check, 12f, Tok.AccentTextPrimary) : new BoxEl(),
                ],
            },
        };
        if (facts.Count > 0) body.Add(new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, Children = facts.ToArray() });
        string bio = Entities.Strings.Resolve(artist.BioLeadId.IsEmpty ? artist.BioId : artist.BioLeadId);
        if (!string.IsNullOrWhiteSpace(bio))
            body.Add(new TextEl(bio) { Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 5, Trim = TextTrim.CharacterEllipsis });
        string artistUri = artist.Uri.Text;
        body.Add(new BoxEl
        {
            Direction = 0,
            Children = [Embed.Comp(() => new Controls.FollowButton { Uri = artistUri, Name = name }) with { Key = "follow:" + artistUri }],
        });

        return Section(Loc.Get(Strings.Detail.AboutTheArtist), new BoxEl
        {
            Direction = 1, Corners = Radii.CardAll, Fill = Tok.FillCardSecondary, ClipToBounds = true,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
            Cursor = CursorId.Hand, OnClick = () => GoTo(artist.Uri, name),
            Children = [band, new BoxEl { Direction = 1, Gap = Spacing.M, Padding = Edges4.All(Spacing.M), Children = body.ToArray() }],
        });
    }

    static Element Fact(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = Spacing.XXS, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(value) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(label) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    /// <summary>At most 5 cities; the longest bar is 260 and the shortest never below 8 % of it.</summary>
    static Element CitiesSection(ReadOnlySpan<CityEdge> cities)
    {
        uint max = 1;
        int n = Math.Min(5, cities.Length);
        for (int i = 0; i < n; i++) max = Math.Max(max, cities[i].Listeners);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = cities[i];
            string city = Entities.Strings.Resolve(c.City), country = Entities.Strings.Resolve(c.Country);
            float frac = Math.Clamp(c.Listeners / (float)max, 0.08f, 1f);
            rows[i] = new BoxEl
            {
                Direction = 1, Gap = Spacing.XS,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(country.Length > 0 ? city + ", " + country : city)
                                { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(c.Listeners.ToString("N0")) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
                        ],
                    },
                    new BoxEl
                    {
                        Height = 4f, Corners = CornerRadius4.All(2f), Fill = Tok.FillSubtleSecondary, ClipToBounds = true,
                        Children = [new BoxEl { Width = 260f * frac, Height = 4f, Corners = CornerRadius4.All(2f), Fill = Tok.AccentDefault }],
                    },
                ],
            };
        }
        return Section(Loc.Get(Strings.Artist.ListenedMostIn), new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows });
    }

    static Element MerchSection(ReadOnlySpan<int> slots)
    {
        int n = Math.Min(4, slots.Length);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            Merch m = Album.MerchAt(slots[i]);
            string name = Entities.Strings.Resolve(m.Name), price = Entities.Strings.Resolve(m.Price);
            string shop = Entities.Strings.Resolve(m.ShopUrl);
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = Edges4.All(Spacing.S),
                Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                HoverFill = Tok.FillCardDefault,                          // an inert row keeps its hover plate
                Cursor = shop.Length > 0 ? CursorId.Hand : (CursorId?)null,
                OnClick = shop.Length > 0 ? () => InputHooks.Current.Default.OpenUri?.Invoke(shop) : null,
                Children =
                [
                    Controls.Artwork(Controls.ArtUrl(m.ImageId), 56f, 56f, Radii.Control),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                        Children =
                        [
                            new TextEl(name) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(price.Length > 0 ? price : Loc.Get(Strings.Artist.Buy)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            };
        }
        return Section(Loc.Get(Strings.Artist.Merch), new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows });
    }

    static Element LoadingSection() => Section(Loc.Get(Strings.Detail.AboutTheArtist), new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.M), Corners = Radii.CardAll, Fill = Tok.FillCardSecondary,
        Children =
        [
            new BoxEl { Height = 18f, Width = 180f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 12f, Width = 240f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 12f, Width = 210f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
        ],
    });

    // ── "Next up" rows (the NPV section and the Video body share them) ───────────────────────────────────────────────

    /// <summary>The user queue + the context continuation, at most five, as the shared rail art cards. False when there
    /// are none. The caller must already subscribe <c>Edges.Queue.Changed</c>.</summary>
    static bool UpNextRows(out Element[] rows)
    {
        if (!Queue.UpNext(out int start, out int length)) { rows = []; return false; }
        bool art = !Prefs.Appearance.TrackArtworkHidden();
        int n = Math.Min(5, length), count = 0;
        var buf = new Element[n];
        var items = Queue.Rows;
        for (int i = start; i < start + n; i++)
        {
            var r = Queue.RefAt(i);
            if (r.Kind != EntityKind.Track || r.IsNone) continue;
            buf[count++] = UpNextCard(new Track(r.Slot), i, items[i].ItemId, art);
        }
        rows = count == n ? buf : buf.AsSpan(0, count).ToArray();
        return count > 0;
    }

    /// <summary>One "Next up" row: THE shared art-forward cell (<c>Track.ArtCard</c>, Rail kind — G-214; 0.2.9's NPV and
    /// video panels used <c>TrackRow.ArtCard</c> with these same options) in an r4 wrapper with the subtle hover plate. The
    /// wrapper's key carries the queue item and the artwork flag, so a settings flip REMOUNTS rather than re-binds. Play
    /// jumps to the row inside the live queue, resolving its index when clicked: the card keeps its options across a
    /// queue shift (a props record compares no delegate), so a captured index would go stale.</summary>
    static Element UpNextCard(Track t, int queueIndex, ulong itemId, bool art)
    {
        Action play = () =>
        {
            int index = itemId != 0 ? Queue.IndexOfItem(itemId) : queueIndex;
            if (index < 0 || index >= Queue.Count) return;
            Playback.PlayNow(Queue.RefAt(index), Playback.ContextUri.Peek(), Queue.CursorOf(index));
        };
        return new BoxEl
        {
            Key = (itemId != 0 ? "vrp:i" + itemId : "vrp:e" + queueIndex) + (art ? ":art" : ":noart"),
            Direction = 1, Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
            Children =
            [
                Track.ArtCard(t, new Track.ArtCardOptions(Kind: Track.ArtCardKind.Rail, Art: Design.Size.ArtThumb,
                    ShowDuration: false, ShowExplicit: false, ShowVideo: true, OnPlay: play)),
            ],
        };
    }

    // ── the Video body (RailMode.Video — everything BELOW the pinned card) ──────────────────────────────────────────

    sealed class VideoBody : Component
    {
        public override Element Render()
        {
            var track = NowTrack();
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = Entities.Current.Edges.Queue.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            var content = new List<Element>(8);
            // B10: the card upstream collapses; the rail does NOT close. Never flash "no video" while Video is unknown.
            if (track.IsValid && track.Knows(TrackFields.Video) && !track.HasVideo) content.Add(NoVideoPlaceholder(track));
            if (track.IsValid)
            {
                content.Add(Design.Type.NowPlayingTitle(track.Title) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis });
                string artists = Entities.Strings.Resolve(track.ArtistLineId), album = track.Album.IsValid ? track.Album.Title : "";
                string meta = artists.Length == 0 ? album : album.Length == 0 ? artists : artists + " · " + album;
                if (meta.Length > 0) content.Add(Design.Type.TrackMeta(meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            }
            content.Add(new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeCardDefault });
            content.Add(Design.Type.Eyebrow(Loc.Get(Strings.Player.VideoUpNext)) with { Color = Tok.TextTertiary });
            content.Add(UpNextRows(out Element[] rows)
                ? new BoxEl { Direction = 1, Gap = 2f, Children = rows }
                : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.QueueEmpty), subtitle: ""));
            return ScrollView(new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.M),
                Children = content.ToArray(),
            }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
        }
    }

    /// <summary>The card's poster composition minus the spinner — the answer is already known. On-media INK, not
    /// <c>TextOnAccentPrimary</c> (which is black in dark theme).</summary>
    /// <para>The 16:9 sits on a PLAIN column box and the layers on a ZStack inside it (G-256): a ZStack's measure ignores
    /// <c>AspectRatio</c> and reports its tallest layer — here the square cover — so the placeholder rendered square.</para>
    static Element NoVideoPlaceholder(Track track) => new BoxEl
    {
        Shrink = 0f, AspectRatio = 16f / 9f, Direction = 1, ClipToBounds = true, Corners = Radii.CardAll, Fill = Tok.MediaLetterbox,
        Children =
        [
            new BoxEl
            {
                // Basis 0: the layers' own measure never becomes the height; the ratio's height is handed out by Grow.
                ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Grow = 1f, Opacity = 0.4f, ClipToBounds = true, Children = [Controls.ArtworkFill(Controls.ArtUrl(track.ImageId), 0f)] },
                    new BoxEl
                    {
                        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = Edges4.All(Spacing.S),
                        Children = [new TextEl(Loc.Get(Strings.Player.NoVideoForThisSong)) { Size = 12f, Weight = 600, Color = Tok.OnMediaPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
                    },
                ],
            },
        ],
    };

    // ── the friends panel ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Keyed rows in a scroller so every push-driven upsert enters/exits/FLIPs into place. Mount == visible
    /// (drives the host's watchdog); a 30 s frame-clock interval advances the live window and the relative times
    /// without a push, and auto-pauses while the window is parked.</summary>
    sealed class FriendsBody : Component
    {
        public override Element Render()
        {
            UseEffect(static () => { Friends.SetActive?.Invoke(true); return static () => Friends.SetActive?.Invoke(false); }, DepKey.Empty);
            var tick = UseSignal(0);
            UseInterval(() => tick.Value = tick.Peek() + 1, Friends.TickMs);
            _ = tick.Value;
            var state = Friends.State.Value;
            var table = Entities.Current.Edges.Friends;
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = table.Changed.Value;
            _ = Entities.Current.Users.Changed.Value;
            _ = Entities.Current.Tracks.Changed.Value;
            var rows = table.Payload(Friends.Feed);

            switch (Friends.SurfaceFor(rows.Length, state))
            {
                case Friends.Surface.Rows:
                    long now = Playback.UnixNowMs();                        // a DATE against a server epoch, not a motion clock
                    var kids = new Element[rows.Length];
                    for (int i = 0; i < rows.Length; i++) kids[i] = FriendRow(rows[i], now);
                    return FriendsShell(new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "friendspanel",
                        Content = new BoxEl { Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.M), Children = kids },
                    });
                case Friends.Surface.Skeleton:
                    var bars = new Element[5];
                    for (int i = 0; i < bars.Length; i++) bars[i] = SkeletonRow();
                    return FriendsShell(new BoxEl { Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, Spacing.XS, 0f, 0f), Children = bars });
                case Friends.Surface.Offline:
                    return Controls.Vacancy(Controls.VacancyVoice.Offline, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Friends.Offline), subtitle: "");
                case Friends.Surface.Error:
                    // A stock Standard Retry, never an accent one — and only when the host can actually retry.
                    return Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Friends.Error), subtitle: "",
                                            actionLabel: Loc.Get(Strings.Friends.Retry), onAction: Friends.Refresh);
                default:
                    return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                            title: Loc.Get(Strings.Friends.Empty), subtitle: Loc.Get(Strings.Friends.EmptyHint));
            }
        }
    }

    static BoxEl FriendsShell(Element content) => new()
    {
        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
        Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, 0f), Children = [content],
    };

    static Element FriendRow(in FriendEdge f, long now)
    {
        const float avatar = 40f;
        var user = new User(f.UserSlot);
        string name = Entities.Strings.Resolve(user.NameId);
        bool live = Friends.IsLive(now, f.TimestampMs);
        string trackTitle = f.TrackSlot > 0 ? new Track(f.TrackSlot).Title : "";
        string artistName = f.ArtistSlot > 0 ? new Artist(f.ArtistSlot).Name : "";
        string albumTitle = f.AlbumSlot > 0 ? new Album(f.AlbumSlot).Title : "";
        string contextName = f.ContextSlot > 0 ? Entities.Strings.Resolve(new Playlist(f.ContextSlot).TitleId) : "";
        string label = contextName.Length > 0 ? contextName : albumTitle.Length > 0 ? albumTitle : artistName;
        int contextSlot = f.ContextSlot, albumSlot = f.AlbumSlot, artistSlot = f.ArtistSlot;
        Action? onClick = Friends.TargetOf(contextSlot, albumSlot, artistSlot) switch
        {
            Friends.Target.Context => () => GoTo(new Playlist(contextSlot).Uri, label),
            Friends.Target.Album => () => GoTo(new Album(albumSlot).Uri, label),
            Friends.Target.Artist => () => GoTo(new Artist(artistSlot).Uri, label),
            _ => null,
        };
        bool nav = onClick is not null;

        var text = new List<Element>(3)
        {
            new TextEl(name) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            new TextEl(artistName.Length > 0 ? trackTitle + "  •  " + artistName : trackTitle)
                { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        string context = contextName.Length > 0 ? contextName : albumTitle;
        if (context.Length > 0)
            text.Add(new TextEl(context) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });

        Element picture = PersonPicture.Create("", avatar, displayName: name, imageSourcePath: Controls.ArtUrl(user.ImageId));
        if (live)
            picture = new BoxEl
            {
                Width = avatar, Height = avatar, Shrink = 0f, ZStack = true,
                Children =
                [
                    picture,
                    new BoxEl
                    {
                        Width = avatar, Height = avatar, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End, HitTestVisible = false,
                        // The ring is the opaque content rung, so the dot reads as a hole docked or floating.
                        Children = [new BoxEl { Width = 12f, Height = 12f, Corners = Radii.Circle(12f), Fill = Tok.AccentDefault, BorderWidth = 2f, BorderColor = Design.Colors.FloatingPane }],
                    },
                ],
            };

        return new BoxEl
        {
            Key = "fr:" + user.Uri.Text,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = Design.Size.TrackRowH,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
            Fill = ColorF.Transparent,                                     // no zebra on a short list
            HoverFill = nav ? Design.Colors.RowHover : ColorF.Transparent,
            PressedFill = nav ? Design.Colors.RowPressed : ColorF.Transparent,
            PressScale = Design.Motion.ScaleSubtle.PressIf(nav),
            Role = nav ? AutomationRole.Button : AutomationRole.None, Cursor = nav ? CursorId.Hand : CursorId.Arrow,
            Focusable = nav, FocusVisualMargin = Design.FocusInsetRow, OnClick = onClick,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                picture,
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f, Children = text.ToArray() },
                new BoxEl
                {
                    Width = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [live ? Controls.Equalizer(true, Tok.AccentDefault, 14f) : new TextEl(RelTime(now - f.TimestampMs)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextTertiary, MaxLines = 1 }],
                },
            ],
        };
    }

    static string RelTime(long ageMs) => Friends.Age(ageMs, out long n) switch
    {
        Friends.AgeUnit.Minutes => Strings.Friends.MinAgo(n),
        Friends.AgeUnit.Hours => Strings.Friends.HrAgo(n),
        Friends.AgeUnit.Days => Strings.Friends.DAgo(n),
        _ => Loc.Get(Strings.Friends.Now),
    };

    static Element SkeletonRow() => new BoxEl
    {
        Direction = 0, MinHeight = Design.Size.TrackRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Children =
        [
            new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = Radii.Circle(40f), Fill = Tok.FillSubtleSecondary },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = Spacing.XS,
                Children =
                [
                    new BoxEl { Width = 120f, Height = 12f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = 160f, Height = 12f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                ],
            },
        ],
    };
}
