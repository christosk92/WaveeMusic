// ── Themes/WinUi/Structure/Views/Podcast/WinUiPodcastAudiobook.cs — WinUI's audiobook page (podcast pages v2, concept page "Audiobook") ─────────────
//
// The concept's WinUI variant, "Chapters as: Line + contents":
//
//   WinUiPodcastKit.Page (the scroller over the cover-tone wash; the 48 bar over it once the hero has gone)
//   └─ column on the frame's gutter
//      ├─ THE HERO   the 200 / 152 / 104 book cover · "Audiobook · genres" + the "Included in Premium" tag · the title (Title Large; 28 Small, 20 Compact) ·
//      │             the subtitle on its own line (18/24 secondary) · "By … Narrated by …" · ★ rating (count) · length · N chapters · year;
//      │             top right: In Your Library / Download / Share / More; the up row: Start / Continue / Listen again (accent unless finished) + Sample ·
//      │             5 min (not started) + the label (Chapter N of M · name / the bar + "% · time left in the book" / Finished) + the speed and sleep timer.
//      ├─ BarSentinel
//      ├─ THE PREMIUM LINE   the headphones glyph · "Included in Premium · 12 h a month, renews 12 Dec" · Check hours left · Top up hours
//      │                     (or, for a book for sale: the price, the list price struck through, Buy audiobook)
//      ├─ THE COLUMNS  ≥ 1008: [the book line over the contents page] beside the 380 About card; narrower: one column, About first until started
//      └─ "More by <author> and similar books" (the kit's cards)
//
// Loading: one SkelRegionEl over the same column built from `AudiobookView.Placeholder` (flat fills at the loaded geometry).
// Numbers: `WinUiPodcastAudiobookRules` and the tier record `WinUiAudiobookLayout`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using AB = Wavee.Themes.WinUi.WinUiPodcastAudiobookRules;
using K = Wavee.Themes.WinUi.WinUiPodcastKitRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's audiobook page.</summary>
internal static class WinUiPodcastAudiobook
{
    public static Element Template(AudiobookPageProps p, in ViewContext ctx)
        => new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, Children = [Embed.Comp(p, static () => new PageHost())] };

    sealed class PageHost : Component
    {
        /// <summary>The page scroller's handle: the contents page windows its rows on it (only the rows near the viewport mount).</summary>
        readonly ScrollHandle _scroll = new();

        public override Element Render()
        {
            var p = UseProps<AudiobookPageProps>();
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = WinUiAudiobookLayout.Of(facts.Content);
            float inset = WinUiTiers.InsetOf(facts);
            var barOn = UseSignal(false);
            var v = p.View.Value;
            bool playing = p.Playing.Value;

            Element real = Column(v, p, lay, playing, barOn);
            var region = new SkelRegionEl(
                Pending: () => p.View.Value.Load == PodcastLoad.Pending, Failed: static () => false,
                Content: () => real, ShimmerSource: () => Column(AudiobookView.Placeholder, p, lay, false, null),
                OnFailed: null, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
            Element content = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(inset, 0f, inset, K.PageFoot), Children = [region],
            };
            Element bar = Embed.Comp(new BarProps(p, inset), static () => new BarHost());
            return WinUiPodcastKit.Page(p.RouteKey, v.Tone, barOn, Ctx.Provide(WinUiAudiobookParts.PageScroll, (ScrollHandle?)_scroll, content), bar, _scroll);
        }
    }

    // ══ THE COLUMN ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Column(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay, bool playing, Signal<bool>? barOn)
    {
        var kids = new List<Element>(6) { Hero(v, p, lay, playing) };
        if (barOn is not null) kids.Add(WinUiPodcastKit.BarSentinel(barOn));
        kids.Add(Premium(v, p));
        kids.Add(Columns(v, p, lay));
        if (v.Similar.Length > 0)
        {
            kids.Add(new BoxEl
            {
                Key = AB.MoreKey, Layout = AB.Block, Direction = 1, Gap = AB.SectionBottom, Margin = new Edges4(0f, AB.SectionTop, 0f, 0f), MinWidth = 0f,
                Children = [WinUiPodcastKit.SectionHeader(v.SimilarTitle), WinUiPodcastKit.MoreLikeThis(v.Similar, p.OpenUri, rows: lay.Stacked, p.SimilarMenu, p.PlaySimilar)],
            });
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = [.. kids] };
    }

    // ══ THE HERO ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Hero(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay, bool playing)
    {
        // The frame's lint guide reads the page's leading edge from the page-title key: the book's leading edge is its cover (the title sits beside
        // it, as on every detail page), so the cover carries the key.
        Element cover = new BoxEl
        {
            Key = WinUiFrameLayout.PageTitleKey, Width = lay.Cover, Height = lay.Cover, Shrink = 0f, AlignSelf = FlexAlign.Start, Corners = CornerRadius4.All(AB.CoverRadius), ClipToBounds = true,
            Children = [Controls.Artwork(v.ArtUrl, lay.Cover, lay.Cover, AB.CoverRadius, decodePx: AB.CoverDecode)],
        };
        Element eyebrow = Eyebrow(v, p);
        BoxEl id = Identity(v, p, lay);
        Element cmds = Commands(v, p, lay);
        Element up = Up(v, p, lay, playing);
        BoxEl form;
        if (lay.Stacked)
        {
            form = new BoxEl
            {
                Key = AB.HeroKey + ":stack", Direction = 1, Gap = lay.RowGap, MinWidth = 0f, Padding = new Edges4(0f, lay.HeroTop, 0f, lay.HeroBottom),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = lay.ColGap, MinWidth = 0f,
                        Children = [cover, new BoxEl { Direction = 1, Gap = AB.IdGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [eyebrow, id] }],
                    },
                    up, cmds,
                ],
            };
        }
        else
        {
        var right = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = lay.Cover, Gap = lay.RowGap, Justify = FlexJustify.SpaceBetween,
            // The commands share the eyebrow's line only: the title and everything under it take the column's whole width (a title squeezed
            // into a narrow column beside the buttons, owner 2026-11-18).
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = AB.IdGap, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Gap = AB.HeroColGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                            Children = [new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [eyebrow] }, cmds],
                        },
                        id,
                    ],
                },
                up,
            ],
        };
        form = new BoxEl
        {
            Key = AB.HeroKey + ":row", Direction = 0, Gap = lay.ColGap, MinWidth = 0f, Padding = new Edges4(0f, lay.HeroTop, 0f, lay.HeroBottom),
            Children = [cover, right],
        };
        }
        // The two arrangements are different trees: keyed per form under a plain anchor (a slot's lone child ignores its key), they cross-fade on a
        // declared swap family while the anchor eases its height.
        return new BoxEl
        {
            Key = AB.HeroKey, Direction = 1, MinWidth = 0f, Layout = AB.Block,
            Children = [form with { Enter = AB.HeroSwap, Exit = AB.HeroSwap, Transition = AB.HeroSwapMotion }],
        };
    }

    static BoxEl Identity(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        var kids = new List<Element>(5);
        kids.Add(TitleLarge(v.Title) with
        {
            Size = lay.TitleSize, LineHeight = lay.TitleLine, Wrap = TextWrap.Wrap, MinWidth = 0f,
        });
        if (v.Subtitle.Length > 0)
            kids.Add(new TextEl(v.Subtitle) { Size = lay.SubtitleSize, LineHeight = lay.SubtitleLine, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f });
        if (v.Authors.Length + v.Narrators.Length > 0) kids.Add(WinUiAudiobookParts.Byline(v, p.OpenUri));
        kids.Add(Meta(v));
        return new BoxEl { Direction = 1, Gap = AB.IdGap, MinWidth = 0f, Children = [.. kids] };
    }

    /// <summary>"Audiobook · Nonfiction · Business &amp; Careers [Included in Premium]" on ONE line: the kind word opens the Browse category of
    /// audiobooks (a house HyperlinkButton: rest, hover, pressed, focus); the genres name no page Wavee can open (AUDIOBOOK_GENRE ids are not Browse
    /// pages) and run after it as one caption that ends in an ellipsis where the line is short (the facts list them all); the tag stays whole at the end.
    /// A wrapped eyebrow put a separator at a line's edge and the tag on a line of its own (owner 2026-11-18).</summary>
    static Element Eyebrow(AudiobookView v, AudiobookPageProps p)
    {
        var kids = new List<Element>(3);
        kids.Add(v.KindRoute is { Length: > 0 } route
            ? HyperlinkButton.Create(v.Kind, () => p.OpenRoute(route), size: ControlSize.Small) with { Key = AB.KindKey, Shrink = 0f, Padding = default }
            : Caption(v.Kind) with { Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, Shrink = 0f });
        if (v.Genres.Length > 0)
            kids.Add(Caption(Sep + string.Join(" " + Sep, v.Genres)) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            });
        if (v.Included && v.IncludedTag.Length > 0)
            kids.Add(new BoxEl
            {
                Height = AB.TagHeight, Padding = new Edges4(AB.TagPadX, 0f, AB.TagPadX, 0f), Corners = CornerRadius4.All(AB.TagRadius), Direction = 0,
                AlignItems = FlexAlign.Center, BorderWidth = 1f, BorderColor = Tok.AccentTextPrimary, Shrink = 0f,
                Children = [new TextEl(v.IncludedTag) { Size = AB.TagSize, LineHeight = AB.TagLine, Weight = AB.Strong, Color = Tok.AccentTextPrimary, Wrap = TextWrap.NoWrap }],
            });
        return new BoxEl { Direction = 0, Gap = AB.EyebrowGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [.. kids] };
    }

    /// <summary>The separator in front of a part (a middle dot and a space).</summary>
    const string Sep = "\u00B7 ";

    /// <summary>★ 4.4 (4,952) · 5 hr 17 min · 12 chapters · 2016 on ONE line: the star and the rating stay whole, the rest is one caption run that ends
    /// in an ellipsis where the line is short, so no line starts or ends on a separator.</summary>
    static Element Meta(AudiobookView v)
    {
        var kids = new List<Element>(4);
        if (v.Rating.Length > 0)
        {
            kids.Add(Icon(Icons.FavoriteStarFill, AB.StarGlyph, Tok.TextSecondary));
            kids.Add(Body(v.Rating) with { Color = Tok.TextSecondary, Shrink = 0f });
            if (v.RatingCount.Length > 0) kids.Add(Body(v.RatingCount) with { Color = Tok.TextTertiary, Shrink = 0f });
        }
        if (v.Meta.Length > 0)
            kids.Add(Body((kids.Count > 0 ? Sep : "") + string.Join(" " + Sep, v.Meta)) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            });
        return new BoxEl { Direction = 0, Gap = AB.MetaGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [.. kids] };
    }

    static Element Commands(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        float h = lay.Control;
        var library = Button.Create(v.LibraryLabel, p.ToggleLibrary, ButtonAppearance.Standard, glyph: v.InLibrary ? Icons.Check : Icons.Add) with
        {
            Key = AB.LibraryKey, Height = h, Shrink = 0f, MinWidth = 0f,
        };
        string downloadWord = Loc.Get(Strings.Podcast.Pages.Download);
        var download = Bordered(Controls.IconAction(Icons.Download, p.Download, size: h, isEnabled: p.Download is not null), p.Download is not null);
        var kids = new List<Element>(4) { library, ToolTip.Wrap(Controls.Named(download, downloadWord), downloadWord) };
        if (p.Share is { } share)
        {
            string shareWord = Loc.Get(Strings.Podcast.Pages.Share);
            kids.Add(ToolTip.Wrap(Controls.Named(Bordered(Controls.IconAction(Icons.Share, share, size: h), true), shareWord), shareWord));
        }
        kids.Add(WinUiDetailKit.MoreButton(p.Menu, h, WinUiDetailKit.MoreLook.Bordered));
        return new BoxEl { Key = AB.CmdsKey, Direction = 0, Gap = AB.CmdGap, AlignItems = FlexAlign.Start, Shrink = 0f, Children = [.. kids] };
    }

    static BoxEl Bordered(BoxEl b, bool enabled) => b with
    {
        Fill = enabled ? Tok.FillControlDefault : Tok.FillControlDisabled, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
    };

    // ── the up row ──

    static BoxEl Primary(AudiobookView v, AudiobookPageProps p, bool playing, float h, string key)
    {
        string glyph = playing ? WinUiDetailRules.PauseSolid : v.State == AudiobookState.Finished ? Icons.Refresh : WinUiDetailRules.PlaySolid;
        return Button.Create(v.PrimaryLabel, p.Play, v.PrimaryAccent ? ButtonAppearance.Accent : ButtonAppearance.Standard, glyph: glyph) with
        {
            Key = key, Height = h, Shrink = 0f,
        };
    }

    static Element Up(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay, bool playing)
    {
        float h = lay.Control;
        var primary = Primary(v, p, playing, h, AB.PrimaryKey) with { MinWidth = AB.PrimaryMinWidth, Justify = FlexJustify.Start };
        Element? sample = v.State == AudiobookState.NotStarted && v.SampleId is not null
            ? Button.Create(v.SampleButton, p.PlaySample, ButtonAppearance.Standard, glyph: Icons.Play) with { Key = AB.SampleKey + ":up", Height = h, Shrink = 0f, MinWidth = 0f }
            : null;
        Element label = Label(v, p, lay);
        Element tools = new BoxEl
        {
            Direction = 0, Gap = AB.CmdGap, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children =
            [
                Embed.Comp(new ToolProps(p, lay.ToolWords), static () => new SpeedHost()) with { Key = AB.SpeedKey },
                Embed.Comp(new ToolProps(p, lay.ToolWords), static () => new SleepHost()) with { Key = AB.SleepKey },
            ],
        };
        if (lay.Stacked)
        {
            var buttons = sample is null ? new Element[] { primary with { Grow = 1f } } : [primary with { Grow = 1f }, sample];
            return new BoxEl
            {
                Key = AB.UpKey, Direction = 1, Gap = AB.UpGapCompact, MinWidth = 0f,
                Children = [new BoxEl { Direction = 0, Gap = AB.CmdGap, MinWidth = 0f, Children = buttons }, label, tools],
            };
        }
        var kids = sample is null ? new Element[] { primary, label, tools } : [primary, sample, label, tools];
        return new BoxEl { Key = AB.UpKey, Direction = 0, Gap = AB.UpGap, AlignItems = FlexAlign.Center, MinHeight = AB.UpMinHeight, MinWidth = 0f, Children = kids };
    }

    static Element Label(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        Element top;
        Element sub;
        if (v.State == AudiobookState.InProgress)
        {
            var spans = v.UpName.Length > 0
                ? new TextSpan[] { new(v.UpTitle, Color: Tok.TextSecondary), new(v.UpName, Weight: AB.Strong, Color: Tok.TextPrimary) }
                : new TextSpan[] { new(v.UpTitle, Weight: AB.Strong, Color: Tok.TextPrimary) };
            top = new SpanTextEl(spans) { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };
            var place = p.Place;
            bool longLeft = lay.LongLeft;
            var caption = Caption("");
            sub = new BoxEl
            {
                // Where the label is narrow the words go under the bar rather than being cut ("6% · 9 ...", owner 2026-11-18).
                Direction = 0, Wrap = true, Gap = AB.LabelBarGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    Bar(place, lay.BarWidth),
                    caption with
                    {
                        Text = Prop.Of(() => longLeft ? place.Value.BookLeftLong : place.Value.BookLeft), Color = Tok.TextSecondary,
                        Wrap = TextWrap.NoWrap, Shrink = 0f,
                    },
                ],
            };
        }
        else
        {
            top = BodyStrong(v.UpTitle) with { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
            sub = Caption(v.UpSub) with { Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        }
        return new BoxEl
        {
            Direction = 1, Gap = AB.LabelGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [top, sub],
        };
    }

    /// <summary>The whole book's bar: WinUI's 3-DIP bar on its 1-DIP track, the fill bound to the live place.</summary>
    static Element Bar(IReadSignal<AudiobookPlace> place, float width) => new BoxEl
    {
        ZStack = true, Width = width, Height = AB.BarHeight, Shrink = 0f,
        Children =
        [
            new BoxEl { Width = width, Height = AB.BarTrack, OffsetY = (AB.BarHeight - AB.BarTrack) / 2f, Fill = Tok.StrokeControlStrongDefault },
            new BoxEl
            {
                Width = Prop.Of(() => Math.Max(AB.BarHeight, place.Value.Fraction * width)), Height = AB.BarHeight,
                Corners = CornerRadius4.All(AB.BarRadius), Fill = Tok.AccentDefault,
            },
        ],
    };

    // ── the speed and the sleep timer ──

    sealed record ToolProps(AudiobookPageProps P, bool Words);

    sealed class SpeedHost : Component
    {
        public override Element Render()
        {
            var t = UseProps<ToolProps>();
            string label = t.P.SpeedLabel.Value;
            var items = new List<MenuFlyoutItem>();
            foreach (var m in t.P.SpeedMenu()) items.Add(MenuFlyoutItem.RadioItem(m.Label, m.Checked, m.Invoke));
            return ToolTip.Wrap(DropDownButton.Create(label, items), Loc.Get(Strings.Podcast.Pages.Speed));
        }
    }

    sealed class SleepHost : Component
    {
        public override Element Render()
        {
            var t = UseProps<ToolProps>();
            string label = t.P.SleepLabel.Value;
            bool on = t.P.SleepOn.Value;
            var items = new List<MenuFlyoutItem>();
            foreach (var m in t.P.SleepMenu()) items.Add(MenuFlyoutItem.RadioItem(m.Label, m.Checked, m.Invoke));
            return ToolTip.Wrap(DropDownButton.Create(t.Words || on ? label : "", items, glyph: Icons.Moon), Loc.Get(Strings.Podcast.Pages.SleepTimer));
        }
    }

    // ══ THE PREMIUM LINE / THE PRICE ═══════════════════════════════════════════════════════════════════════════════════════════

    static Element Premium(AudiobookView v, AudiobookPageProps p)
    {
        var kids = new List<Element>(5);
        if (v.Included)
        {
            if (v.PremiumLine.Length > 0)
            {
                kids.Add(Icon(Icons.Headphones, AB.PremiumGlyph, Tok.TextTertiary));
                kids.Add(Caption(v.PremiumLine) with { Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f });
                kids.Add(HyperlinkButton.Create(v.CheckHoursLabel, p.CheckHours, size: ControlSize.Small));
                kids.Add(HyperlinkButton.Create(v.TopUpLabel, p.TopUp, size: ControlSize.Small));
            }
        }
        else
        {
            if (v.Price.Length > 0) kids.Add(BodyStrong(v.Price));
            if (v.ListPrice.Length > 0) kids.Add(Caption(v.ListPrice) with { Color = Tok.TextTertiary, Strikethrough = true });
            if (v.BuyLabel.Length > 0 && p.Buy is { } buy) kids.Add(Button.Create(v.BuyLabel, buy, ButtonAppearance.Standard) with { Shrink = 0f });
        }
        return new BoxEl
        {
            Key = AB.PremiumKey, Layout = AB.Block, Direction = 0, Wrap = true, Gap = AB.PremiumGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Margin = new Edges4(0f, 0f, 0f, kids.Count > 0 ? AB.PremiumBottom : 0f), Children = [.. kids],
        };
    }

    // ══ THE COLUMNS ═════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Columns(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        Element chapters = new BoxEl
        {
            Key = AB.ChaptersKey, Layout = AB.Block, Direction = 1, Gap = AB.CardGap, Grow = lay.TwoColumns ? 1f : 0f, Shrink = 1f, Basis = lay.TwoColumns ? 0f : float.NaN,
            MinWidth = 0f,
            Children = [WinUiAudiobookParts.Line(v, p, lay), WinUiAudiobookParts.Contents(v, p, lay)],
        };
        Element about = WinUiAudiobookParts.About(v, p, lay) with { Layout = AB.Block };
        bool aboutFirst = !lay.TwoColumns && v.State == AudiobookState.NotStarted;
        return new BoxEl
        {
            Key = AB.ColumnsKey, Layout = AB.Block, Direction = (byte)(lay.TwoColumns ? 0 : 1), Gap = lay.ColumnGap, MinWidth = 0f,
            AlignItems = lay.TwoColumns ? FlexAlign.Start : FlexAlign.Stretch,
            Children = aboutFirst ? [about, chapters] : [chapters, about],
        };
    }

    // ══ THE BAR ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record BarProps(AudiobookPageProps P, float Inset);

    /// <summary>The 48 bar: the cover, the title over "48% · 9 hr left" (follows the place by the minute), the primary.</summary>
    sealed class BarHost : Component
    {
        public override Element Render()
        {
            var b = UseProps<BarProps>();
            var p = b.P;
            var v = p.View.Value;
            bool playing = p.Playing.Value;
            var left = UseComputed(() => p.Place.Value.BookLeft);
            string caption = v.State == AudiobookState.InProgress ? left.Value : v.BarTitle;
            return WinUiPodcastKit.Bar(v.ArtUrl, v.Title, caption, Primary(v, p, playing, K.BarCover, AB.PrimaryKey + ":bar"), b.Inset);
        }
    }
}
