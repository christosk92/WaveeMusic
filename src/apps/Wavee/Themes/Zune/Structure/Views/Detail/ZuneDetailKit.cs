// ── Themes/Zune/Structure/Views/Detail/ZuneDetailKit.cs — the pieces Zune's detail views share (themes plan 6.3; P5 Z6a1) ──────────────────
//
// Z-Album's grammar, shared by the identity column, the vertical hero, the show header and the album's own views:
//
//   · the RING: a 36 circle (40 at Compact and 4K), a filled accent disc for play and a 2-dip outline for the rest, its glyph centred, its
//     lowercase word beside it where the tier writes one (play, saved). The SAVE ring is its own component: it reads the saved set and wears
//     the accent ring and the filled heart while saved ("saved"), the outline and the empty heart otherwise ("save");
//   · the PANORAMA: the page's title in the panorama voice at the tier's step, wrapped over up to two lines with the band hugging it;
//   · the HEADING (26/32 light, lowercased by the heading case role) and the LINK word (13, secondary, primary on hover);
//   · the keyed ROWS of a column (a position-only FLIP when a row above arrives, a fade-up for a row that arrives late) and the skeleton BARS.
//
// A name (a title, an artist) is never re-cased: the case roles apply to the words the theme writes. Numbers are `Rules\ZuneDetailRules.cs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Roles;
using Z = Wavee.Themes.Zune.ZuneDetailRules;

namespace Wavee.Themes.Zune;

/// <summary>The shared pieces of Zune's detail views.</summary>
internal static class ZuneDetailKit
{
    // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A late row's entrance: it fades up when it arrives after the frame.</summary>
    public static readonly EnterExit FadeUp = new(Opacity: 0f, Active: true);

