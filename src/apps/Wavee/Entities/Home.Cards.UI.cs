// ── Entities/Home.Cards.UI.cs ──────────────────────────────────────────────────────────────────────────────────────
// the card skins: the content shapes of ch 11 (less the podium's ranked avatar, stream P3), their shared leaves, the
// accent LEAF, the now-playing mark and the hero's daylist countdown
//
// Role: UI
// Owner: P (stream P2)
// Wave: 5
// Budget: 1250 lines
// Spec: ch 11 §0-§6 (skins, tokens, colour, motion, interaction), §9.1-§9.2 (what must not be simplified, traps)
//
// ── ONE SKIN PER CONTENT SHAPE ───────────────────────────────────────────────────────────────────────────────────────
//
// Home is not one square card twelve times. A station is a round 32 avatar on a 48-DIP row, a mix is a numeral on a
// shared plate, an audiobook is a rating cluster, an episode is a 16:9 crop with a resume hairline — each shape says
// what the content IS before the text does (ch 11 §0.1). Every skin renders over a `HomeCard` HANDLE (P1's CORE,
// `Entities/Home.cs`): the handle reads its entity columns LIVE, so a card re-describes itself on the next render after
// its row hydrates, and `default(HomeCard)` is the BLANK card the skeleton seed hands every skin — every string "",
// every number 0. A skin that dereferences past that is a crash on the loading path (ch 11 §0.4).
//
// ── COLOUR ONLY THROUGH `HomeCardAccent` ─────────────────────────────────────────────────────────────────────────────
//
// The spine, the mix wash, the mix numeral, the hero backdrop/veil/CTA and the Editorial tag border are the ONLY places
// a per-item colour touches a card, and every one of them resolves through P1's `HomeCardAccent` ladder: the payload's
// `extractedColors.colorDark` first, the graded cover second, NOTHING third (ch 11 §0.3). The hero's chrome derivation
// alone falls back to the app accent, because a Play capsule must paint something; a spine never does.
//
// ── TWO LEAF COMPONENTS ──────────────────────────────────────────────────────────────────────────────────────────────
//
//   · `HomeAccentLeaf` watches ONE cover (`Palette.Watch(url)`), never a plane-wide epoch: a landed grading repaints
//     that hairline and nothing else (ch 11 §1.2, §9.2 "narrow subscriptions").
//   · `HomeNowPlaying` reads the COARSE `HasActiveContext` first and bails, so an idle page never joins the identity
//     fanout, and a track skip re-renders no card (ch 11 §0.14, parity 71).
// A ComponentEl carries NO layout props (no AlignSelf, no Height), so each leaf sits inside a plain positioning box —
// the first time the spine became a bare component every spine on the page vanished (ch 11 §9.2).

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The Home card vocabulary (ch 11 §1.1 A-K). Pure static factories over a <see cref="HomeCard"/> handle; the
/// module shells in <c>Home.UI.cs</c> arrange them and apply the entity chrome (drag + menu) once.</summary>
public static partial class HomeCards
{
    const string DisplayFace = "Segoe UI Variable Display";

    // ══ 0. THE TWO GRADING HALVES, AS DELEGATES ══════════════════════════════════════════════════════════════════════
    //
    // Allocated once. The card identity ladder reads the PAGE half (theme-following — a spine is a surface mark the
    // page's own ink sits beside); the shell wash reads the CHROME half (ch 10 §4.1 drift note: the code wins).

    /// <summary>The page-half grading (<c>Design.SchemeFor</c>) as the identity ladder's input.</summary>
    public static readonly Func<string?, Scheme?> PageScheme = static u => Design.SchemeFor(u.AsSpan());

    /// <summary>The chrome-half grading (<c>Design.ChromeSchemeFor</c>) — the shell wash's input.</summary>
    public static readonly Func<string?, Scheme?> ChromeScheme = static u => Design.ChromeSchemeFor(u.AsSpan());

    // ══ 1. THE SHARED LEAVES (ch 11 §1.1 "Shared leaves") ════════════════════════════════════════════════════════════

    /// <summary>A title line with the now-playing mark trailing it. The mark collapses to zero width when the card is not
    /// the sounding context, so the line is byte-identical to a bare title in the common case.</summary>
    public static Element Titled(Element title, string uri, float mark = 11f) => new BoxEl
    {
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Children = [title, HomeNowPlaying.Mark(uri, mark)],
    };

    /// <summary>`.spine` — the 2-DIP hairline of the item's own colour on the card's bottom edge. The POSITIONING lives
    /// on this plain box; the leaf component inside only resolves the colour (a ComponentEl has no layout props).</summary>
    public static Element Spine(in HomeCard c) => new BoxEl
    {
        Direction = 0, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = 2f, HitTestVisible = false,
        Children = [HomeAccentLeaf.Of(in c, HomeAccentLeaf.Kind.Spine)],
    };

    /// <summary>A card PLATE: contour + the card ramp + the app-wide −4 lift and 0.99 press. <c>Controls.CardPhysics</c>
    /// is applied LAST so it wins over the recipe's own 0.985 spring (ch 11 W29). ZStack so the spine can overlay the
    /// bottom edge.</summary>
    public static BoxEl Card(Element content, Action onClick, float radius, HomeCard? spine = null, float height = 0f)
    {
        Element[] kids = spine is { } s ? [content, Spine(in s)] : [content];
        return Controls.CardPhysics(new BoxEl
        {
            ZStack = true, MinWidth = 0f,
            Height = height > 0f ? height : float.NaN,
            Corners = CornerRadius4.All(radius),
            ClipToBounds = true,
            OnClick = onClick, Role = AutomationRole.Button, Focusable = true,
            FocusVisualMargin = Design.FocusInsetBordered,
            Children = kids,
        }.Interactive(Interaction.Card));
    }

