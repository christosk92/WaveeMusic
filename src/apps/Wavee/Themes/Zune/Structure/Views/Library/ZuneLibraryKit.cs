// ── Themes/Zune/Structure/Views/Library/ZuneLibraryKit.cs — the parts Zune's library views share (themes plan 6.3; P5 Z7) ─────────────────
//
// Boards Z-Library-*: a WORD is the library's control (sort, view, the pane's verbs): the 13 voice in the secondary ink that lights to the primary on hover,
// the lit one primary, never a plate. The play affordances are the 36 rings of the discovery pages (`ZuneDiscoveryKit.Ring`); a saved heart and the "…" menu
// are the shared hosts. The selection swap of the panes is the production recipe (the entering pane rises 6 while the leaving one fades), words take their
// casing from the case roles, and every size is a number of `ZuneLibraryRules`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>The words and parts Zune's library shares.</summary>
internal static class ZuneLibraryKit
{
    /// <summary>The page's pane rung (<see cref="LibraryPageProps.PaneBeside"/>), provided around the artists' reader so its board draws the picked album's pane beside its middle
    /// column, or lets it step aside, on the same ladder as the grouped albums. Outside a provider (the drill-in) it reads true.</summary>
    public static readonly Context<bool> PaneBeside = new(true);

    /// <summary>The selection swap: the leaving pane fades out as an exit orphan (drawn UNDER the live tree) while the entering one rises 6 DIP into place. Opacity only
    /// (a page-sized subtree is the blur-group perf cliff); faster out than in, so the new album never waits for the old one.</summary>
    public static readonly LayoutTransition PaneSwap = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(LR.SwapInMs, Easing.SmoothOut),
        Enter: new EnterExit(Dy: LR.SwapDy, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(LR.SwapOutMs, Easing.SmoothOut));

