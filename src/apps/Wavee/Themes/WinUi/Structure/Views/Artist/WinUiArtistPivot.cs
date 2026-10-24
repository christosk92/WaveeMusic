// ── Themes/WinUi/Structure/Views/Artist/WinUiArtistPivot.cs — WinUI's artist pivot (themes plan 6.2; P6 W6a) ─────────────────────────────────────────
//
// A SelectorBar (Overview · Albums · Singles & EPs · Compilations · Appears on · About; 14/20 words, 40 tall items, the accent pill under the lit one), sticky under the hero card on the
// content layer with a hairline at its foot, on the tier's inset. The tabs after Overview are the core's pivot destinations (`ArtistPageProps.Pivot`, in the plan's order); Overview
// scrolls home. The lit tab is the scroll spy's answer (`ArtistPageProps.Active`, -2 above the first section = Overview). The bar's own height (the 40 items and the 1-px hairline, 41)
// is what `ArtistTierPolicy.BandClearance` answers, so a click parks the section exactly under it and the spy measures from the same edge.
//
// Each tab is a keyed child of its own component; the bar re-renders only when the lit tab or the tab set changes (it reads `Active` alone), never per scroll step.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

internal static class WinUiArtistPivot
{
    /// <summary>The sticky bar. A box around the component (a sticky with no scope clamps to its immediate parent, the page column: a component's own root never pins).</summary>
    public static Element Bar(ArtistPageProps p, float inset)
    {
        var labels = new string[p.Pivot.Count + 1];
        labels[0] = Loc.Get(Strings.Theme.Winui.Detail.Overview);
        int at = 1;
        for (int i = 0; i < p.Sections.Count && at < labels.Length; i++)
        {
            var slot = p.Sections[i];
            if (slot.Anchor is null) continue;
            labels[at] = slot.Section switch
            {
                ArtistSection.AppearsOn => Loc.Get(Strings.Artist.AppearsOn),
                ArtistSection.Biography => Loc.Get(Strings.Theme.Winui.Detail.About),
                _ => p.Pivot[at - 1].Label,
            };
            at++;
        }
        for (; at < labels.Length; at++) labels[at] = p.Pivot[at - 1].Label;
        var clicks = new Action[labels.Length];
        clicks[0] = p.ScrollToTop;
        for (int i = 1; i < clicks.Length; i++) clicks[i] = p.Pivot[i - 1].OnClick;
        return new BoxEl
        {
            Key = "artist-pivot-sticky", Direction = 1, MinWidth = 0f,
            Children = [Embed.Comp(new PivotProps(labels, clicks, p.Active, inset), static () => new PivotHost()) with { Key = "artist-pivot" }],
        }.Sticky(0f);
    }

    sealed record PivotProps(string[] Labels, Action[] Clicks, IReadSignal<int> Active, float Inset)
    {
        public bool Equals(PivotProps? o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o is null || !ReferenceEquals(Active, o.Active) || Inset != o.Inset || Labels.Length != o.Labels.Length) return false;
            for (int i = 0; i < Labels.Length; i++)
                if (!string.Equals(Labels[i], o.Labels[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public override int GetHashCode() => HashCode.Combine(Labels.Length, Active, Inset);
    }

    /// <summary>The item's word centred in the 40 and the pill parted from it, at the board's foot (W-Artist: the bar 3 tall, 4 above the item's bottom). The stock
    /// item stacks the pill right under the label box, where it read as an underline on the word.</summary>
    static readonly TemplateParts s_items = new()
    {
        [SelectorBar.PartItem] = static e => e with
        {
            Justify = FlexJustify.End, Gap = WinUiArtistRules.PivotPillGap, Padding = new Edges4(0f, 0f, 0f, WinUiArtistRules.PivotPillLift),
        },
    };

    sealed class PivotHost : Component, IPropsHost
    {
        PivotProps? _latest;
        readonly Signal<PivotProps?> _props = new(null);
        Action<int>? _change;

        public void ApplyProps(object props)
        {
            _latest = (PivotProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            // The lit tab: the spy's section plus Overview's slot; nothing lit above the first section lights Overview.
            int active = p.Active.Value;
            int selected = active >= 0 && active + 1 < p.Labels.Length ? active + 1 : 0;
            _change ??= i => { if (_latest is { } live && (uint)i < (uint)live.Clicks.Length) live.Clicks[i](); };
            var style = new SelectorBarStyle
            {
                RestColor = Tok.TextSecondary, SelectedColor = Tok.TextPrimary, ItemHeight = WinUiArtistRules.PivotItemHeight,
                ItemPadding = WinUiArtistRules.PivotItemPadding, ItemGap = WinUiArtistRules.PivotGap,
            };
            // A FRESH signal per render: the spy, not the control, is the truth; a click scrolls and the spy then lights the tab.
            Element bar = SelectorBar.Create(p.Labels, new Signal<int>(selected), onChange: _change, style: style, parts: s_items);
            Element scroller = new ScrollEl
            {
                Horizontal = true, ContentSized = true, Grow = 1f, Basis = 0f, MinWidth = 0f,
                SuppressScrollBar = true, AutoEdgeFade = true, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl { Direction = 0, Shrink = 0f, Children = [bar] },
            };
            // NO FILL: the band paints nothing of its own (an opaque stand-in read lighter than the translucent page layer over Mica, edge to edge); the
            // page's content cuts itself at the band's lower edge instead (`StickyClip(PivotClearance)` on the magazine, `WinUiArtistPage`).
            return new BoxEl
            {
                Direction = 1, Padding = new Edges4(p.Inset, 0f, p.Inset, 0f),
                Children =
                [
                    new BoxEl { Direction = 0, Height = WinUiArtistRules.PivotItemHeight, MinWidth = 0f, Children = [scroller] },
                    new BoxEl { Height = WinUiArtistRules.PivotHairline, AlignSelf = FlexAlign.Stretch, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                ],
            };
        }
    }
}