    /// <summary>A tabular ROW: no contour, no spine, the transparent → subtle ramp (ch 11 §0.7).</summary>
    public static BoxEl Row(Element content, Action onClick, float height = 0f) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Height = height > 0f ? height : float.NaN,
        Corners = CornerRadius4.All(Radii.Control),
        OnClick = onClick, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
        FocusVisualMargin = Design.FocusInsetRow,
        Children = [content],
    }.Interactive(Interaction.ListRow);

    /// <summary>Art, always through the app's one artwork slot — never a hand-rolled image. <paramref name="decodePx"/>
    /// is the SQUARE decode target so several sizes of one cover share a texture (ch 10 §9 decode budget).</summary>
    public static Element Art(in HomeCard c, float w, float h, float corners, int decodePx = 0)
        => Controls.Artwork(c.ImageUrl, w, h, corners, decodePx: decodePx > 0 ? decodePx : (int)MathF.Max(w, h));

    /// <summary>`.chip` — a FILLED seed chip (a mix's seed artists).</summary>
    public static Element Chip(string text) => new BoxEl
    {
        Shrink = 0f, MinWidth = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS),
        Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
        Children = [Caption(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
    };

    /// <summary>`.tag2` — a BORDERED tag: the hero's daylist terms are labels ON a washed surface, where a filled chip
    /// would read as a second material.</summary>
    public static Element Tag(string text) => new BoxEl
    {
        Shrink = 0f, MinWidth = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS),
        Corners = Radii.ControlAll, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children = [Caption(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
    };

    static Element ChipRun(IReadOnlyList<string>? seeds, int max, bool bordered, float bottomMargin = 0f)
    {
        if (seeds is not { Count: > 0 }) return new BoxEl();
        int n = Math.Min(seeds.Count, max);
        var kids = new Element[n];
        for (int i = 0; i < n; i++) kids[i] = bordered ? Tag(seeds[i]) : Chip(seeds[i]);
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
            Margin = new Edges4(0f, 0f, 0f, bottomMargin), Children = kids,
        };
    }

    /// <summary>`.count` — small 12-px tertiary. A FIXED width where it heads a column: no tabular figures exist in the
    /// text seam, so proportional digits cannot column-align without one (ch 11 §0.6).</summary>
    public static Element Count(string text, float width = 0f) => width > 0f
        ? new BoxEl
        {
            Width = width, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Children = [Caption(text) with { Color = Tok.TextTertiary, MaxLines = 1 }],
        }
        : Caption(text) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f };

    /// <summary>The hover-revealed play affordance: opacity 0 → 1 plus the Emphatic scale, engine-serviced (no per-frame
    /// work). <c>Skeletonized(false)</c>: a hover-only affordance is not skeleton content (ch 11 §0.15).</summary>
    public static Element HoverPlay(Action onPlay, float size = 28f, bool solid = true) => new BoxEl
    {
        Width = size, Height = size, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(size),
        Fill = solid ? Tok.AccentDefault : ColorF.Transparent,
        Opacity = 0f, HoverOpacity = 1f,
        HoverScale = Design.Motion.ScaleEmphatic.Hover,
        HoverDurationMs = MotionTok.ControlFast.DurationMs, HoverEasing = MotionTok.ControlFast.Easing,
        OnClick = onPlay, Cursor = CursorId.Hand, Role = AutomationRole.Button, BlocksDragArm = true,
        Children = [Icon(Icons.Play, size <= 24f ? 11f : 13f, solid ? Tok.TextOnAccentPrimary : Tok.TextSecondary)],
    }.Skeletonized(false);

    /// <summary>The one-line title / secondary-line pair every tabular row shares: BodyStrong 14/20 over Caption 12/16
    /// (the prototype's rogue 13/17 + 11/14 ramp is NOT reproduced — ch 11 §9.5).</summary>
    public static Element TwoLine(string title, Element? second)
    {
        Element head = BodyStrong(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        return new BoxEl
        {
            Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children = second is null ? [head] : [head, second],
        };
    }

    /// <summary>The single-line secondary run.</summary>
    public static TextEl Sub(string text) => Caption(text) with
    {
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>A description as RICH TEXT — Spotify blurbs are HTML fragments; a plain text node prints the markup
    /// (ch 11 §9.1 #12). A routable anchor is an accent hyperlink that navigates with Home's origin.</summary>
    public static Element Desc(string? html, int maxLines, float size = 12f)
        => Controls.RichTextFlex(html, size, Tok.TextSecondary, Tok.AccentTextPrimary, maxLines, NavRoute);

    /// <summary>A rich-text anchor's navigation: the route key the shell's uri seam resolved, carrying Home's origin.</summary>
    static readonly Action<string> NavRoute = static key => Shell.GoTo(Shell.Parse(key), HomeCardNav.HomeOrigin);

    /// <summary>A possibly-HTML description as plain text for the string-typed consumers (a menu subtitle, a one-line
    /// caption) — P1's ported <see cref="HomeCardText.PlainText"/> (0.2.9 <c>SpotifyExportMapper.ToPlainText</c>),
    /// never null. The markup-free common case allocates nothing.</summary>
    public static string PlainText(string? html) => HomeCardText.PlainText(html) ?? "";

    // ══ 2. A · THE HERO BAND (ch 11 W1-W5, ch 10 §4.2) ═══════════════════════════════════════════════════════════════

    /// <summary>The hero band: the COMPLETE square cover integrated into the trailing material under the copy veil — or,
    /// when the payload authored a desktop header image, a full-bleed photo masthead (media → veil → copy).
    /// <para>The copy column is the band's FULL inner measure, not a left column: the artwork is a ZStack SIBLING, so a
    /// long title runs under the cover and the veil's falloff is what separates them (ch 10 W3, parity 76). Do not
    /// "fix" this into a two-column row.</para>
    /// <para>Clicks land on the ARTWORK (or the photo) only; the copy area does nothing (ch 10 W13, parity 60). The
    /// "…" carries no handler: it re-enters the context funnel and finds the band's attached menu (parity 18).</para></summary>
    public static Element HeroBand(in HomeCard c, string eyebrow, string meta, Action onPlay, Action onShuffle,
                                   Action onNav, Action onLike, IOverlayService? menuHost,
                                   Func<ContextMenuModel?>? menu, float width)
    {
        var card = c;
        ColorF accent = HomeCardAccent.AccentOrChrome(in card, Tok.AccentDefault, PageScheme);
        var metrics = HomeHeroLayout.For(width);
        TextEl title = metrics.Tier switch
        {
            HomeHeroTier.Wide => Design.Type.ArtistTitle(card.Title),
            HomeHeroTier.Medium => Design.Type.ArtistCompactTitle(card.Title),
            _ => Design.Type.PageHero(card.Title),
        };

        // The pulse slot is ALWAYS in the children list so the reserved PulseBlock matches it; a spotlight hero puts an
        // empty box there. Keyed on uri + window: a daylist rollover must REMOUNT the digits (props freeze at mount).
        long expires = card.ExpiresAtMs;
        Element pulse = expires > 0
            ? Embed.Comp(new HeroCountdown.Props(expires, accent), static () => new HeroCountdown())
                with { Key = card.Uri + ":" + expires.ToString(CultureInfo.InvariantCulture) }
            : new BoxEl();

        bool hasMenu = menu is not null && !Controls.IsNullOverlay(menuHost);
        var copy = new BoxEl
        {
            Direction = 1, Width = MathF.Max(1f, width - 2f * metrics.CopyPaddingX), Gap = 0f, MinWidth = 0f,
            Children =
            [
                // Sentence case, the string's own casing: it carries the USER'S NAME (ch 11 §6.6).
                Design.Type.Eyebrow(eyebrow) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    Margin = new Edges4(0f, 0f, 0f, Spacing.S),
                },
                title with
                {
                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    Margin = new Edges4(0f, 0f, 0f, Spacing.M),
                },
                ChipRun(card.Seeds, 6, bordered: true, bottomMargin: Spacing.M),
                meta.Length > 0
                    ? (Element)(Body(meta) with
                    {
                        Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2,
                        Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        Margin = new Edges4(0f, 0f, 0f, Spacing.L),
                    })
                    : new BoxEl(),
                pulse,
                // The app's ONE primary-action grammar: an accent Play capsule on the card's own colour, a standard
                // Shuffle capsule, then the icon arm of the same capsule for like and overflow (ch 11 §9.1 #9).
                new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        Controls.Accent(Loc.Get(Strings.Home.Play), accent, onPlay) with { Shrink = 0f },
                        Controls.Pill(Loc.Get(Strings.Detail.Shuffle), onShuffle, ButtonAppearance.Standard,
                                      glyph: Icons.Shuffle) with { Shrink = 0f },
                        // The heart never re-skins — 0.2.9's hero reads nothing back (ch 10 §6, parity 77).
                        Controls.IconPill(Icons.Heart, onLike) with { Shrink = 0f },
                        hasMenu ? Controls.IconPill(Icons.More, null, requestsContext: true) with { Shrink = 0f } : new BoxEl(),
                    ],
                }.Skeletonized(false),
            ],
        };

        var foreground = new BoxEl
        {
            Width = width, Height = metrics.Height,
            Direction = 1, AlignItems = FlexAlign.Start,
            Justify = metrics.Stacked ? FlexJustify.End : FlexJustify.Center,
            Padding = new Edges4(metrics.CopyPaddingX, metrics.CopyPaddingY, metrics.CopyPaddingX, metrics.CopyPaddingY),
            Children = [copy],
        };

        var veil = new BoxEl
        {
            Width = width, Height = metrics.Height, HitTestVisible = false,
            Gradient = Controls.ArtistHeroVeil(accent, vertical: metrics.Stacked),
        };

        BoxEl surface;
        if (card.HeaderImageUrl is { Length: > 0 } header)
        {
            int decodePx = Math.Clamp((int)MathF.Round(width), 320, 1920);
            float aspect = width / MathF.Max(1f, metrics.Height);
            var media = new BoxEl
            {
                Width = width, Height = metrics.Height,
                OnClick = onNav, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, HomeHeroLayout.ArtworkFade),
                Children =
                [
                    Image(header, ImageFit.Cover, aspect, decodePx, 0f, Design.ArtworkPlaceholder, null,
                          new ImageTransition(MotionTok.StandardEnter.DurationMs, Easing.FluentDecelerate)),
                ],
            };
            surface = new BoxEl
            {
                ZStack = true, MinWidth = 0f, Height = metrics.Height, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = [media, veil, foreground],
            };
        }
        else
        {
            var artwork = new BoxEl
            {
                Width = metrics.ArtworkSize, Height = metrics.ArtworkSize, Shrink = 0f,
                AlignSelf = FlexAlign.Center, JustifySelf = metrics.Stacked ? FlexAlign.Center : FlexAlign.End,
                OnClick = onNav, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                EdgeFade = metrics.Stacked ? null : new EdgeFadeSpec(EdgeMask.Left, HomeHeroLayout.ArtworkFade),
                Children = [Art(in card, metrics.ArtworkSize, metrics.ArtworkSize, 0f, decodePx: 512)],
            };
            surface = new BoxEl
            {
                ZStack = true, MinWidth = 0f, Height = metrics.Height, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Gradient = Controls.HomeHeroBackdrop(accent),
                Children = [artwork, veil, foreground],
            };
        }
        return hasMenu ? surface.WithContextMenu(menuHost!, menu!) : surface;
    }

    /// <summary>The daylist flip countdown (ch 11 W1 pulse row, §3.2): HH:MM:SS in fixed cells that tick through the
    /// shared <see cref="Controls.FlipDigit"/> cell, beside "Next update at {time}". A view of wall time through the
    /// FRAME clock: a wall-clock anchor plus the frame delta since, re-anchored on <see cref="DaylistCountdown"/>'s
    /// cadence so a stalled frame clock (sleep) cannot leave the digits behind. Past the window the strip goes Rolling —
    /// digits hold at zero in tertiary ink under "Updating your daylist…" and the interval keeps ticking until the new
    /// window remounts it (mount sites key on the window). Hours clamp at 99 (parity 77).</summary>
    public sealed class HeroCountdown : Component
    {
        /// <summary>Re-pushed props: the window and the chrome accent the digits take their HUE from.</summary>
        public sealed record Props(long ExpiresAtMs, ColorF Accent);

        readonly Signal<int> _tick = new(0);
        readonly Action _bump;
        long _unixAnchorMs, _frameAnchorMs, _lastTickFrameMs;
        int _lastTick;
        bool _anchored;
        long _labelFor;
        string? _nextLabel;

        public HeroCountdown() => _bump = () => _tick.Value = _tick.Peek() + 1;

        public override Element Render()
        {
            var p = UseProps<Props>();
            int tick = _tick.Value;
            long frameNow = Design.FrameTime.NowMs;
            long sinceTick = frameNow - _lastTickFrameMs;
            if (tick != _lastTick) { _lastTick = tick; _lastTickFrameMs = frameNow; }
            if (!_anchored || DaylistCountdown.NeedsReanchor(tick, sinceTick))
            {
                _anchored = true;
                _frameAnchorMs = frameNow;
                _unixAnchorMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_lastTickFrameMs == 0) _lastTickFrameMs = frameNow;
            }
            long now = DaylistCountdown.Now(_unixAnchorMs, _frameAnchorMs, frameNow);
            var phase = DaylistCountdown.PhaseOf(p.ExpiresAtMs, now);
            UseInterval(_bump, 1000f, enabled: phase != DaylistCountdown.Phase.Idle);
            if (phase == DaylistCountdown.Phase.Idle) return new BoxEl();

            bool rolling = phase == DaylistCountdown.Phase.Rolling;
            long left = DaylistCountdown.RemainingMs(p.ExpiresAtMs, now);
            // TextInk: the chrome fill painted as ink on a wash of the same hue is unreadable (ch 11 §4.1).
            ColorF ink = rolling ? Tok.TextTertiary : Design.Palette.TextInk(p.Accent);
            float row = Controls.FlipHeroRowHeight;
            var t = TimeSpan.FromMilliseconds(left);
            int hours = Math.Min(99, (int)t.TotalHours);
            if (_nextLabel is null || _labelFor != p.ExpiresAtMs)
            {
                _labelFor = p.ExpiresAtMs;
                _nextLabel = Strings.Home.NextUpdateAt(DateTimeOffset.FromUnixTimeMilliseconds(p.ExpiresAtMs).ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
            }
            string caption = rolling ? Loc.Get(Strings.Home.DaylistUpdating) : _nextLabel;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Margin = new Edges4(0f, 0f, 0f, Spacing.M),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                        Children =
                        [
                            Controls.FlipDigit(hours / 10, row, ink), Controls.FlipDigit(hours % 10, row, ink), Colon(row, ink),
                            Controls.FlipDigit(t.Minutes / 10, row, ink), Controls.FlipDigit(t.Minutes % 10, row, ink), Colon(row, ink),
                            Controls.FlipDigit(t.Seconds / 10, row, ink), Controls.FlipDigit(t.Seconds % 10, row, ink),
                        ],
                    },
                    Caption(caption) with
                    {
                        Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }

        static Element Colon(float row, ColorF ink) => new BoxEl
        {
            Width = 8f, Height = row, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [new TextEl(":") { Size = row * 0.72f, LineHeight = row, Weight = 350, Color = ink, FontFamily = DisplayFace }],
        };
    }

    // ══ 3. A2 · THE WEEKLY CARD (ch 11 W6-W7) ════════════════════════════════════════════════════════════════════════

    /// <summary>`.wcard` — padding 16, art 56 r-ctrl, a display-face 18/24/600 title, ONE plain-text sentence of blurb
    /// over two lines, a trailing count, the spine.</summary>
    public static Element WeeklyCard(in HomeCard c, Action onNav)
    {
        var card = c;
        int count = card.TrackCount;
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = Edges4.All(Spacing.L),
            Children =
            [
                Art(in card, Design.Size.Thumb56, Design.Size.Thumb56, Radii.Control, decodePx: 128),
                new BoxEl
                {
                    Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        Titled(BodyLarge(card.Title) with
                        {
                            Weight = 600, FontFamily = DisplayFace, CharSpacing = -8f,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        }, card.Uri, 12f),
                        Caption(HomeCardText.FirstSentence(card.Subtitle)) with
                        {
                            Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
                count > 0 ? Count(count.ToString(CultureInfo.CurrentCulture)) : new BoxEl(),
            ],
        };
        return Card(body, onNav, Radii.Card, spine: card);
    }

    // ══ 4. B · THE JUMP-BACK-IN TILE (ch 11 W8) ══════════════════════════════════════════════════════════════════════

    /// <summary>`.qtile` — exactly 56 tall (its cover), art flush to the leading edge, a 2-line BodyStrong title, the
    /// now-playing mark BEFORE the hover play, the spine.</summary>
    public static Element QuickTile(in HomeCard c, Action onNav, Action onPlay)
    {
        var card = c;
        var body = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Children =
            [
                Art(in card, Design.Size.Thumb56, Design.Size.Thumb56, Radii.Control, decodePx: 128),
                BodyStrong(card.Title) with
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Margin = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
                HomeNowPlaying.Mark(card.Uri, 12f),
                new BoxEl { Padding = new Edges4(0f, 0f, Spacing.M, 0f), Shrink = 0f, Children = [HoverPlay(onPlay)] },
            ],
        };
        return Card(body, onNav, Radii.Control, spine: card, height: Design.Size.Thumb56);
    }

    // ══ 5. D · ONE CELL OF THE DAILY-MIX BAND (ch 11 W10-W12) ════════════════════════════════════════════════════════

    /// <summary>`.bseg` — a cell of ONE shared plate: the mix's own-colour numeral, a "DAILY MIX" eyebrow (authored caps,
    /// no ToUpper), the seeds on a 3-line clamp, a 9% wash, the spine, and the internal rules a grid cannot place between
    /// its tracks. <paramref name="ordinal"/> is the card's POSITION, never parsed out of a localized title. Hover changes
    /// only the fill — lifting a cell would tear the plate.</summary>
    public static Element MixSegment(in HomeCard c, int ordinal, Action onNav, bool leading, bool above)
    {
        var card = c;
        var content = new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Padding = Edges4.All(Spacing.L),
            Children =
            [
                HomeAccentLeaf.Numeral(in card, Ordinal(ordinal)),
                Titled(Design.Type.Eyebrow(Loc.Get(Strings.Home.DailyMix)) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Shrink = 1f, MinWidth = 0f,
                }, card.Uri, 10f),
                card.Seeds is { Count: > 0 } seeds
                    ? (Element)(Caption(string.Join(" · ", seeds)) with
                    {
                        Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3,
                        Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        Margin = new Edges4(0f, 0f, 0f, Spacing.XXS),
                    })
                    : new BoxEl(),
            ],
        };
        var layers = new List<Element>(5)
        {
            new BoxEl
            {
                Direction = 0, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Children = [HomeAccentLeaf.Of(in card, HomeAccentLeaf.Kind.Wash)],
            },
            content,
            Spine(in card),
        };
        if (leading)
            layers.Add(new BoxEl { Width = 1f, JustifySelf = FlexAlign.Start, Fill = Tok.StrokeDividerDefault, HitTestVisible = false });
        if (above)
            layers.Add(new BoxEl
            {
                Height = 1f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch,
                Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
            });
        return new BoxEl
        {
            ZStack = true, MinWidth = 0f, ClipToBounds = true,
            OnClick = onNav, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary,
            BrushTransitionMs = Design.Motion.Faster,
            Children = layers.ToArray(),
        };
    }

    static readonly string[] s_ordinals = ["1", "2", "3", "4", "5", "6", "7", "8", "9"];
    static string Ordinal(int n) => (uint)(n - 1) < (uint)s_ordinals.Length ? s_ordinals[n - 1] : n.ToString(CultureInfo.CurrentCulture);

    // ══ 6. F · THE CHIP CARD (ch 11 W18) ═════════════════════════════════════════════════════════════════════════════

    /// <summary>`.ccard` — padding 12, art 64, a BodyStrong title, up to three filled seed chips (or, with no seeds, the
    /// description as rich text over two lines), "{n} songs", the spine.</summary>
    public static Element ChipCard(in HomeCard c, Action onNav)
    {
        var card = c;
        int count = card.TrackCount;
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, MinWidth = 0f, Grow = 1f, AlignItems = FlexAlign.Start,
            Padding = Edges4.All(Spacing.M),
            Children =
            [
                Art(in card, Design.Size.Thumb64, Design.Size.Thumb64, Radii.Control, decodePx: 128),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        Titled(BodyStrong(card.Title) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        }, card.Uri),
                        card.Seeds is { Count: > 0 } ? ChipRun(card.Seeds, 3, bordered: false) : Desc(card.Subtitle, 2),
                        count > 0 ? Count(Strings.Detail.SongCount(count)) : new BoxEl(),
                    ],
                },
            ],
        };
        return Card(body, onNav, Radii.Control, spine: card);
    }

    // ══ 7. G · THE RADIO DIAL ROW (ch 11 W19) ════════════════════════════════════════════════════════════════════════

    /// <summary>`.drow` — 48 tall, a ROUND 32 avatar (a station stands for an artist), the name over its seeds joined
    /// ", " (else the flattened description), the mark, a 24-DIP GHOST play. No spine, no lift.</summary>
    public static Element RadioRow(in HomeCard c, Action onNav, Action onPlay)
    {
        var card = c;
        string seeds = card.Seeds is { Count: > 0 } s ? string.Join(", ", s) : PlainText(card.Subtitle);
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children =
            [
                Art(in card, Design.Size.Thumb32, Design.Size.Thumb32, Radii.Full, decodePx: 64),
                TwoLine(card.Title, Sub(seeds) with { Color = Tok.TextSecondary }),
                HomeNowPlaying.Mark(card.Uri, 11f),
                HoverPlay(onPlay, 24f, solid: false),
            ],
        };
        return Row(body, onNav, 48f);
    }

    // ══ 8. H1 · THE EPISODE QUEUE ROW (ch 11 W20-W21) ════════════════════════════════════════════════════════════════

    /// <summary>`.qrow` — artwork that ENCODES THE MEDIUM (56×32 16:9 with video, 32×32 without), a resume hairline
    /// across the art when partly played, "Video" + the show, and a fixed 56-DIP time cell. A 1-DIP bottom rule on all
    /// but the last row makes the stack read as a table (ch 11 §9.1 #4).</summary>
    public static Element QueueRow(in HomeCard c, Action onNav, bool last)
    {
        var card = c;
        bool video = card.HasVideo;
        float artW = video ? Design.Size.Thumb56 : Design.Size.Thumb32;
        long duration = card.DurationMs, resume = card.ResumeMs;
        float progress = duration > 0 && resume > 0 ? Math.Clamp((float)resume / duration, 0f, 1f) : 0f;

        Element art = new BoxEl
        {
            Width = artW, Height = Design.Size.Thumb32, Shrink = 0f, ZStack = true, ClipToBounds = true,
            Corners = Radii.ControlAll,
            Children = progress > 0f
                ? [Art(in card, artW, Design.Size.Thumb32, Radii.Control, decodePx: 64), ResumeHairline(artW, progress)]
                : [Art(in card, artW, Design.Size.Thumb32, Radii.Control, decodePx: 64)],
        };
        string show = PlainText(card.Subtitle);
        var showLine = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = video
                ? [Sub(Loc.Get(Strings.Home.Video)) with { Color = Tok.TextTertiary, Shrink = 0f },
                   Sub(show) with { Color = Tok.TextSecondary }]
                : [Sub(show) with { Color = Tok.TextSecondary }],
        };
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = Edges4.All(Spacing.S),
            Children =
            [
                art,
                TwoLine(card.Title, showLine),
                new BoxEl
                {
                    Width = 56f, Shrink = 0f, Direction = 1, Gap = 0f, AlignItems = FlexAlign.End,
                    Children =
                    [
                        Sub(HomeCardText.Duration(duration)) with { Color = Tok.TextSecondary },
                        Sub(Loc.Get(progress > 0f ? Strings.Home.Resume : Strings.Home.Unplayed)) with { Color = Tok.TextTertiary },
                    ],
                },
            ],
        };
        var row = Row(body, onNav);
        return last ? row : row with
        {
            ZStack = true,
            Children =
            [
                .. row.Children!,
                new BoxEl
                {
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = 1f,
                    Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                },
            ],
        };
    }

    static Element ResumeHairline(float width, float progress) => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Direction = 0, HitTestVisible = false,
        Children = [new BoxEl { Width = MathF.Max(2f, width * progress), Height = 2f, Fill = Tok.AccentDefault }],
    };

    // ══ 9. H2 · THE AUDIOBOOK ROW (ch 11 W20) ════════════════════════════════════════════════════════════════════════

    /// <summary>`.brow` — art 48, title over author, the mark, and a rating cluster: the STOCK read-only star strip, the
    /// value to two decimals, the length in a fixed 32-DIP cell (ch 11 §9.1 #11). A card with no rating keeps only the
    /// length cell.</summary>
    public static Element BookRow(in HomeCard c, Action onNav)
    {
        var card = c;
        double rating = card.Rating;
        string hours = HomeCardText.Hours(card.DurationMs);
        Element cluster = rating > 0
            ? new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    RatingControl.Create(placeholder: (float)rating, readOnly: true),
                    Caption(rating.ToString("0.00", CultureInfo.CurrentCulture)) with { Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                    Count(hours, Design.Size.Thumb32),
                ],
            }
            : Count(hours, Design.Size.Thumb32);
        string by = card.Author is { Length: > 0 } author ? author : PlainText(card.Subtitle);
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = Edges4.All(Spacing.S),
            Children =
            [
                Art(in card, Design.Size.Thumb48, Design.Size.Thumb48, Radii.Control, decodePx: 128),
                TwoLine(card.Title, Sub(by) with { Color = Tok.TextSecondary }),
                HomeNowPlaying.Mark(card.Uri, 11f),
                cluster,
            ],
        };
        return Row(body, onNav);
    }

    // ══ 10. J · THE EDITORIAL FEATURE AND ITS CROWD ROWS (ch 11 W22) ═════════════════════════════════════════════════

    /// <summary>`.feature` — padding 20, art 148 r-card, an accent-bordered "Editorial" tag, a display 20/28 title, three
    /// lines of rich blurb, and a footer pinned to the bottom (Play + meta) so it lands level with the companion
    /// column's last row. The spine.</summary>
    public static Element FeatureCard(in HomeCard c, string meta, Action onNav, Action onPlay)
    {
        var card = c;
        ColorF accent = HomeCardAccent.AccentOrChrome(in card, Tok.AccentDefault, PageScheme);
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.XL, MinWidth = 0f, Grow = 1f, AlignItems = FlexAlign.Start,
            Padding = Edges4.All(Spacing.XL),
            Children =
            [
                Art(in card, 148f, 148f, Radii.Card, decodePx: 256),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, Shrink = 0f,
                            Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS),
                            Corners = Radii.ControlAll,
                            BorderWidth = 1f, BorderColor = ColorF.Lerp(Tok.StrokeControlDefault, accent, 0.40f),
                            Children = [Design.Type.Eyebrow(Loc.Get(Strings.Home.Editorial)) with { Color = Design.Accent.Decor, MaxLines = 1 }],
                        },
                        Titled(Subtitle(card.Title) with
                        {
                            FontFamily = DisplayFace, CharSpacing = -12f, Wrap = TextWrap.Wrap, MaxLines = 2,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        }, card.Uri, 13f),
                        Desc(card.Subtitle, 3),
                        new BoxEl { Grow = 1f, MinHeight = 0f },
                        new BoxEl
                        {
                            Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Padding = new Edges4(0f, Spacing.M, 0f, 0f),
                            Children = [Controls.Play(accent, onPlay, Loc.Get(Strings.Home.Play)), Count(meta)],
                        }.Skeletonized(false),
                    ],
                },
            ],
        };
        return Card(body, onNav, Radii.Card, spine: card);
    }

    /// <summary>`.crowd` — padding 12, art 48, a BodyStrong title over one line of blurb, a 30-DIP ghost play, the spine.
    /// Grows to fill the companion column beside the feature.</summary>
    public static Element CrowdRow(in HomeCard c, Action onNav, Action onPlay)
    {
        var card = c;
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = Edges4.All(Spacing.M),
            Children =
            [
                Art(in card, Design.Size.Thumb48, Design.Size.Thumb48, Radii.Control, decodePx: 128),
                new BoxEl
                {
                    Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        BodyStrong(card.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Desc(card.Subtitle, 1),
                    ],
                },
                HoverPlay(onPlay, 30f, solid: false),
            ],
        };
        return Card(body, onNav, Radii.Control, spine: card) with { Grow = 1f };
    }

    // ══ 11. I · THE WHAT'S-NEW TIMELINE ROW (ch 11 W23) ══════════════════════════════════════════════════════════════

    /// <summary>`.tlrow` — a 7-DIP pip with a 1.5-DIP ring straddling the day column's rule at −4 (filled accent unread,
    /// hollow seen), art 40 (ROUND for an act), a bordered kind tag + meta, and a trailing "New" pill or "Seen". Plain
    /// fields, never a notification record: two sources share this one anatomy and the row must not type-test.</summary>
    public static Element TimelineRow(string? imageUrl, string title, string kindLabel, string meta, bool unread,
                                      Action onNav, float artRadius = 0f)
    {
        float corners = artRadius > 0f ? artRadius : Radii.Control;
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.S, Spacing.S),
            Children =
            [
                Controls.Artwork(imageUrl, Design.Size.Thumb40, Design.Size.Thumb40, corners, decodePx: 64),
                new BoxEl
                {
                    Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        BodyStrong(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children = [KindTag(kindLabel), Sub(meta) with { Color = Tok.TextSecondary }],
                        },
                    ],
                },
                unread ? NewPill() : Count(Loc.Get(Strings.Home.Seen)),
            ],
        };
        return new BoxEl
        {
            ZStack = true, MinWidth = 0f,
            Children =
            [
                Row(body, onNav),
                new BoxEl
                {
                    Width = 7f, Height = 7f, Shrink = 0f,
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(-4f, 0f, 0f, 0f),
                    Corners = Radii.Circle(7f),
                    Fill = unread ? Tok.AccentDefault : Tok.FillLayerDefault,
                    BorderWidth = 1.5f, BorderColor = unread ? Tok.AccentDefault : Tok.StrokeControlStrongDefault,
                    HitTestVisible = false,
                },
            ],
        };
    }

    /// <summary>`.kindtag` — a bordered eyebrow type label ("Releases" / "Podcast" / "Concert").</summary>
    static Element KindTag(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = Radii.ControlAll, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children = [Design.Type.Eyebrow(text) with { Color = Tok.TextTertiary, MaxLines = 1 }],
    };

    /// <summary>`.newpill` — the ONE accent plate behind text on Home.</summary>
    static Element NewPill() => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
        Children = [Design.Type.Eyebrow(Loc.Get(Strings.Home.NewBadge)) with { Color = Tok.TextOnAccentPrimary, MaxLines = 1 }],
    };

    // ══ 12. THE SHELF CARD ADAPTER (Controls' shared plate, configured — never re-created) ═══════════════════════════

    /// <summary>What a shelf card PAINTS, as a value: <c>PagedShelf</c>'s props gate compares items by VALUE and ignores
    /// the card builder, so the strings a card shows must be IN the item or a hydrated title would never reach a retained
    /// card (component-props-contract "Retained shelf authoring").</summary>
    public readonly record struct ShelfItem(HomeCard Card, string Title, string Second, string? Art, bool Circular);

    /// <summary>Snapshot a card for a shelf. <paramref name="second"/> is the card's second line (a kind word, a
    /// subtitle, a reason) — resolved by the module, because what that line NAMES differs per module (ch 11 W9 vs W27).</summary>
    public static ShelfItem ShelfItemOf(in HomeCard c, string second, bool circular)
        => new(c, c.Title, second, c.ImageUrl, circular);

    /// <summary>A shelf cell over <see cref="Controls.ShelfCard"/>: the shared plate, hover physics, "…" corner and play
    /// FAB. The entity chrome (drag + menu) rides a column wrapper, because the card itself is a component.</summary>
    public static Element ShelfCell(in ShelfItem item, float cardW, Action onNav, Action onPlay,
                                    DragSource? drag, IOverlayService? menuHost, Func<ContextMenuModel?>? menu)
    {
        bool hasMenu = menu is not null && !Controls.IsNullOverlay(menuHost);
        Element? second = item.Second.Length > 0
            ? Design.Type.TrackMeta(item.Second) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
            : null;
        var data = new Controls.CardData(item.Card.Uri, item.Title, second, item.Art, onNav, onPlay,
                                         Circular: item.Circular, Drag: drag, ShowMenu: hasMenu);
        var cell = new BoxEl { Direction = 1, Shrink = 0f, Children = [Controls.ShelfCard(data, cardW)] };
        return hasMenu ? cell.WithContextMenu(menuHost!, menu!) : cell;
    }
}