    /// <summary>A page-level word (13 voice): the secondary ink, the primary when <paramref name="active"/> or hovered; cased as a command.</summary>
    public static TextEl WordText(string text, bool active) => R.Type.DenseMeta(text) with
    {
        Color = active ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command),
        MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
    };

    /// <summary>A static label word ("sort", "view"): the tertiary ink, cased as a label.</summary>
    public static TextEl LabelText(string text) => R.Type.DenseMeta(text) with
    {
        Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
    };

    /// <summary>One word of a head: a target <paramref name="height"/> tall, optionally followed by a direction chevron.</summary>
    public static Element Word(string key, string text, bool active, Action go, float height, string? chevron = null)
    {
        var kids = new List<Element>(2) { WordText(text, active) };
        if (chevron is not null) kids.Add(Icon(chevron, LR.ChevronGlyph, Tok.TextTertiary));
        return new BoxEl
        {
            Key = key, Direction = 0, Gap = LR.WordChevronGap, AlignItems = FlexAlign.Center, Height = height, Shrink = 0f,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = go,
            Children = kids.ToArray(),
        };
    }

    /// <summary>A 1-DIP rule between two groups of words.</summary>
    public static Element Divider() => new BoxEl
    {
        Width = LR.DividerW, Height = LR.DividerH, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
    };

    /// <summary>A pane title (22 light): one line, cased as written (a name).</summary>
    public static TextEl PaneTitle(string text) => R.Type.QuickplayHead(text) with
    {
        Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>The line of meta under a pane title (12, tertiary).</summary>
    public static TextEl PaneMeta(string text) => R.Type.MicroMeta(text) with
    {
        Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    // ── the ⋯ button ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The More button: its flyout is built lazily AT OPEN from the newest menu factory (the shared context menu host, so a surface never grows a second).</summary>
    public static Element MoreButton(Func<ContextMenuModel?> menu, float size)
        => Embed.Comp(new MoreProps(size, menu), static () => new MoreHost());

    sealed record MoreProps(float Size, Func<ContextMenuModel?> Menu)
    {
        public bool Equals(MoreProps? o) => o is not null && Size == o.Size;
        public override int GetHashCode() => Size.GetHashCode();
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

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
            BoxEl button = Controls.IconAction(Icons.More, null, requestsContext: true, size: p.Size);
            if (!Controls.IsNullOverlay(overlay)) button = ContextSheets.Attach(button, overlay, _menu, s_moreMenu);
            return ToolTip.Wrap(button, Loc.Get(Strings.Common.More));
        }
    }

    /// <summary>The heart: the shared save button at <paramref name="box"/> (keyed on the uri; props freeze at mount).</summary>
    public static Element Save(string uri, string name, float box, float glyph = LR.SaveGlyph)
        => Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = name, Glyph = glyph, Box = box }) with { Key = "save:" + uri };

    /// <summary>The billed artists as one comma-joined run of links (the secondary ink, the primary on hover), each shrinkable and clipped so a long bill yields. An
    /// empty bill is an empty box.</summary>
    public static Element ArtistLine(IReadOnlyList<Controls.Face>? artists)
    {
        if (artists is not { Count: > 0 }) return new BoxEl();
        var kids = new Element[artists.Count * 2 - 1];
        for (int i = 0, k = 0; i < artists.Count; i++)
        {
            if (i > 0) kids[k++] = R.Type.DenseMeta(", ") with { Color = Tok.TextSecondary };
            var face = artists[i];
            var label = R.Type.DenseMeta(face.Name) with
            {
                Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            kids[k++] = face.OnClick is { } go
                ? new BoxEl { Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = go, Shrink = 1f, MinWidth = 0f, Children = [label] }
                : label;
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = kids };
    }

    /// <summary>The pane's title as a link that opens the page: one line, shrinking before anything beside it does.</summary>
    public static BoxEl TitleLink(string title, Action open) => new()
    {
        Direction = 0, Shrink = 1f, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false,
        OnClick = open, Children = [PaneTitle(title)],
    };

    /// <summary>The pane's header: the title (one line, a link that opens the page), the billed attribution, then the meta. A stated height is not drawn: the words stack.</summary>
    public static Element PaneHeader(string title, Action open, Element attribution, string meta) => new BoxEl
    {
        Direction = 1, Gap = LR.PaneTitleGap, Shrink = 0f, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, LR.PaneMetaBottom),
        Children = [TitleLink(title, open) with { Shrink = 0f }, attribution, PaneMeta(meta)],
    };

    /// <summary>The Short rung's pane header: the title link and the verbs in ONE row (the title yields first), because the full header (title, billed artists, meta, then the
    /// verbs) is as tall as the whole 126 DIP page. <paramref name="verbs"/> are the rings and buttons, each already a target; the title is a <paramref name="target"/> tall
    /// link itself (40 at Compact).</summary>
    public static Element BriefHeader(string title, Action open, Element[] verbs, float target)
    {
        var kids = new Element[verbs.Length + 1];
        kids[0] = TitleLink(title, open) with { Grow = 1f, MinHeight = target, AlignItems = FlexAlign.Center };
        Array.Copy(verbs, 0, kids, 1, verbs.Length);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.RowGap, Shrink = 0f, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, LR.ShortPaneBottom), Children = kids,
        };
    }

    /// <summary>The pane's loading twin: the title and meta as bars in the same slots, so the dissolve into the real header moves nothing.</summary>
    public static BoxEl HeaderSkeleton() => new()
    {
        Direction = 1, Gap = LR.PaneTitleGap, Shrink = 0f, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, LR.PaneMetaBottom),
        Children =
        [
            new BoxEl { Height = LR.PaneTitleSlotH, Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start, Children = [Controls.PendingBar(LR.ShimTitleW, LR.ShimTitleBarH)] },
            new BoxEl { Height = LR.PaneMetaSlotH, Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start, Children = [Controls.PendingBar(LR.ShimMetaW, LR.ShimBarH)] },
        ],
    };
}
