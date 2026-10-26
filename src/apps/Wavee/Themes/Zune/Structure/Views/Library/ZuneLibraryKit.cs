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

    /// <summary>The gap between two columns of the page or the board, as a box: its width eases when a tier changes it (40 ↔ 48), which a <c>Gap</c> cannot.</summary>
    public static Element ColGap(string key, float width) => new BoxEl { Key = key, Width = width, Shrink = 0f, Animate = LR.ColumnResize };

    /// <summary>The THIRD column's slot (the album's songs beside the grouped albums, the artist board's picked album): one node at every width, its declared width the
    /// pane's and the gap before it while the pane stands <paramref name="beside"/>, 0 while it steps aside, eased through layout (<see cref="LR.ColumnResize"/>) so the
    /// flexible middle column rides every tick both ways. The content stands at the pane's own width inside it (clipped, never squeezed) and cross-fades in and out
    /// (<see cref="ZuneMotion.SwapFade"/>, its entrance only once the view has <paramref name="rendered"/>). <paramref name="grow"/> (Huge's albums): the slot rests at the
    /// pane's width and takes its share of the page up to <paramref name="maxWidth"/>.</summary>
    public static Element PaneSlot(string key, float width, float gap, bool beside, bool rendered, string? contentKey, Element? content,
        float grow = 0f, float maxWidth = float.NaN)
    {
        Element[] kids = content is null || contentKey is null
            ? []
            :
            [
                new BoxEl
                {
                    Key = contentKey, Direction = 1, Grow = 1f, MinHeight = 0f, Shrink = 0f, ClipToBounds = true,
                    Width = grow > 0f ? float.NaN : width, Margin = new Edges4(gap, 0f, 0f, 0f),
                    Enter = ZuneMotion.SwapEnter(rendered), Exit = ZuneMotion.SwapFade, Children = [content.Value],
                },
            ];
        bool grows = beside && grow > 0f;
        return new BoxEl
        {
            Key = key, Direction = 1, Shrink = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, Animate = LR.ColumnResize,
            Width = grows ? float.NaN : beside ? width + gap : 0f,
            Basis = grows ? width + gap : float.NaN, Grow = grows ? grow : 0f, MaxWidth = grows ? maxWidth + gap : float.NaN,
            Children = kids,
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

    /// <summary>The pane's head in ONE tree for both height rungs: [the words, the verbs]. Regular: a column, the words (the title link, the billed
    /// <paramref name="attribution"/>, the meta) over the verbs' row. The Short rung (<paramref name="brief"/>): ONE row, the title link (a <paramref name="target"/> tall link,
    /// yielding first) beside the verbs, the attribution and the meta hidden, because the full head is as tall as the whole 126 DIP page. The words and the verbs keep their
    /// nodes across a height crossing (only the head's direction and the parts' visibility change), so no verb is rebuilt. <paramref name="verbs"/> are the rings and buttons,
    /// each already a target, in the same slots in both forms (a caller hides what one form does not draw).</summary>
    public static Element PaneHead(string title, Action open, Element attribution, string meta, Element[] verbs, float target, bool brief) => new BoxEl
    {
        Key = "lib-pane-head", Direction = (byte)(brief ? 0 : 1), AlignItems = brief ? FlexAlign.Center : FlexAlign.Stretch, Gap = brief ? LR.RowGap : 0f,
        Shrink = 0f, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, brief ? LR.ShortPaneBottom : 0f),
        Children =
        [
            new BoxEl
            {
                Key = "lib-pane-words", Direction = 1, Gap = LR.PaneTitleGap, MinWidth = 0f, Justify = FlexJustify.Center,
                Grow = brief ? 1f : 0f, Shrink = brief ? 1f : 0f, Basis = brief ? 0f : float.NaN,
                Padding = new Edges4(0f, 0f, 0f, brief ? 0f : LR.PaneMetaBottom),
                Children =
                [
                    TitleLink(title, open) with { Shrink = 0f, MinHeight = brief ? target : 0f, AlignItems = FlexAlign.Center },
                    new BoxEl { Direction = 1, MinWidth = 0f, Visible = !brief, Children = [attribution] },
                    PaneMeta(meta) with { Visible = !brief },
                ],
            },
            new BoxEl
            {
                Key = "lib-pane-verbs", Direction = 0, AlignItems = FlexAlign.Center, Gap = brief ? LR.RowGap : LR.PaneCommandsGap, Shrink = 0f, MinWidth = 0f,
                Padding = brief ? default : new Edges4(0f, LR.PaneCommandsTop, 0f, LR.PaneCommandsBottom),
                Children = verbs,
            },
        ],
    };

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
