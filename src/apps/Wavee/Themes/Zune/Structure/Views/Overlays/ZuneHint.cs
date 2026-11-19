// ── Themes/Zune/Structure/Views/Overlays/ZuneHint.cs — Zune's `Hint.Line` (settings rework phase 6, design 6.1-6.3 and 7.3) ──────────────────
//
// Zune writes a hint as ONE lowercase line in the meta voice (Z-InContext: "want every word timed as it is sung? word-by-word lyrics run on
// this pc · set up · not now"): the line in the secondary ink, its action an accent word, "not now" a quiet word (the hint's own snooze) and
// "more", a quiet word whose menu offers "don't show tips" (a right-click or a long press on it opens the same menu). Never a card and
// never a box; the display voice never shrinks to make room: the words wrap under the line.
//
//   in place     the lyrics pane's footer (on the lyrics' gutter), the devices picker under its quality row (on the rows' text column), the
//                bell panel under its header: the bare line on the surface.
//   floating     by a control (the play button, the volume, the queue's crossfade word) and at the top of Home: the same line on a band of the
//                player bar's own ink (no ring, no corners, no shadow: the bar grows a line), floating in the overlay layer at that control
//                (`CommonHintFloat`), so the page under it never moves. It stays inside the surface the control lives in: in the queue panel the
//                band runs under the modes row from edge to edge, its words on the panel's content column; above the player bar it keeps Zune's
//                inset from the bar's ends, centred on the play disc or ending at the volume.
//
// A state line (the metered cap) keeps its "change" word and has no "not now". After an inline write: the confirmation ("on · normal") and its
// accent "change". Keys: `hint:<id>`, the words `:act`, `:change`, `:dismiss`, `:more`. The entrance slides the line in from the right as it
// fades (Zune's words arrive sideways). Every word is at least the tier's target (the kit's commands).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Common;

namespace Wavee.Themes.Zune;

/// <summary>Zune's hint template (see the file header).</summary>
internal static class ZuneHint
{
    /// <summary>The registered template of <see cref="ViewKeys.Hint"/>.</summary>
    public static Element Template(HintProps p, in ViewContext ctx)
    {
        var tier = ctx.Tier();
        var lay = ZuneSettingsTiers.For(tier.Content);
        var band = ZuneOverlayTiers.Of(tier);
        float viewport = ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width;
        if (p.Target is not null)
        {
            Edges4 padding = p.Surface == HintSurfaces.QueuePanel ? ZuneHintRules.QueueBandPadding : ZuneHintRules.BandPadding;
            return CommonHintFloat.Create(p, PlaceOf(p.Surface), (int)tier.Window, (h, width) => Band(h, lay, width, padding));
        }
        return p.Surface switch
        {
            HintSurfaces.HomeBanner => Band(p, lay, band.PanelWidth(ZuneHintRules.BannerMaxWidth, viewport), ZuneHintRules.BandPadding) with
            {
                Margin = new Edges4(0f, ZuneHintRules.BannerTop(tier), 0f, 0f),   // under the title bar and the band, never over the pivots
            },
            HintSurfaces.LyricsFooter => Line(p, lay, ZuneHintRules.LyricsMargin),
            HintSurfaces.DevicePicker => Line(p, lay, ZuneHintRules.PickerMargin),
            HintSurfaces.BellHeader => Line(p, lay, ZuneHintRules.BellMargin),
            _ => CommonHint.Hint(p, ctx),   // a surface Zune does not draw (fast navigation and the backdrop are not Zune's): the neutral fallback
        };
    }

    /// <summary>Where a floating line sits in its surface: under the queue's modes row, edge to edge in the panel (it covers the list, not the
    /// words); above the player bar's play disc (centred) and volume (end-aligned), Zune's inset from the bar's ends.</summary>
    static HintFloatPlace PlaceOf(string surface) => surface switch
    {
        HintSurfaces.QueuePanel => new(Below: true, HintFloatAlign.Start, ZuneHintRules.QueueInset, ZuneHintRules.FloatMaxWidth),
        HintSurfaces.PlayerVolume => new(Below: false, HintFloatAlign.End, ZuneHintRules.FloatInset, ZuneHintRules.FloatMaxWidth),
        _ => new(Below: false, HintFloatAlign.Center, ZuneHintRules.FloatInset, ZuneHintRules.FloatMaxWidth),
    };

    /// <summary>The line on a band of the player bar's ink, <paramref name="width"/> wide (its words wrap inside it).</summary>
    static BoxEl Band(HintProps p, ZuneSettingsLayout lay, float width, Edges4 padding) => new BoxEl
    {
        Key = "hint:" + p.Id + ":band", Width = width, Direction = 1, Padding = padding, Fill = R.Layer.PlayerBar,
        Enter = new EnterExit(Dx: ZuneHintRules.EnterDx, Opacity: 0f, Active: true),
        Children = [Line(p, lay, default) with { Enter = default }],
    };

    /// <summary>The line: the words of the hint, then its action, "not now" and "more".</summary>
    static BoxEl Line(HintProps p, ZuneSettingsLayout lay, Edges4 margin)
    {
        string key = "hint:" + p.Id;
        var k = new ZuneKit(lay);
        var kids = new List<Element>(4)
        {
            ZuneText.Cased(R.Type.DenseMeta(p.Done ? p.DoneText ?? p.Text : p.Text), LabelKind.Label) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.WrapWholeWords, MinWidth = 0f, Shrink = 1f, Margin = new Edges4(0f, 0f, ZuneHintRules.WordGap, 0f),
            },
        };
        if (p.Done)
        {
            if (p.Change is { } change && p.ChangeLabel is { } changeLabel) kids.Add(k.Cmd(changeLabel, change, accent: true) with { Key = key + ":change" });
        }
        else
        {
            if (p.Act is { } act && p.ActionLabel is { } label) kids.Add(k.Cmd(label, act, accent: true) with { Key = key + ":act" });
            if (p.Dismiss is { } dismiss) kids.Add(k.Cmd(Loc.Get(Strings.Hints.NotNow), dismiss) with { Key = key + ":dismiss" });
            if (p.NeverTips is { } never) kids.Add(Embed.Comp(new MoreProps(key, never, lay), static () => new More()) with { Key = key + ":more" });
        }
        return new BoxEl
        {
            Key = key, Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f, Margin = margin,
            Role = AutomationRole.InfoBar,
            Enter = new EnterExit(Dx: ZuneHintRules.EnterDx, Opacity: 0f, Active: true),
            Children = [.. kids],
        };
    }

    sealed record MoreProps(string Key, Action Never, ZuneSettingsLayout Lay);

    /// <summary>"more": a quiet word whose menu offers "don't show tips" (the hint's overflow); a right-click or a long press opens it too.</summary>
    sealed class More : Component
    {
        public override Element Render()
        {
            var p = UseProps<MoreProps>();
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var never = p.Never;

            void Open()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                OverlayHandle? mine = null;
                mine = overlay.Open(() => anchor.Value,
                    () => MenuFlyout.Create([new MenuFlyoutItem(Loc.Get(Strings.Hints.NeverTips), default, true, never)], () => mine?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedLeft, PopupOptions.Menu);
                handle.Value = mine;
                mine.ClosedAction = () => { if (ReferenceEquals(handle.Value, mine)) handle.Value = null; };
            }

            return new ZuneKit(p.Lay).Cmd(Loc.Get(Strings.Common.More), Open) with
            {
                OnRealized = h => anchor.Value = h, OnContextRequested = _ => Open(),
            };
        }
    }
}
