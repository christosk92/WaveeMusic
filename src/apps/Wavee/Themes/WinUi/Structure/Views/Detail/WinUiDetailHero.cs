// ── Themes/WinUi/Structure/Views/Detail/WinUiDetailHero.cs — WinUI's detail hero, its sticky bar and its loading band (themes plan 6.2; P6 W6a) ──────────
//
// `Detail.Hero` is item 0 of the core's vertical list: a plain box the table pins with `Sticky(0)` and collapses with `Collapse` around the view this file draws.
// The view draws two presentations of ONE page identity and the table's scroll binds cross them:
//
//   · THE EXPANDED HERO (boards W-Album, -Small, -Huge, -Huge4K; -Compact stacked): the cover (r8, the card shadow) beside the words, bottom-aligned: the eyebrow,
//     the title (Title Large 40/52), the billed artists with their avatars and the meta on one line, the action row; under it the table's own list bar (Find,
//     the order, Filter, Select). A SHORT window (under 720 tall, W-Shell-Short) draws the short form: a 104 cover, the title 28/36, no eyebrow.
//   · THE STICKY BAR (W-Shell-Short): 48 tall, once the hero has collapsed: a 32 cover, the title over the byline and Play as an accent button, on a layer of its
//     own with a hairline under it. The core's collapse distance is the hero's measured height less these 48 (`DetailTierLayout.BandHeight`); it reveals over the
//     last 44 of the collapse and takes hits only once the table's chrome has stuck (the engaged edge, `CompactInteractive`).
//
// The numbers are `WinUiDetailRules` and the tier's `WinUiDetailTier`; the form (tall, short, stacked) is the tier's and the window's height (`TierFacts.Height`).
// THE LOADING BAND (`StyleKeys.DetailHero`): the same parts at the same sizes, unfilled, so the swap to the loaded hero never moves a row.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using HeroParts = Wavee.Detail.HeroParts;
using VerticalLayout = Wavee.Detail.VerticalLayout;
using VerticalSpec = Wavee.Detail.VerticalSpec;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiDetailFrame
{
    // ══ 1. THE HERO ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly LayoutTransition HeroGeometryMotion = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(280f, Easing.SmoothOut), SizeMode.ScaleCorrect);

    static readonly LayoutTransition HeroReflowMotion = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(280f, Easing.SmoothOut), SizeMode.Reveal);

    /// <summary>The expanded hero's height across a tier crossing (the short form under 720 tall, a wider window's larger cover): it eases through REAL
    /// layout, so the list rows under the hero (the table hosts it as its item 0) ride it frame by frame instead of stepping the whole change at once.</summary>
    static readonly LayoutTransition HeroHeightMotion = new(
        TransitionChannels.Size, TransitionDynamics.Tween(280f, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>The hero's loading band, as the style the table reads (<see cref="StyleKeys.DetailHero"/>); the list's page inset eases through real layout when
    /// a tier changes it (audit B9: the rows, the chrome and the tail ride it instead of stepping).</summary>
    public static readonly DetailHeroStyle HeroStyle = new() { Skeleton = HeroSkeleton, InsetMotion = WinUiDiscoveryMotion.WidthReflow };

    /// <summary>The registered template of <see cref="ViewKeys.DetailHero"/>.</summary>
    public static Element HeroTemplate(DetailHeroProps p, in ViewContext ctx) => Embed.Comp(p, static () => new HeroHost());

    /// <summary>The hero host: the expanded presentation and the stuck bar.</summary>
    sealed class HeroHost : Component, IPropsHost
    {
        DetailHeroProps? _latest;
        readonly Signal<DetailHeroProps?> _props = new(null);
        readonly Action<RectF> _measure;
        float _lastMeasuredH;

        public HeroHost() => _measure = Measure;

        public void ApplyProps(object props)
        {
            _latest = (DetailHeroProps)props;
            _props.Value = _latest;
        }

        void Measure(RectF r)
        {
            if (r.H <= 1f) return;
            if (MathF.Abs(r.H - _lastMeasuredH) <= 1f) return;
            _lastMeasuredH = r.H;
            _latest?.Parts.OnHeroMeasured?.Invoke(r.H);   // the table republishes its collapse height
        }

        public override Element Render()
        {
            _ = _props.Value;
            // The tiers this hero is drawn in (its root's facts), never the process's last window.
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var p = _latest!;
            var spec = p.Spec;
            var parts = p.Parts;
            var id = spec.Identity;
            var tier = WinUiDetailTiers.Of(in tiers);
            var form = WinUiDetailTiers.HeroOf(in tiers);
            float inset = tier.InsetX;
            bool compactCanHit = parts.CompactInteractive.Value;   // subscribe: the stuck edge moves input ownership
            float availW = parts.ColumnWidth > 0f ? parts.ColumnWidth : VerticalLayout.FallbackW;
            float measure = MathF.Max(0f, availW - 2f * inset - (form.Stacked ? 0f : form.Cover + form.Gap));
            bool words = !form.Stacked && availW - 2f * inset >= WinUiDetailRules.LabelledActionsMin;
            bool wordsStacked = form.Stacked;   // Compact: glyphs alone

            // ── the words' column, row by row (each keyed; a late row fades up, every row FLIPs) ──
            // A LIST's stacked hero (W-Playlist-Compact, W-PlaylistFollowed-Compact) keeps the cover centred and starts its words at the gutter; an album's centres both.
            bool center = Centered(form, id.Kind);
            float avatar = form.AvatarFor(id.Kind);
            var rows = new List<Element>(6);
            string description = HomeCards.PlainText(id.DescriptionHtml);
            if (form.Eyebrow && EyebrowOf(id, form, description) is { Length: > 0 } eyebrow)
                rows.Add(Block("hero-eyebrow", ChipRow(id, spec.Config)
                    ? WinUiDetailKit.EyebrowWithChip(eyebrow, form, WinUiDetailKit.SaveTargetOf(id).Text, center)
                    : WinUiDetailKit.EyebrowRun(eyebrow, form) with { AlignSelf = center ? FlexAlign.Center : FlexAlign.Auto }, late: true));
            // An album not read yet holds its eyebrow's line ("Album · 2020" lands into it), as the names and the meta below (audit B7).
            else if (form.Eyebrow && id.HeaderPending && id.Kind == DetailKind.Album)
                rows.Add(Block("hero-eyebrow", Reserved(null, form.EyebrowLine, center)));
            rows.Add(Block("hero-title", TitleOf(spec, form, center)));
            // The description under the title (W-Playlist: the owner's own list edits it in place, "Add an optional description" while empty; W-PlaylistFollowed: the
            // list's own words). It lands with the header, as the title does.
            if (form.Description && id.Kind == DetailKind.Playlist)
            {
                if (id.EditableMetadata && spec.Slots.Description is { } editDescription)
                    rows.Add(Block("hero-description", editDescription(measure), late: true));
                else if (description.Length > 0)
                    rows.Add(Block("hero-description", WinUiDetailKit.DescriptionRun(description, center), late: true));
            }
            // THE RESERVED LINES (the owner's recording: the title jumped 37 up when "50 songs · 519.6K saves" arrived, the buttons rode the facts after it). A line
            // the page can still fill (a meta loading, an owner, billed artists) holds its fixed box from the first frame; its words fade in inside it, and a
            // later fact (the duration) fills in place. Nothing above or below it ever moves.
            // The line FILLS ONCE (the owner's recording: "1494 songs · 49.8K saves", then the owner and "· 98 hr 18 min" a moment later, three pop-ins):
            // while a part of it is still on its way (the count held with the list, Identity.MetaLoading; the duration or the owner, Identity.MetaSettling) the box
            // stays reserved and empty, and the words fade in whole.
            var faces = FacesOf(id);
            // A head not read yet holds its KIND's lines (audit B7: the stacked album hero grew 40 when its artists and meta landed): an album states its
            // billed artists and its meta, a playlist its owner and meta; the words fade into the reserved boxes.
            bool kindLines = id.HeaderPending && id.Kind is DetailKind.Album or DetailKind.Playlist;
            bool metaLine = id.MetaLoading || id.Meta is { Length: > 0 } || kindLines;
            bool settled = !id.MetaSettling && !id.MetaLoading;
            if (center)
            {
                if (faces is { Count: > 0 }) rows.Add(Block("hero-attribution", WinUiDetailKit.NamesLine(faces, measure), late: true));
                else if (kindLines) rows.Add(Block("hero-attribution", Reserved(null, WinUiDetailRules.AttributionLine, center)));
                if (metaLine) rows.Add(Block("hero-meta", Reserved(settled && id.Meta is { Length: > 0 } meta ? MetaRun(meta, form) : null, WinUiDetailRules.MetaLine, center)));
            }
            else if (faces is { Count: > 0 } || metaLine)
            {
                bool filled = settled && (faces is { Count: > 0 } || id.Meta is { Length: > 0 });
                rows.Add(Block("hero-attribution", Reserved(filled ? WinUiDetailKit.AttributionLine(faces, id.Meta, avatar, measure) : null,
                    WinUiDetailRules.AttributionRow(avatar), center)));
            }
            // A daylist's strip under the facts (its countdown, or "Your next daylist is running late"): the page's pulse slot, as Classic's rail draws it.
            if (Pulses(spec)) rows.Add(Block("hero-daylist", PulseRow(spec.Slots.Pulse!(), center)));
            rows.Add(Block("hero-actions", Actions(id, spec.Config, spec.Actions, spec.Slots, spec.PlayAll, tier, form, words, center)));

            Element cover = Cover(spec.Identity, spec.Slots, spec.Actions, form);
            Element hero = Compose(form, inset, cover, rows.ToArray(), toolbar: ToolbarOf(parts, tier, form), center);

            Element expanded = new BoxEl { Direction = 1, Animate = HeroHeightMotion, OnBoundsChanged = _measure, Children = [hero] };

            // ── the STICKY BAR: the title over the byline, Play on the right; NO scrolling of its own (the offset model: the rows are clipped under it) ──
            float heroH = parts.HeroHeight > 1f ? parts.HeroHeight : Detail.HeroBandHeight(spec, availW);
            float cd = VerticalLayout.CollapseDistance(heroH, WinUiDetailRules.BandHeight);
            Element normalBar = Bar(spec, parts, availW, inset);
            Element barContent = normalBar;
            if (parts.SelectionBar is { } selectionBar)
            {
                var selectionVisible = parts.SelectionVisible;
                Element selectionBand = new BoxEl
                {
                    Direction = 1, Width = availW, Height = WinUiDetailRules.BandHeight, Padding = new Edges4(inset, 0f, inset, 0f),
                    Justify = FlexJustify.Center, HitTestVisible = true, Children = [selectionBar],
                };
                barContent = new BoxEl
                {
                    ZStack = true, Width = availW, Height = WinUiDetailRules.BandHeight,
                    Children = [Flow.Show(() => !selectionVisible.Value, normalBar), Flow.Show(() => selectionVisible.Value, selectionBand)],
                };
            }

            // Input ownership crosses WITH the bar: it takes hits only once stuck.
            Element compact = new BoxEl
            {
                ZStack = true, Width = availW, Height = WinUiDetailRules.BandHeight,
                HitTestVisible = compactCanHit, HitTestPassThrough = true, Children = [barContent],
            }.Reveal(VerticalLayout.CompactRevealStart(cd), cd - VerticalLayout.CompactRevealStart(cd), Design.Reduced ? 0f : Spacing.XS);

            // The scrolled-away hero stops eating clicks at the same edge; it rides the scroll back out of the pinned hero and fades over the tail of it.
            Element presentation = (ZStack(expanded) with { Direction = 1, HitTestVisible = !compactCanHit })
                .Parallax(0.0, cd, 0f, -cd)
                .Fade(VerticalLayout.ExpandedFadeStart(cd), cd, 1f, 0f);
            return ZStack(presentation, compact) with { Direction = 1 };
        }
    }

    // ══ 2. THE PARTS ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A keyed block: a position-only FLIP always, a fade-up entrance when it can arrive late.</summary>
    static BoxEl Block(string key, Element child, bool late = false) => new()
    {
        Key = key, Direction = 1, Layout = WinUiDetailKit.Shove, Enter = late ? WinUiDetailKit.FadeUp : (EnterExit?)null, Children = [child],
    };

    /// <summary>Are the stacked hero's words centred? An album's, a show's, an episode's (W-Album-Compact); a list's start at the gutter under the centred cover
    /// (W-Playlist-Compact). The horizontal forms never centre.</summary>
    static bool Centered(WinUiHeroForm form, DetailKind kind) => form.Stacked && kind is not (DetailKind.Playlist or DetailKind.Liked);

    /// <summary>The eyebrow's words: "Playlist · public" for a public list (W-Playlist), else the page's; where the form draws no description line, a followed
    /// list's description rides it ("Playlist · The hottest 50.", W-PlaylistFollowed-Small).</summary>
    static string EyebrowOf(Detail.Identity id, WinUiHeroForm form, string description)
    {
        string eyebrow = id.Kind == DetailKind.Playlist && id.IsPublic && !id.Collaborative ? Loc.Get(Strings.Nav.PlaylistPublic) : id.Eyebrow;
        if (!form.Description && id.Kind == DetailKind.Playlist && !id.EditableMetadata && description.Length > 0)
            eyebrow = eyebrow.Length > 0 ? eyebrow + WinUiDetailRules.MetaLead + description : description;
        return eyebrow;
    }

    /// <summary>Does the eyebrow carry the "Saved" chip? A playlist with no save toggle of its own in the hero (the owner's list: W-Playlist's "[✓ Saved]"); a followed
    /// list says it with its Saved toggle instead (W-PlaylistFollowed), so the chip never repeats it.</summary>
    static bool ChipRow(Detail.Identity id, Detail.Config cfg) => id.Kind == DetailKind.Playlist && cfg.Heart == HeartMode.None && id.Subject.Provider == EntityProvider.Spotify;

    /// <summary>A reserved line: a FIXED box of <paramref name="height"/> whether or not its words have landed; the words (keyed) fade up inside it when they arrive,
    /// and a later change of them (a fact appended) fills in place.</summary>
    static BoxEl Reserved(Element? words, float height, bool center) => new()
    {
        Direction = 1, Height = height, Shrink = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
        AlignItems = center ? FlexAlign.Center : FlexAlign.Stretch,
        Children = words is null ? [] : [new BoxEl { Key = "hero-line:words", Direction = 1, MinWidth = 0f, AlignItems = center ? FlexAlign.Center : FlexAlign.Stretch, Enter = WinUiDetailKit.FadeUp, Children = [words] }],
    };

    /// <summary>The faces the attribution line leads with: the billed artists, or a playlist's owner (W-Playlist: the owner's portrait and name, a link, before the
    /// facts).</summary>
    static IReadOnlyList<Controls.Face>? FacesOf(Detail.Identity id)
    {
        if (id.Artists is { Count: > 0 } artists) return artists;
        if (id.Kind == DetailKind.Playlist && id.OwnerName is { Length: > 0 } owner)
            return [new Controls.Face(owner, id.OwnerImageUrl, id.Owner.IsValid ? DetailVerbs.OpenOwner(id.Owner, owner) : null)];
        return null;
    }

    /// <summary>The title: Title Large (40/52; 28/36 stacked and short), semibold, wrapping to at most two lines.</summary>
    static Element TitleOf(VerticalSpec spec, WinUiHeroForm form, bool center)
        => spec.Slots.Title is { } edit
            ? edit(form.TitleSize, form.TitleLine)
            : R.Type.DetailHero(spec.Identity.Title) with
            {
                Size = form.TitleSize, LineHeight = form.TitleLine, Weight = 600, Color = Tok.TextPrimary,
                Wrap = TextWrap.WrapWholeWords, MaxLines = WinUiDetailRules.TitleMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                AlignSelf = center ? FlexAlign.Center : FlexAlign.Auto,
                Key = "hero-title:text",
            };

    /// <summary>Does the hero draw a daylist strip: a list with a daylist window and the page's pulse slot (the core's presence flag, <c>HeroFlags.Pulse</c>).</summary>
    static bool Pulses(VerticalSpec spec) => spec.Identity.DaylistExpiresAtMs > 0 && spec.Slots.Pulse is not null;

    /// <summary>The daylist strip's row: at least the flip cells' height whatever face it shows, so the loading band reserves it to the DIP.</summary>
    static BoxEl PulseRow(Element strip, bool center) => new()
    {
        Direction = 0, MinHeight = Controls.FlipCompactRowHeight, AlignItems = FlexAlign.Center, MinWidth = 0f,
        AlignSelf = center ? FlexAlign.Center : FlexAlign.Auto,
        Children = [strip],
    };

    /// <summary>The stacked hero's meta line: Caption in Text 3, centred.</summary>
    static Element MetaRun(string meta, WinUiHeroForm form) => new TextEl(meta)
    {
        Size = WinUiDetailRules.MetaSize, LineHeight = WinUiDetailRules.MetaLine, Color = Tok.TextTertiary, AlignSelf = FlexAlign.Center,
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>The cover: the page's own (a slot) or the artwork, r8 with the card shadow, a click where the page gives one.</summary>
    static Element Cover(Detail.Identity id, Detail.FrameSlots slots, Detail.FrameActions acts, WinUiHeroForm form)
    {
        float art = form.Cover;
        BoxEl cover = new()
        {
            Width = art, Height = art, Shrink = 0f,
            Corners = CornerRadius4.All(WinUiDetailRules.CoverRadius), Shadow = Elevation.Card, ClipToBounds = true,
            Animate = HeroGeometryMotion, TransformOriginX = 0f, TransformOriginY = 0f,
            Draggable = acts.CoverDrag is { } drag ? Drag.Source(drag) : null,
            Children = [slots.Cover?.Invoke(art) ?? Controls.Artwork(id.CoverUrl, art, art, WinUiDetailRules.CoverRadius, decodePx: WinUiDetailRules.CoverDecode(art))],
        };
        var mode = SurfaceRules.Ownership(inSlot: false, hasClick: acts.CoverClick is not null);
        return !mode.OwnsClick ? cover : cover with
        {
            OnClick = acts.CoverClick, Role = mode.Role, Focusable = mode.OwnsFocus,
            FocusVisualMargin = Design.FocusInsetBordered, Cursor = SurfaceRules.Cursor(in mode),
        };
    }

    /// <summary>The action row: the Play split, then Shuffle, Save, Share and "..." (the page's satellites replace the fixed group where it has them).</summary>
    static Element Actions(Detail.Identity id, Detail.Config cfg, Detail.FrameActions acts, Detail.FrameSlots slots, Action? playAll, WinUiDetailTier tier,
        WinUiHeroForm form, bool words, bool center)
    {
        float target = tier.Target;
        Element primary = slots.Primary is { } primaryOf
            ? primaryOf(() => Tok.AccentDefault)
            : WinUiDetailKit.PlaySplit(id.Subject, playAll ?? DetailVerbs.PlayContext(id.Subject), target, enabled: !id.Empty);
        IReadOnlyList<Element> fixedGroup = slots.Satellites is { } satellites
            ? satellites()
            : WinUiDetailKit.FixedActions("vhero-", id, cfg.Heart != HeartMode.None, acts, target, words,
                shuffleGlyph: ShuffleGlyph(tier, id.Kind), invite: acts.Invite, tune: TuneInHero(tier) ? acts.Tune : null);
        return WinUiDetailKit.ActionRow("vhero-actions", primary, fixedGroup, form.ActionTop, center: center);
    }

    /// <summary>Does the worded Shuffle wear its glyph? Not on a list's Standard and Small heroes (W-Playlist, W-PlaylistFollowed, W-LikedSongs); the Huge boards,
    /// the albums and the glyph-only Compact keep it.</summary>
    static bool ShuffleGlyph(WinUiDetailTier tier, DetailKind kind)
        => !(kind is DetailKind.Playlist or DetailKind.Liked) || tier.Tier is not (WidthTier.Small or WidthTier.Standard);

    /// <summary>Is a playlist's Tune the hero's action at this tier (<see cref="DetailTableTier.TuneInHero"/>; the list bar leaves it out then)?</summary>
    static bool TuneInHero(WinUiDetailTier tier) => tier.Core.Table.TuneInHero;

    /// <summary>The table's list bar under the hero (Find, the order, Filter, Select), on the page inset; null reserves its row.</summary>
    static Element ToolbarOf(HeroParts parts, WinUiDetailTier tier, WinUiHeroForm form)
        => new BoxEl
        {
            // It FLIPs with the cover and the words (a form change moves it: the short form's smaller cover and title): laid out at its new place at
            // once, it sat under the still-shrinking cover on the 720 crossing.
            Key = "hero-toolbar", Direction = 1, Margin = new Edges4(0f, form.ListGap, 0f, 0f), Animate = HeroReflowMotion,
            Padding = new Edges4(MathF.Max(0f, tier.InsetX - WinUiDetailRules.BarInset), 0f, MathF.Max(0f, tier.InsetX - WinUiDetailRules.BarInset), 0f),
            Children = [parts.Toolbar ?? new BoxEl { Height = VerticalLayout.ToolbarRowHeight }],
        };

    /// <summary>The hero's arrangement from its parts: the cover beside the words (bottom-aligned), or over them, both centred (stacked); then the toolbar.</summary>
    static Element Compose(WinUiHeroForm form, float inset, Element cover, Element[] words, Element toolbar, bool center)
    {
        // The words FLIP with the cover's resize (the same 280 ms): laid out at the new cover's edge at once, they sat on the still-shrinking cover
        // (W1920 widening and narrowing) until it caught up.
        Element info = new BoxEl
        {
            Direction = 1, Gap = form.InfoGap, AlignItems = center ? FlexAlign.Center : FlexAlign.Stretch, Animate = HeroReflowMotion,
            Justify = form.Stacked ? FlexJustify.Start : FlexJustify.End,
            Grow = form.Stacked ? 0f : 1f, Basis = form.Stacked ? float.NaN : 0f, MinWidth = 0f,
            // A stacked list hero's words span the column from the gutter (the cover alone is centred).
            AlignSelf = form.Stacked && !center ? FlexAlign.Stretch : FlexAlign.Auto,
            Children = words,
        };
        Element row = new BoxEl
        {
            Direction = form.Stacked ? (byte)1 : (byte)0, Gap = form.Gap, Animate = HeroReflowMotion,
            AlignItems = form.Stacked ? FlexAlign.Center : FlexAlign.End, MinWidth = 0f,
            Children = [cover, info],
        };
        return new BoxEl
        {
            Direction = 1, Animate = HeroReflowMotion,
            Children =
            [
                new BoxEl { Direction = 1, Padding = new Edges4(inset, form.TopInset, inset, 0f), MinWidth = 0f, Children = [row] },
                toolbar,
            ],
        };
    }

    // ══ 3. THE STICKY BAR ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The 48 bar: a 32 cover, the title (Body Strong 14/20) over the byline (Caption 12/16), the insights entry where the page has facts and Play as an
    /// accent button, on the tier's inset, over a layer with a hairline at its foot (W-Shell-Short).</summary>
    static Element Bar(VerticalSpec spec, HeroParts parts, float width, float inset)
    {
        var id = spec.Identity;
        float h = WinUiDetailRules.BandHeight;
        string? byline = Detail.Text.Byline(id.OwnerName, id.Meta, id.Eyebrow) is { Length: > 0 } text ? ArtistsByline(id, text) : null;
        var identity = new List<Element>(2) { BodyStrong(id.Title) with { Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis } };
        if (byline is { Length: > 0 })
            identity.Add(Caption(byline) with { Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis });
        Action play = spec.PlayAll ?? DetailVerbs.PlayContext(id.Subject);
        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                Width = WinUiDetailRules.BandCover, Height = WinUiDetailRules.BandCover, Shrink = 0f,
                Corners = CornerRadius4.All(WinUiDetailRules.BandCoverRadius), ClipToBounds = true,
                Children = [Controls.Artwork(id.CoverUrl, WinUiDetailRules.BandCover, WinUiDetailRules.BandCover, WinUiDetailRules.BandCoverRadius, decodePx: WinUiDetailRules.BandCoverDecode)],
            },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Children = identity.ToArray() },
        };
        if (spec.Insights is { } insights) kids.Add(Detail.InsightsBandAction(insights));
        kids.Add(Button.Create(Loc.Get(Strings.Detail.Play), play, ButtonAppearance.Accent, isEnabled: !id.Empty) with
        {
            Key = "bar:play", Shrink = 0f, Padding = new Edges4(WinUiDetailRules.BandPlayPad, 0f, WinUiDetailRules.BandPlayPad, 0f),
        });
        return new BoxEl
        {
            Width = width, Height = h, ZStack = true, HitTestVisible = true,
            Fill = Prop.Of(static () => Tok.FillCardDefault),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Width = width, Height = h, Padding = new Edges4(inset, 0f, inset, 0f), Gap = WinUiDetailRules.BandGap,
                    AlignItems = FlexAlign.Center, Children = kids.ToArray(),
                },
                new BoxEl
                {
                    Width = width, Height = h, Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
                    Children = [new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) }],
                },
            ],
        };
    }

    /// <summary>The bar's byline: the billed artists and the meta (W-Shell-Short: "Edward Maya, Vika Jigulina · 15 songs · 57 min").</summary>
    static string ArtistsByline(Detail.Identity id, string fallback)
    {
        if (id.Artists is not { Count: > 0 } artists) return fallback;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < artists.Count; i++) sb.Append(i > 0 ? ", " : "").Append(artists[i].Name);
        if (id.Meta is { Length: > 0 } meta) sb.Append(WinUiDetailRules.MetaLead).Append(meta);
        return sb.ToString();
    }

    // ══ 4. THE LOADING BAND ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The vertical arm's reserved LOADING band: the same parts at the same sizes as the loaded hero, unfilled boxes the deriver paints as bones; a usable
    /// preview cover is painted for real.</summary>
    internal static Element HeroSkeleton(VerticalSpec spec, float columnWidth, float compactLeft, TierFacts tiers, DetailForm pageForm = DetailForm.Hero,
        DetailColumnsProps? columns = null)
    {
        var tier = WinUiDetailTiers.Of(in tiers);
        var form = WinUiDetailTiers.HeroOf(in tiers);
        var id = spec.Identity;
        float inset = tier.InsetX;
        bool center = Centered(form, id.Kind);
        float avatar = form.AvatarFor(id.Kind);
        var rows = new List<Element>(5);
        // The eyebrow's bone stands in the row the loaded eyebrow holds (the chip's height where the list can carry one).
        if (form.Eyebrow)
            rows.Add(ChipRow(id, spec.Config)
                ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Height = form.ChipHeight, Children = [Bone(WinUiDetailRules.SkelEyebrowW, form.EyebrowLine, center)] }
                : Bone(WinUiDetailRules.SkelEyebrowW, form.EyebrowLine, center));
        rows.Add(Bone(WinUiDetailRules.SkelTitleW(form), form.TitleLine, center));
        rows.Add(new BoxEl
        {
            // The loaded hero's reserved attribution box, to the DIP (WinUiDetailRules.AttributionRow): the swap to the loaded hero moves nothing.
            Height = center ? float.NaN : WinUiDetailRules.AttributionRow(avatar),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiDetailRules.AvatarGap, AlignSelf = center ? FlexAlign.Center : FlexAlign.Start,
            Children =
            [
                .. avatar > 0f ? [new BoxEl { Width = avatar, Height = avatar, Shrink = 0f, Corners = CornerRadius4.All(avatar / 2f) }] : Array.Empty<Element>(),
                new BoxEl { Width = WinUiDetailRules.SkelNameW, Height = WinUiDetailRules.AttributionLine, Corners = CornerRadius4.All(WinUiDetailRules.BoneRadius) },
            ],
        });
        // The stacked (centred) hero lays the meta as its OWN reserved line under the names (`hero-meta`, every kind has one): the band reserves it too, or
        // the actions rode 40 down when the page landed (audit B7: WinUI album at 400).
        if (center) rows.Add(new BoxEl
        {
            Height = WinUiDetailRules.MetaLine, Direction = 0, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Center,
            Children = [Bone(WinUiDetailRules.SkelNameW, WinUiDetailRules.MetaLine, center)],
        });
        if (Pulses(spec)) rows.Add(PulseRow(Bone(WinUiDetailRules.SkelNameW, WinUiDetailRules.AttributionLine, center), center));
        rows.Add(SkeletonActions(spec, tier, form, center));
        Element cover = new BoxEl
        {
            Width = form.Cover, Height = form.Cover, Shrink = 0f, Corners = CornerRadius4.All(WinUiDetailRules.CoverRadius), ClipToBounds = true,
            Children = id.CoverUrl is { Length: > 0 } url ? [Controls.Artwork(url, form.Cover, form.Cover, WinUiDetailRules.CoverRadius, decodePx: WinUiDetailRules.CoverDecode(form.Cover))] : [],
        };
        Element toolbar = new BoxEl
        {
            Key = "hero-toolbar", Direction = 1, Margin = new Edges4(0f, form.ListGap, 0f, 0f),
            Padding = new Edges4(inset, 0f, inset, 0f), Height = VerticalLayout.ToolbarRowHeight,
            Children = [SkeletonToolbar(columnWidth - 2f * inset)],
        };
        return new BoxEl { Key = "detail-skeleton:hero", Direction = 1, Children = [Compose(form, inset, cover, rows.ToArray(), toolbar, center)] };
    }

    static BoxEl Bone(float width, float height, bool center) => new()
    {
        Width = width, Height = height, Corners = CornerRadius4.All(WinUiDetailRules.BoneRadius), AlignSelf = center ? FlexAlign.Center : FlexAlign.Start,
    };

    /// <summary>The action row while loading: the split's box, then exactly the buttons the loaded row will lay out.</summary>
    static Element SkeletonActions(VerticalSpec spec, WinUiDetailTier tier, WinUiHeroForm form, bool center)
    {
        float h = tier.Target;
        var kids = new List<Element>(5)
        {
            new BoxEl { Width = WinUiDetailRules.SkelPlayW, Height = h, Shrink = 0f, Corners = Radii.ControlAll },
        };
        int count = WinUiDetailKit.FixedActionCount(spec.Identity, spec.Config.Heart != HeartMode.None, spec.Actions,
            invite: spec.Actions.Invite is not null, tune: spec.Actions.Tune is not null && TuneInHero(tier));
        for (int i = 0; i < count; i++)
            kids.Add(new BoxEl { Width = i < 2 && !form.Stacked ? WinUiDetailRules.SkelShuffleW : h, Height = h, Shrink = 0f, Corners = Radii.ControlAll });
        return WinUiDetailKit.ActionRow("vhero-skel-actions", kids[0], kids.GetRange(1, kids.Count - 1), form.ActionTop, center: center);
    }

    /// <summary>The list bar's reserved row while loading: Find on the left, the order, Filter and Select on the right.</summary>
    static Element SkeletonToolbar(float rowWidth) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiDetailRules.ActionGap, Grow = 1f, MinWidth = 0f,
        Children =
        [
            new BoxEl { Width = WinUiDetailRules.SkelToolbarPlate, Height = WinUiDetailRules.Target, Corners = Radii.ControlAll },
            new BoxEl { Grow = 1f, Height = 1f },
            new BoxEl { Width = WinUiDetailRules.SkelToolbarPlate, Height = WinUiDetailRules.Target, Corners = Radii.ControlAll },
            new BoxEl { Width = WinUiDetailRules.SkelToolbarPlate, Height = WinUiDetailRules.Target, Corners = Radii.ControlAll },
        ],
    };
}