// ══ 13. THE NOW-PLAYING MARK (ch 11 §0.14, W30) ══════════════════════════════════════════════════════════════════════

/// <summary>The now-playing equalizer for a Home card. A COMPONENT, not an inline read: the playback relation is read
/// COARSE-FIRST through the <see cref="Controls.NowPlaying"/> seam — only <c>HasActiveContext</c> on an idle page — and
/// bridged into ONE <c>(here, playing)</c> signal whose write suppresses on equality, so an unrelated track skip
/// schedules no card render (parity 71). Three states: not the context ⇒ a 0 × 0 box (no gap); the context, playing ⇒
/// animated bars; the context, paused ⇒ the same mark, settled flat (ch 11 §5 "paused"). A null seam renders nothing.</summary>
public static class HomeNowPlaying
{
    /// <summary>Mount the mark for <paramref name="uri"/> at <paramref name="height"/> DIP (12 quick tile / weekly, 10 mix
    /// cell, 11 rows, 13 feature).</summary>
    public static Element Mark(string uri, float height = 12f)
        => Embed.Comp(new Props(uri, height), static () => new Host());

    sealed record Props(string Uri, float Height);

    sealed class Host : Component
    {
        // The card's uri as a SIGNAL the bridge reads: a signal effect is created once, so a recycled slot's re-pushed
        // uri must reach it through a signal (written from an effect, never during render) — not a captured closure.
        readonly Signal<string> _uri = new("");
        readonly Signal<(bool Here, bool Playing)> _vis = new((false, false));

