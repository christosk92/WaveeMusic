// ── Themes/Zune/Structure/Views/Home/ZuneHomeCustomize.cs — Zune's Home customizer (`Pages.HomeCustomize`; audit B5a, V20) ───────────────────
//
// The Common customizer (`Themes\Common\CommonCustomize.cs`) drew a small Title Case title and 11-DIP-looking rows under Zune. No board draws it
// (owner Q18), so it takes the voice of Zune's utility screens (Z-Settings-*): the panorama title "customize home" bled to the gutter (it wraps
// whole words where the column is narrow, never shrinks), the body line under it with "reset to default" as a word at the row's end, then one
// 56-tall row per Home section: the gripper, the section's word in the 15/20 voice (lowercase: these are the theme's words), up and down (the
// keyboard's way to reorder), and the visibility switch. A hidden section's row dims. The title stands over the scroll, as Settings' does.
//
// The layout document, the ONE `HomeLayout` instance Home reads too, the reorder behaviour and the three verbs are the core host's (`CustomizeScreen`);
// this view wires the reorderable to its own render, as the Common view does, and draws the document it is handed. Numbers: `Rules\ZuneUtilityRules.cs`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using U = Wavee.Themes.Zune.ZuneUtilityRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Home customizer.</summary>
internal static class ZuneHomeCustomize
{
    /// <summary>The page title's node.</summary>
    internal const string TitleKey = "zc:title";

    /// <summary>The registered template of <see cref="ViewKeys.HomeCustomize"/>.</summary>
    public static Element Template(HomeCustomizeProps props, in ViewContext ctx)
    {
        var doc = props.Doc;
        var reorder = props.Reorder;
        int n = doc.Zones.Count;
        reorder.Scene = ctx.Hooks.Scene;
        reorder.RequestRender = ctx.Hooks.RequestRerender;
        reorder.ItemCount = n;
        reorder.ItemExtent = U.CustomizeRowH;
        var lay = ZuneSettingsTiers.Of(ctx.Tier());

        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int item = reorder.ItemAt(i);
            if ((uint)item >= (uint)n) item = i;
            var entry = doc.Zones[item];
            rows[i] = reorder.Item(item, Embed.Comp(new RowProps(entry, item, n, props.Toggle, props.Move), static () => new ZoneRow()), key: entry.Kind);
        }

        var column = new BoxEl
        {
            Key = "zc:column", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Padding = new Edges4(0f, 0f, 0f, U.PageBottom),
            Children =
            [
                new BoxEl
                {
                    // Its height steps with the link's target (40 at Compact): it eases, and the rows under it ride it.
                    Key = "zc:meta", Direction = 0, AlignItems = FlexAlign.Center, Gap = U.HeadGap, MinWidth = 0f, Animate = ZuneMotion.HeightResize,
                    Margin = new Edges4(0f, ZuneSettingsRules.TitleBodyGap, 0f, U.HeadBottom),
                    Children =
                    [
                        // A sentence the theme writes: it keeps its casing (ZuneText.Prose).
                        ZuneText.Prose(Loc.Get(Strings.Home.CustomizeBody)) with { Color = Tok.TextTertiary, Grow = 1f, Shrink = 1f },
                        K.Link("zc:reset", Loc.Get(Strings.Home.ResetDefault), props.Reset, lay.Target),
                    ],
                },
                reorder.List(new BoxEl { Key = "home-customize-list", Direction = 1, MinWidth = 0f, Children = rows }),
            ],
        };
        // The panorama stands over the scroll, as Settings' does: it bleeds left of the gutter, which a scroll's clip would cut. The scroll spans the
        // window's width with the page column inside it, so its bar's lane runs in the right gutter, never over the rows' switches.
        return new BoxEl
        {
            Key = "home-customize", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                ZuneFrameRules.PageRow(new BoxEl
                {
                    Direction = 1, Shrink = 0f, MinWidth = 0f,
                    Children = [ZuneSettings.ScreenTitle(lay, Loc.Get(Strings.Home.Customizer.Title), TitleKey, wraps: true)],
                }, lay.Gutter),
                // The title's zone steps on a crossing: the list under it eases to its new place, never jumps.
                new BoxEl
                {
                    Key = "zc:list", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Animate = ZuneMotion.ColumnCollapse,
                    Children = [ScrollView(ZuneFrameRules.PageRow(column, lay.Gutter)) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "home-customize", Key = "zc:scroll" }],
                },
            ],
        };
    }

    internal sealed record RowProps(LayoutEntry Entry, int Index, int Count, Action<LayoutZone> Toggle, Action<int, int> Move);

    /// <summary>Gripper · word · up and down · the visibility switch. The switch's own <see cref="Signal{T}"/> freezes at mount
    /// (<c>ToggleSwitch.Create</c>'s contract), so it is kept in sync with the live document through a layout effect, as the Common row does.</summary>
    sealed class ZoneRow : CommonTierComponent
    {
        readonly Signal<bool> _visible = new(true);
        bool _wantVisible;
        Action? _sync;
        Action<bool>? _onToggle;

        public override Element Render()
        {
            var lay = ZuneSettingsTiers.Of(ReadTier());
            var p = UseProps<RowProps>();
            bool visible = p.Entry.Visible;
            _wantVisible = visible;
            UseLayoutEffect(_sync ??= SyncToggle, DepKey.From(visible ? 1 : 0, p.Index));

            LayoutZones.TryParse(p.Entry.Kind, out var zone);
            int index = p.Index, count = p.Count;
            var move = p.Move;
            float target = lay.Touch ? lay.Target : Controls.IconButtonSize;

            return new BoxEl
            {
                Direction = 0, Height = U.CustomizeRowH, AlignItems = FlexAlign.Center, Gap = U.CustomizeRowGap,
                Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Opacity = visible ? 1f : U.CustomizeHiddenOpacity,
                Children =
                [
                    Icon(Icons.GripperBar, U.CustomizeGrip, Tok.TextTertiary),
                    ZuneText.Cased(R.Type.TrackTitle(CommonCustomize.ZoneLabel(zone)), LabelKind.Label) with
                    {
                        Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                    },
                    Controls.Named(Controls.IconAction(Icons.ChevronUp, index > 0 ? () => move(index, index - 1) : null, size: target), Loc.Get(Strings.Menu.MoveUp)),
                    Controls.Named(Controls.IconAction(Icons.ChevronDown, index < count - 1 ? () => move(index, index + 1) : null, size: target), Loc.Get(Strings.Menu.MoveDown)),
                    // The bare switch: no on/off word beside it (the row's word names it), so it takes only its own width.
                    ToggleSwitch.Create(_visible, onChange: _onToggle ??= _ => p.Toggle(zone), style: ToggleSwitch.DefaultStyle with { MinWidth = 0f }),
                ],
            };
        }

        /// <summary>Cached once and re-run by the effect on every (visible, index) change: reads the FIELD, never a captured render-local.</summary>
        void SyncToggle()
        {
            if (_visible.Peek() != _wantVisible) _visible.Value = _wantVisible;
        }
    }

}
