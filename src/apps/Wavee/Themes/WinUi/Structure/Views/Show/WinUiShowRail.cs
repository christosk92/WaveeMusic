// ── Themes/WinUi/Structure/Views/Show/WinUiShowRail.cs — WinUI's show header slots (W6b, winui menus/podcasts) ─────────────────────────────────
//
// What the show header's podcast slots (`Detail.FrameSlots` Primary, Ledger, Rating, Badges, Topics, Attribution, Satellites) draw in WinUI's voice
// (W-Show): "▶ Resume · 31 min left" as the AccentButton, "✓ Following" as the neutral toggle, the trailer, the bell, share and "..." as subtle
// 32 glyph buttons, the played ledger on the ACCENT (never the cover's tone), the rating and the topics as standard small buttons. WinUI's header
// (`WinUiDetailFrame.ShowHeaderTemplate`) arranges them; the decisions (which primary, the follow write, the trailer, the rate flyout) stay the core's.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.WinUi.WinUiShowRules;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiShow
{
    static readonly Func<ColorF> s_accent = static () => Tok.AccentDefault;

    /// <summary>The primary by visit: Follow (and a standard "▶ Latest" / "▶ Episode 1" beside it), Resume · N min left, ▶ Episode 1 or ▶ Latest, on the
    /// AccentButton with the play glyph (W-Show). A show with no episodes keeps its play verbs in place, disabled.</summary>
    static Element Primary(ShowPrimaryArgs a)
    {
        bool playable = !a.Empty;
        Element pill = a.Primary switch
        {
            ShowReaderRules.Primary.Follow => Button.Create(Loc.Get(Strings.Artist.Follow), a.Follow, ButtonAppearance.Accent, a.Size, glyph: Icons.Add),
            ShowReaderRules.Primary.Resume => Button.Create(
                Loc.Get(Strings.Podcast.Resume) + " · " + Strings.Podcast.Left(Episode.DurationWords(a.ResumeLeft)), a.Resume, ButtonAppearance.Accent, a.Size,
                glyph: Icons.Play, isEnabled: playable),
            ShowReaderRules.Primary.PlayFirst => Button.Create(Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)), a.First, ButtonAppearance.Accent, a.Size,
                glyph: Icons.Play, isEnabled: playable),
            _ => Button.Create(Loc.Get(Strings.Podcast.Latest), a.Latest, ButtonAppearance.Accent, a.Size, glyph: Icons.Play, isEnabled: playable),
        };
        if (a.Primary != ShowReaderRules.Primary.Follow) return pill;
        Element ghost = a.Ghost == ShowReaderRules.Primary.PlayFirst
            ? Button.Create(Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)), a.First, ButtonAppearance.Standard, a.Size, glyph: Icons.Play, isEnabled: playable)
            : Button.Create(Loc.Get(Strings.Podcast.Latest), a.Latest, ButtonAppearance.Standard, a.Size, glyph: Icons.Play, isEnabled: playable);
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = WinUiDetailRules.ActionGap, AlignItems = FlexAlign.Center, Children = [pill, ghost],
        };
    }

    /// <summary>The played ledger: "3 played · 1 in progress · 10 to go · 2 new" in Caption (W-Show), the three-part bar on the accent before it at Huge
    /// (W-Show-Huge: a short bar on the line) — or, when the hydrate failed, "progress unavailable".</summary>
    static Element Ledger(ShowLedgerArgs a) => Embed.Comp(new LedgerProps(a), static () => new LedgerView());

    sealed record LedgerProps(ShowLedgerArgs Args);

    sealed class LedgerView : Component
    {
        public override Element Render()
        {
            var a = UseProps<LedgerProps>().Args;
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            return LedgerOf(a, WinUiDetailTiers.Of(in facts).Tier >= WidthTier.Huge);
        }
    }

    static Element LedgerOf(ShowLedgerArgs a, bool bar)
    {
        if (a.Failed)
            return new BoxEl
            {
                Direction = 1, Width = a.Width, MinWidth = 0f,
                Children = [Caption(Loc.Get(Strings.Podcast.ProgressUnavailable)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
            };
        int sum = a.Played + a.InProgress + a.ToGo;
        float played = sum > 0 ? a.Played / (float)sum : 0f, progress = sum > 0 ? a.InProgress / (float)sum : 0f;
        TextSpan[] line = a.Fresh > 0
            ? [new TextSpan(Strings.Podcast.Ledger(a.Played, a.InProgress, a.ToGo) + " · "),
               new TextSpan(Strings.Podcast.LedgerNew(a.Fresh), Weight: 600, Color: Tok.AccentTextPrimary)]
            : [new TextSpan(Strings.Podcast.Ledger(a.Played, a.InProgress, a.ToGo))];
        Element words = new SpanTextEl(line) { Size = Caption("").Size, LineHeight = Detail.RailLayout.LedgerLineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, Shrink = 1f };
        return new BoxEl
        {
            Direction = 0, Gap = LedgerGapX, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = bar
                ? [new BoxEl { Direction = 1, Width = LedgerBarWidth, Shrink = 0f, Children = [Controls.LedgerBar(played, progress, s_accent)] }, words]
                : [words],
        };
    }

    /// <summary>The rating row: 4.8 ★ · "12,431 ratings" — a subtle button that opens the rate flyout.</summary>
    static Element Rating(ShowRatingArgs a)
    {
        var button = new BoxEl
        {
            Direction = 0, Gap = RatingGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
            Padding = new Edges4(RatingPadX, RatingPadY, RatingPadX, RatingPadY), Margin = new Edges4(-RatingPadX, 0f, -RatingPadX, 0f), Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = a.Toggle, OnRealized = a.Realized,
            Children =
            [
                Body(a.Average) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap },
                Icon(Icons.FavoriteStarFill, RatingStar, Tok.TextSecondary),
                Body(a.Count) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            ],
        };
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, MaxWidth = float.IsFinite(a.Width) ? a.Width : float.NaN,
            Children = [Controls.Named(button, a.Name)],
        };
    }

    /// <summary>The badge chips on the eyebrow's line: exclusive · E · video (W-Show: "Video" on the subtle chip).</summary>
    static Element Badges(ShowBadgesArgs a)
    {
        var flags = a.Flags;
        var chips = new List<Element>(3);
        if ((flags & ShowFlags.Exclusive) != 0) chips.Add(Controls.Chip(Loc.Get(Strings.Podcast.Badge.Exclusive)));
        if ((flags & ShowFlags.Explicit) != 0) chips.Add(Controls.Chip("E"));
        if ((flags & ShowFlags.Video) != 0) chips.Add(Controls.Chip(Loc.Get(Strings.Podcast.Badge.Video)));
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Detail.RailLayout.BadgeGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = chips.ToArray(),
        };
    }

    /// <summary>The topic words: one standard small button per topic (W-Show's About chips), wrapping, at the rail's measure.</summary>
    static Element Topics(ShowTopicsArgs a)
    {
        var children = new Element[a.Topics.Length];
        for (int i = 0; i < children.Length; i++)
        {
            string topic = a.Topics[i];
            children[i] = Button.Create(topic, () => a.Open(topic), ButtonAppearance.Standard, ControlSize.Small);
        }
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.S, MinWidth = 0,
            MaxWidth = float.IsFinite(a.Width) ? a.Width : float.NaN, Children = children,
        };
    }

    /// <summary>The publisher (the Attribution slot): Body in the accent text ink, opening the facts line (W-Show: "Half Moon Recordings · 4.7 ★ · ...").</summary>
    static Element Publisher(string publisher, float width) => new BoxEl
    {
        Direction = 0, MinWidth = 0f, Shrink = 1f, MaxWidth = float.IsFinite(width) ? width : AboutMeasure,
        Children = [Body(publisher) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
    };

    /// <summary>The satellites (W-Show): "✓ Following" (the neutral toggle with its check; hidden while Follow is the primary) · the trailer · the bell, disabled
    /// with its reason · share · "...". A FIXED array; what comes and goes binds its presence. At Compact (W-Show-Compact) the row is the primary, a square
    /// 40 check toggle and "...": the trailer, the bell and share leave the row for the "..." menu, which carries share and the trailer.</summary>
    static Element[] Satellites(ShowSatellitesArgs a) =>
    [
        new BoxEl { Key = "sat:heart", Visible = a.HeartShown, Children = [Embed.Comp(new ShowFollowProps(a.Uri, a.Name), static () => new ShowFollow()) with { Key = "follow:" + a.Uri }] },
        new BoxEl { Key = "sat:trailer", Visible = a.TrailerShown, Children = [WideOnly(Glyph(Icons.TvMonitor, Loc.Get(Strings.Podcast.Trailer), a.PlayTrailer))] },
        WideOnly(Glyph(Icons.Bell, Loc.Get(Strings.Podcast.Notify.Unavailable), null)) with { Key = "sat:bell" },
        WideOnly(Glyph(Icons.Share, Loc.Get(Strings.Menu.Share), a.Share)) with { Key = "sat:share" },
        Embed.Comp(new MoreSatProps(a.More), static () => new MoreSat()) with { Key = "rail:more" },
    ];

    /// <summary>A satellite only the wider rows carry: gone at Compact (its verb is in the "..." menu there).</summary>
    static Element WideOnly(Element child) => Embed.Comp(new WideOnlyProps(child), static () => new WideOnlyView());

    sealed record WideOnlyProps(Element Child);

    sealed class WideOnlyView : Component
    {
        public override Element Render()
        {
            var p = UseProps<WideOnlyProps>();
            var facts = Wavee.Theming.TierContext.DrawnIn(UseContext(Wavee.Theming.ThemeFactsContext.Signal)).Value.Tiers;
            bool compact = WinUiDetailTiers.Of(in facts).Tier == WidthTier.Compact;
            return new BoxEl { Visible = !compact, Children = [p.Child] };
        }
    }

    static readonly TemplateParts s_followNoShrink = new() { [ToggleButton.PartRoot] = static e => e with { Shrink = 0f, MinWidth = 0f } };

    /// <summary>The show's follow toggle ("✓ Following", the 40 square at Compact), for the library's show pane.</summary>
    internal static Element FollowToggle(string uri, string name) => Embed.Comp(new ShowFollowProps(uri, name), static () => new ShowFollow());

    sealed record ShowFollowProps(string Uri, string Name);

    /// <summary>Follow beside the primary: "✓ Following" / "+ Follow" (the neutral toggle, W-Show), the square 40 check toggle at Compact (W-Show-Compact).
    /// The state is the library's, visible at rest.</summary>
    sealed class ShowFollow : Component
    {
        public override Element Render()
        {
            var p = UseProps<ShowFollowProps>();
            var lib = Controls.Library;
            var facts = Wavee.Theming.TierContext.DrawnIn(UseContext(Wavee.Theming.ThemeFactsContext.Signal)).Value.Tiers;
            bool compact = WinUiDetailTiers.Of(in facts).Tier == WidthTier.Compact;
            bool on = lib is not null && p.Uri.Length > 0 && lib.IsSaved(p.Uri);
            string uri = p.Uri, name = p.Name;
            Action<bool> toggle = _ => lib?.ToggleSaved(uri, name);
            string label = Loc.Get(on ? Strings.Artist.Following : Strings.Artist.Follow);
            if (compact)
                return Controls.Named(Controls.IconAction(on ? Icons.Check : Icons.Add, () => lib?.ToggleSaved(uri, name), size: WinUiDetailRules.CompactTarget) with
                {
                    Key = "follow:square", Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                    BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                }, label);
            return ToggleButton.Controlled(Loc.Get(Strings.Artist.Follow), on, toggle, glyph: Icons.Add, checkedGlyph: Icons.Check,
                checkedLabel: Loc.Get(Strings.Artist.Following), style: WinUiDetailKit.NeutralToggle(ControlSize.Medium), parts: s_followNoShrink) with { Key = "follow:word" };
        }
    }

    sealed record MoreSatProps(Func<ContextMenuModel?> More)
    {
        public bool Equals(MoreSatProps? o) => o is not null;
        public override int GetHashCode() => 0;
    }

    /// <summary>"..." at the row's end: 32, the Compact 40 (W-Show-Compact).</summary>
    sealed class MoreSat : Component
    {
        public override Element Render()
        {
            var p = UseProps<MoreSatProps>();
            var facts = Wavee.Theming.TierContext.DrawnIn(UseContext(Wavee.Theming.ThemeFactsContext.Signal)).Value.Tiers;
            float size = WinUiDetailTiers.Of(in facts).Target;
            return WinUiDetailKit.MoreButton(p.More, size);
        }
    }

    /// <summary>A subtle 32 glyph button with its name as the tooltip; no action = disabled in place.</summary>
    static Element Glyph(string glyph, string name, Action? onClick)
    {
        BoxEl b = Controls.IconAction(glyph, onClick, size: WinUiDetailRules.Target);
        return Controls.Named(onClick is null ? b with { IsEnabled = false, Cursor = null } : b, name);
    }
}