        public override Element Render()
        {
            var p = UsePropsOrDefault<Props>();
            string uri = p?.Uri ?? "";
            UseEffect(() => { if (!string.Equals(_uri.Peek(), uri, StringComparison.Ordinal)) _uri.Value = uri; },
                      DepKey.From(StringComparer.Ordinal.GetHashCode(uri)));
            UseSignalEffect(() =>
            {
                string u = _uri.Value;
                var seam = Controls.NowPlaying;
                if (u.Length == 0 || seam is null || !seam.HasActiveContext.Value)   // COARSE first: one app-wide bool
                {
                    if (_vis.Peek() != (false, false)) _vis.Value = (false, false);
                    return;
                }
                bool here = seam.RelatesTo(u);
                var next = (here, here && seam.IsPlaying.Value);
                if (_vis.Peek() != next) _vis.Value = next;
            });
            var (here, _) = _vis.Value;
            var playing = Controls.NowPlaying?.IsPlaying;
            if (p is null || !here || playing is null) return new BoxEl { Width = 0f, Height = 0f };
            return new BoxEl
            {
                Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Controls.Equalizer(playing, static () => Tok.AccentTextPrimary, p.Height)],
            };
        }
    }
}

// ══ 14. THE ACCENT LEAF (ch 11 §1.2, §4.1) ═══════════════════════════════════════════════════════════════════════════