    /// <summary>The position-only FLIP a keyed row takes when a row above it arrives or leaves.</summary>
    public static readonly LayoutTransition Shove = new(TransitionChannels.Position, TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    /// <summary>A row at the END of a column whose own height grows when a fact lands late (the release panel's released date, its "other
    /// versions"): it FLIPs when pushed and eases its height open (a reflow clipped from the bottom, anchored at its top), so the rows under it
    /// glide instead of snapping and nothing above it moves.</summary>
    public static readonly LayoutTransition GrowShove = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>A keyed row present from the first frame: it only ever gets pushed.</summary>
    public static BoxEl Row(string key, Element child) => new() { Key = key, Direction = 1, MinWidth = 0f, Layout = Shove, Children = [child] };

    /// <summary>A keyed row that can arrive late: fades up on insert, FLIPs on later shoves.</summary>
    public static BoxEl LateRow(string key, Element child) => new()
    {
        Key = key, Direction = 1, MinWidth = 0f, Layout = Shove, Enter = FadeUp, Children = [child],
    };

    /// <summary>A skeleton bar (the deriver paints it).</summary>
    public static BoxEl Bar(float width, float height) => new()
    {
        Width = width, Height = height, Shrink = 0f, Corners = CornerRadius4.All(Z.SkelRadius),
    };

    // ── words ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A section heading (26/32 light), lowercased by the heading case role (a word the theme writes).</summary>
    public static TextEl Heading(string text) => R.Type.Heading(text) with
    {
        Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>A heading that carries a NAME ("More by Daft Punk"): the heading voice as written, never re-cased.</summary>
    public static TextEl NamedHeading(string text) => Heading(text) with { Case = TextCase.AsWritten };

    /// <summary>A link word: the 13 voice in the secondary ink, the primary on hover, a hand and a hyperlink role. <paramref name="minHeight"/> is its hit
    /// height (a heading row's 40: the Compact minimum), the word centred in it.</summary>
    public static Element Link(string key, string text, Action go, float minHeight = 0f) => new BoxEl
    {
        Key = key, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, MinHeight = minHeight,
        Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = go,
        Children =
        [
            R.Type.DenseMeta(text) with
            {
                Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    /// <summary>A heading row: the heading, a spring, then the row's link word.</summary>
    public static Element HeadingRow(string key, TextEl heading, Element? link, float height) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Height = height, Shrink = 0f, MinWidth = 0f, Gap = Z.SectionGap,
        Children = link is null ? [heading] : [heading, new BoxEl { Grow = 1f, MinWidth = 0f }, link],
    };

    // ── the page column ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The system page column (<see cref="ZuneFrameRules.PageColumn"/>: the gutter each side, capped at 2400 plus the gutters) centred in a
    /// row and grown to its cap, so the page x equals the band's (audit A1). The row is the centring host: inside it the column stretches to the row's
    /// height (a list fills it) instead of hugging its content, which a centred child of a column would do.</summary>
    public static BoxEl CentredPage(BoxEl page, float gutter) => new()
    {
        Direction = 0, MinWidth = 0f, Justify = FlexJustify.Center,
        Children = [ZuneFrameRules.PageColumn(page, gutter) with { AlignSelf = FlexAlign.Stretch, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f }],
    };

    // ── the panorama ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's title in the panorama voice (the record's step, for every kind): WRAPPED by whole words over up to
    /// <see cref="ZuneDetailLayout.PanoramaLines"/> lines and hugged by its band, so a long name keeps its size (an ellipsis only after the last line). A
    /// folded band (a short window) hides the title on one line. A title is a name: never re-cased.</summary>
    public static TextEl Panorama(string title, ZuneDetailLayout lay)
    {
        var text = lay.PanoramaLarge ? R.Type.Panorama(title) : R.Type.PanoramaCompact(title);
        if (lay.PanoramaSize > 0f) text = text with { Size = lay.PanoramaSize, LineHeight = lay.PanoramaLine };
        // The boards set the title on an EXACT line (120/130, 150/160, 64/72): the line advances by the line height, not the font's taller natural box.
        text = text with { LineStacking = LineStacking.BlockLineHeight };
        if (lay.PanoramaWraps)
            return text with
            {
                Color = Tok.TextPrimary, MaxLines = lay.PanoramaLines, Wrap = TextWrap.WrapWholeWords,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            };
        return text with
        {
            Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };
    }

    /// <summary>The panorama's band: its reserved height (eased: a short window folds it), the title nudged onto the gutter's optical edge. Clipped, so
    /// a folded band hides the title. <paramref name="gutter"/> is the inset the band adds itself (0 inside the page column, which pads the gutter: the
    /// nudge then becomes a negative margin).
    ///
    /// THE TITLE. The title keeps its tier's step and WRAPS by whole words over <see cref="ZuneDetailLayout.PanoramaLines"/> lines; the band HUGS it (its
    /// reserved height a floor, eased on the size channel), so a long name pushes the body down instead of shrinking or clipping. An editable playlist
    /// hands its inline editor as the <c>Title</c> slot (Z6b1 must-fix: the owner renames the playlist in its panorama, as under Classic); the editor takes
    /// the same step and the band hugs it likewise (the edit arm's field and words push the body down). A folded band (a short window) draws the plain
    /// title, hidden: the identity column leads with the editor there.</summary>
    public static BoxEl PanoramaBand(Detail.Identity id, Detail.FrameSlots slots, ZuneDetailLayout lay, float gutter)
    {
        float lead = gutter + lay.PanoramaNudgeX;
        bool editor = slots.Title is not null && lay.PanoramaH > 0f;
        // The editor and a wrapping title stand INSIDE the page and let the band hug them.
        bool inside = editor || lay.PanoramaWraps;
        Element title;
        if (slots.Title is { } edit && editor)
        {
            var voice = Panorama("", lay);
            title = edit(voice.Size, voice.LineHeight);
        }
        else
            title = Panorama(id.Title, lay);
        return new BoxEl
        {
            Key = Z.PanoramaKey, Direction = 0, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
            Height = inside ? float.NaN : lay.PanoramaH, MinHeight = inside ? lay.PanoramaH : 0f,
            Animate = Z.PanoramaFold, HitTestVisible = editor,
            // The editor and the title wrap inside the page (the editor's words and pencil must stay on screen).
            Padding = new Edges4(MathF.Max(0f, lead), 0f, gutter, 0f),
            Margin = new Edges4(MathF.Min(0f, lead), 0f, 0f, 0f),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Shrink = 1f, Grow = inside ? 1f : 0f,
                    Margin = new Edges4(0f, lay.PanoramaNudgeY, 0f, inside ? lay.PanoramaGap : 0f),
                    Animate = ZuneMotion.TitleScale, Opacity = Z.PanoramaOpacity,
                    Children = [title],
                },
            ],
        };
    }

    // ── the rings ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ring's disc: the filled accent (<paramref name="accent"/> non-null: the play disc) or the outline in the primary ink.</summary>
    public static BoxEl Disc(string glyph, float size, float glyphSize, Func<ColorF>? accent) => new()
    {
        Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size / 2f),
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = accent is { } a ? Prop.Of(a) : (Prop<ColorF>)ColorF.Transparent,
        BorderWidth = accent is null ? Z.RingStroke : 0f, BorderColor = Tok.TextPrimary,
        HoverFill = accent is null ? R.Layer.ChromeHover : ColorF.Transparent, PressedFill = accent is null ? R.Layer.ChromePressed : ColorF.Transparent,
        Children =
        [
            new TextEl(glyph)
            {
                Size = glyphSize, FontFamily = Tok.Definition.Fonts.Icon,
                Color = accent is { } fg ? Prop.Of(() => ColorContrast.PickContrast(fg())) : (Prop<ColorF>)Tok.TextPrimary,
            },
        ],
    };

    /// <summary>A ring's word (the label voice, lowercased as a command).</summary>
    static TextEl Word(string text, ZuneDetailLayout lay) => R.Type.DenseTitle(text) with
    {
        Size = lay.LabelSize, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
    };

    /// <summary>A ring action: the disc and, where the tier writes one, its word. A glyph-only ring is named for its tooltip and assistive tech.</summary>
    public static Element Ring(string id, string glyph, string label, bool word, Func<ColorF>? accent, Action? onClick, ZuneDetailLayout lay)
    {
        var kids = new List<Element>(2) { Disc(glyph, lay.RingSize, lay.RingGlyph, accent) };
        if (word) kids.Add(Word(label, lay));
        BoxEl box = new()
        {
            Key = Z.RingPrefix + id, Direction = 0, AlignItems = FlexAlign.Center, Gap = lay.RingLabelGap, Shrink = 0f, MinHeight = lay.RingSize,
            Cursor = onClick is null ? (CursorId?)null : CursorId.Hand, Role = AutomationRole.Button, Focusable = onClick is not null,
            AllowFocusOnInteraction = false, BlocksDragArm = true, IsEnabled = onClick is not null,
            Opacity = onClick is null ? ZuneBandRules.DisabledOpacity : 1f,
            OnClick = onClick, Children = kids.ToArray(),
        };
        return word ? box : Controls.Named(box, label);
    }

    /// <summary>The "..." ring: the outline around the page's lazily built menu (the frame's More factory), opened at the ring.</summary>
    public static Element MoreRing(Func<ContextMenuModel?> menu, ZuneDetailLayout lay)
        => Embed.Comp(new MoreProps(lay.RingSize, lay.RingGlyph, menu), static () => new MoreHost()) with { Key = Z.RingPrefix + "more" };

    sealed record MoreProps(float Size, float Glyph, Func<ContextMenuModel?> Menu)
    {
        public bool Equals(MoreProps? o) => o is not null && Size == o.Size && Glyph == o.Glyph;
        public override int GetHashCode() => HashCode.Combine(Size, Glyph);
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

    /// <summary>The "..." ring's host: one menu host, the newest factory read at open.</summary>
    sealed class MoreHost : Component, IPropsHost
    {
        MoreProps? _latest;
        readonly Signal<MoreProps?> _props = new(null);
        readonly Func<ContextMenuModel?> _menu;

        public MoreHost() => _menu = () => _latest?.Menu();

        public void ApplyProps(object props)
        {
            _latest = (MoreProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            var overlay = UseContext(Overlay.Service);
            BoxEl ring = Disc(Icons.More, p.Size, p.Glyph, null) with
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, AllowFocusOnInteraction = false, BlocksDragArm = true,
                ClickRequestsContext = true,
            };
            if (!Controls.IsNullOverlay(overlay)) ring = ContextSheets.Attach(ring, overlay, _menu, s_moreMenu);
            return Controls.Named(ring, Loc.Get(Strings.Common.More));
        }
    }

    /// <summary>The save ring (the heart), keyed on the uri it saves (props freeze at mount).</summary>
    public static Element SaveRing(string uri, string name, bool word, Func<ColorF> accent, ZuneDetailLayout lay)
        => Embed.Comp(new SaveProps(uri, name, word, accent, lay), static () => new SaveHost()) with { Key = Z.RingPrefix + "save:" + uri };

    sealed record SaveProps(string Uri, string Name, bool Word, Func<ColorF> Accent, ZuneDetailLayout Layout)
    {
        public bool Equals(SaveProps? o) => o is not null && Uri == o.Uri && Name == o.Name && Word == o.Word && ReferenceEquals(Layout, o.Layout);
        public override int GetHashCode() => HashCode.Combine(Uri, Name, Word);
    }

    /// <summary>The save ring's host: reads the saved set (a change re-skins this ring alone), the accent ring and the filled heart while saved.
    /// Nothing is drawn where no library seam is connected (the affordance is gated on the capability, as the shared heart's).</summary>
    sealed class SaveHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<SaveProps>();
            var lib = Controls.Library;
            if (lib is null) return new BoxEl();
            bool saved = lib.IsSaved(p.Uri);
            var lay = p.Layout;
            var accent = p.Accent;
            var disc = Disc(saved ? Icons.HeartFill : Icons.Heart, lay.RingSize, lay.RingGlyph, null);
            if (saved)
                disc = disc with
                {
                    BorderColor = Prop.Of(accent),
                    Children = [new TextEl(Icons.HeartFill) { Size = lay.RingGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Prop.Of(accent) }],
                };
            string label = Loc.Get(saved ? Strings.Menu.Saved : Strings.Menu.Save);
            var kids = new List<Element>(2) { disc };
            if (p.Word) kids.Add(Word(label, lay));
            string uri = p.Uri, name = p.Name;
            BoxEl box = new()
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = lay.RingLabelGap, Shrink = 0f, MinHeight = lay.RingSize,
                Cursor = CursorId.Hand, Role = AutomationRole.ToggleButton, Focusable = true, AllowFocusOnInteraction = false, BlocksDragArm = true,
                OnClick = () => lib.ToggleSaved(uri, name), Children = kids.ToArray(),
            };
            return p.Word ? box : Controls.Named(box, label);
        }
    }

    /// <summary>The ring row: play (its word where the tier writes it), the save ring, shuffle (4K), share and "..." in the tier's order, wrapping
    /// one by one (a localisation longer than the board's words never overflows the column). <paramref name="spring"/> pushes share and "..." to
    /// the row's end (the vertical hero's row, Z-Album-Compact).</summary>
    public static Element Rings(string key, Detail.Identity id, Detail.Config cfg, Detail.FrameActions acts, Detail.FrameSlots slots,
        Func<ColorF> accent, Action play, ZuneDetailLayout lay, bool spring)
    {
        // One home per verb (audit A4): from Small up the band's row 2 carries play, shuffle and saved, so the record leaves them out here. A page's
        // own primary (a show's or an episode's) is not a band verb and stays.
        var kids = new List<Element>(6);
        if (slots.Primary is { } primary) kids.Add(primary(accent));
        else if (lay.PrimaryInHero) kids.Add(Ring("play", Icons.Play, Loc.Get(Strings.Detail.Play), lay.PlayWord, accent, play, lay));
        if (slots.Satellites is { } satellites)
            kids.AddRange(satellites());
        else
        {
            if (lay.PrimaryInHero && cfg.Heart != HeartMode.None)
            {
                var target = id.SaveTarget.IsValid ? id.SaveTarget : id.Subject;
                kids.Add(SaveRing(target.Text, id.Title, lay.SaveWord, accent, lay));
            }
            if (lay.PrimaryInHero && lay.ShuffleRing && acts.Shuffle is { } shuffle)
                kids.Add(Ring("shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), false, null, shuffle, lay));
            if (spring) kids.Add(new BoxEl { Key = "rings:spring", Grow = 1f, MinWidth = 0f });
            if (lay.ShareRing && ShareActionFor(id) is { } share)
                kids.Add(Ring("share", Icons.Share, Loc.Get(Strings.Menu.Share), false, null, share, lay));
            if (lay.MoreRing && acts.More is { } more) kids.Add(MoreRing(more, lay));
        }
        return new BoxEl
        {
            Key = key, Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = lay.ActionsGap, MinWidth = 0f,
            Layout = Shove, Children = kids.ToArray(),
        };
    }

    /// <summary>How many rings <see cref="Rings"/> lays out for this page (the skeleton reserves exactly these discs).</summary>
    public static int RingCount(Detail.Identity id, Detail.Config cfg, Detail.FrameActions acts, Detail.FrameSlots slots, ZuneDetailLayout lay)
    {
        int n = slots.Primary is not null || lay.PrimaryInHero ? 1 : 0;
        if (slots.Satellites is { } satellites) return n + satellites().Length;
        if (lay.PrimaryInHero && cfg.Heart != HeartMode.None) n++;
        if (lay.PrimaryInHero && lay.ShuffleRing && acts.Shuffle is not null) n++;
        if (lay.ShareRing && id.ShareUrl is { Length: > 0 }) n++;
        if (lay.MoreRing && acts.More is not null) n++;
        return n;
    }

    /// <summary>The ring row's loading twin: the discs, each at the ring's edge.</summary>
    public static Element RingsSkeleton(string key, int count, ZuneDetailLayout lay)
    {
        var kids = new Element[Math.Max(1, count)];
        for (int i = 0; i < kids.Length; i++)
            kids[i] = new BoxEl { Width = lay.RingSize, Height = lay.RingSize, Shrink = 0f, Corners = CornerRadius4.All(lay.RingSize / 2f) };
        return new BoxEl { Key = key, Direction = 0, Wrap = true, Gap = lay.ActionsGap, MinWidth = 0f, Children = kids };
    }

    /// <summary>Copy the page's web link with the confirmation toast, where a clipboard seam exists.</summary>
    public static Action? ShareActionFor(Detail.Identity id) => ShareLinkAction(id.ShareUrl);

    /// <summary>The same copy-the-link verb for a bare web link (a concert's).</summary>
    public static Action? ShareLinkAction(string? shareUrl)
    {
        if (shareUrl is not { Length: > 0 } url) return null;
        return () =>
        {
            if (Actions.Services.Clipboard is not { } copy) return;
            copy(url);
            _ = Notify.Say(Loc.Get(Strings.Menu.LinkCopied), InfoBarSeverity.Success);
        };
    }

    // ── identity text ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The meta line (Z-Album: "album · may 17, 2013 · 13 songs · 74 min"): the eyebrow (the kind and the year) and the page's meta in one
    /// run, the label case lowering the words. Null while neither is known.</summary>
    public static string? MetaText(Detail.Identity id)
    {
        bool eyebrow = id.Eyebrow.Length > 0;
        bool meta = !id.MetaLoading && id.Meta is { Length: > 0 };
        if (eyebrow && meta) return id.Eyebrow + " · " + id.Meta;
        if (meta) return id.Meta;
        return eyebrow && !id.MetaLoading ? id.Eyebrow : null;
    }

    /// <summary>A line of data under a name (13/18, secondary, one line): a card's subtitle, a video's meta. Data, never re-cased.</summary>
    public static TextEl Sub(string text) => R.Type.TrackMeta(text) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>The meta line at the record's size, one line (or <paramref name="lines"/>), or its loading bar while the count is not known. A meta that
    /// may wrap breaks only between its " · " runs (<see cref="ZuneDetailRules.Glued"/>), so no run leaves an orphan word ("12" / "min") on the next line.</summary>
    public static Element Meta(Detail.Identity id, float width, ZuneDetailLayout lay, int lines = 1)
    {
        // The row's height is RESERVED for every line the meta may take (loading bar, one-line meta and wrapped meta alike): when "· 41 min"
        // lands the line wraps inside its own slot and nothing under it (the rings, released, label, copyright, other versions) moves.
        float reserve = lay.MetaLine * Math.Max(1, lines);
        if (MetaText(id) is { } meta)
        {
            string text = lines > 1 ? Z.Glued(meta) : meta;
            return new BoxEl
            {
                Width = width, Height = reserve, Direction = 1, Shrink = 0f, MinWidth = 0f,
                Children =
                [
                    R.Type.DenseMeta(text) with
                    {
                        Size = lay.MetaSize, LineHeight = lay.MetaLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label),
                        Width = width, MinWidth = 0f, MaxLines = lines, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };
        }
        return new BoxEl
        {
            Width = width, Height = reserve, Direction = 1, Shrink = 0f, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Height = lay.MetaLine, Direction = 1, Justify = FlexJustify.Center, MinWidth = 0f,
                    Children =
                    [
                        new SkelRegionEl(Pending: s_true, Failed: s_false, Content: s_metaShimmer, ShimmerSource: null, OnFailed: null,
                            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
                    ],
                },
            ],
        };
    }

    static readonly Func<Element> s_metaShimmer = static () => R.Type.DenseMeta(Z.MetaShimmerText) with { MaxLines = 1 };

    /// <summary>The default artist line where a page declares no attribution slot: the billed names as one link run in the record's artist voice.</summary>
    public static Element Artists(Detail.Identity id, float width, ZuneDetailLayout lay, float hit = 0f)
    {
        var artists = id.Artists;
        if (artists is not { Count: > 0 })
            return id.OwnerName is { Length: > 0 } owner ? ArtistText(owner, width, lay) : new BoxEl();
        var names = string.Join(", ", artists.Select(static a => a.Name));
        var go = artists[0].OnClick;
        Element text = ArtistText(names, width, lay);
        return go is null ? text : new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, MaxWidth = width, MinHeight = hit, Role = AutomationRole.Hyperlink, Focusable = true,
            Cursor = CursorId.Hand, OnClick = go, Children = [text],
        };
    }

    /// <summary>A name in the artist voice (18/24, 20/28 at 4K): primary, the accent on hover, one line.</summary>
    public static TextEl ArtistText(string name, float width, ZuneDetailLayout lay) => R.Type.TrackTitle(name) with
    {
        Size = lay.ArtistSize, LineHeight = lay.ArtistLine, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
        MaxWidth = width, MinWidth = 0f, Shrink = 1f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The cover, the page's subject: square at the Cover shape, the cover's own click and drag, a 1-dip edge against the wash.</summary>
    public static BoxEl Cover(string key, Detail.Identity id, Detail.FrameActions acts, Detail.FrameSlots slots, float edge)
    {
        bool click = acts.CoverClick is not null;
        return new BoxEl
        {
            Key = key, Width = edge, Height = edge, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true,
            BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
            Draggable = acts.CoverDrag is { } drag ? Drag.Source(drag) : null,
            OnClick = acts.CoverClick, Role = click ? AutomationRole.Button : AutomationRole.None, Focusable = click,
            Cursor = click ? CursorId.Hand : (CursorId?)null,
            Children = [slots.Cover?.Invoke(edge)
                ?? Controls.Artwork(id.CoverUrl, edge, edge, R.Shape.For(ShapeRole.Cover), decodePx: Z.CoverDecodePx)],
        };
    }

    /// <summary>The cover's loading twin: the real preview where the url is known (exempt from the deriver), else the reserved square.</summary>
    public static Element CoverSkeleton(string? url, float edge)
    {
        if (url is { Length: > 0 })
        {
            Element preview = Controls.Artwork(url, edge, edge, R.Shape.For(ShapeRole.Cover), decodePx: Z.CoverDecodePx);
            return preview.Skel(preview);
        }
        return new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover) };
    }

    /// <summary>The description, clamped with the inline "more" (the whole text on hover while clamped).</summary>
    public static Element? Description(Detail.Identity id, Detail.FrameSlots slots, float width, Func<ColorF> accent)
    {
        if (id.EditableMetadata && slots.Description is { } edit) return edit(width);
        if (id.DescriptionHtml is not { Length: > 0 } html) return null;
        return Controls.ExpandableRichText(html, Z.DescriptionSize, Tok.TextSecondary, accent(), width, Z.DescriptionLines, id.Subject.Text,
            DetailVerbs.NavigateKey, fullTextTip: true);
    }

    static readonly Func<bool> s_true = static () => true;
    static readonly Func<bool> s_false = static () => false;

    /// <summary>Never pending (a region shown loaded at once).</summary>
    public static Func<bool> Never => s_false;
}