/// <summary>The leaf that resolves a card's identity colour and paints with it — the spine hairline, the mix cell's wash,
/// the mix cell's numeral. It subscribes NARROWLY (<c>Palette.Watch</c> on THIS card's cover), so a landed grading
/// repaints one leaf, and it paints NOTHING until a real colour exists — except the numeral, which is content and keeps
/// its slot in primary ink until the grading lands (ch 11 §4.1).</summary>
public static class HomeAccentLeaf
{
    /// <summary>Which mark the leaf paints.</summary>
    public enum Kind : byte { Spine, Wash, Numeral }

    /// <summary>The spine or the wash leaf for <paramref name="c"/>.</summary>
    public static Element Of(in HomeCard c, Kind kind)
        => Embed.Comp(new Props(c, kind, null), static () => new Host());

    /// <summary>The mix numeral leaf.</summary>
    public static Element Numeral(in HomeCard c, string text)
        => Embed.Comp(new Props(c, Kind.Numeral, text), static () => new Host());

    sealed record Props(HomeCard Card, Kind Which, string? Text);

    sealed class Host : Component
    {
        public override Element Render()
        {
            var p = UsePropsOrDefault<Props>();
            if (p is null) return new BoxEl();
            var card = p.Card;
            // Reading the watch IS the subscription; a card with no cover takes none (an unkeyable url would answer
            // with the plane's global signal — exactly the fan-out this leaf exists to avoid).
            if (card.ImageUrl is { Length: > 0 } url) _ = Palette.Watch(url.AsSpan()).Value;

            switch (p.Which)
            {
                case Kind.Spine:
                    // The explicit HoverFill is load-bearing: the recorder opts a hit-test-free descendant into the
                    // card's inherited hover cross-fade only because it declares one (ch 11 §0.2).
                    return new BoxEl
                    {
                        Grow = 1f, Height = 2f, HitTestVisible = false,
                        Fill = HomeCardAccent.SpineAccent(in card, false, HomeCards.PageScheme) ?? ColorF.Transparent,
                        HoverFill = HomeCardAccent.SpineAccent(in card, true, HomeCards.PageScheme) ?? ColorF.Transparent,
                    };
                case Kind.Wash:
                    return new BoxEl
                    {
                        Grow = 1f, AlignSelf = FlexAlign.Stretch, HitTestVisible = false,
                        Fill = HomeCardAccent.Accent(in card, HomeCards.PageScheme) is { } wash ? wash with { A = 0.09f } : ColorF.Transparent,
                    };
                default:
                    return Ui.Title(p.Text ?? "") with
                    {
                        FontFamily = "Segoe UI Variable Display", CharSpacing = -40f, MaxLines = 1,
                        Color = HomeCardAccent.Accent(in card, HomeCards.PageScheme) ?? Tok.TextPrimary,
                        Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
                    };
            }
        }
    }
}
