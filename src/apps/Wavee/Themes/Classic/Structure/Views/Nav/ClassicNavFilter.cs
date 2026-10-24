// ── Themes/Classic/Structure/Views/Nav/ClassicNavFilter.cs — Classic's Playlists filter box and empty pane (themes plan 6.1, T5b) ─
//
// From `Shell/Sidebar.UI.cs` (`PaneView.PaneFilterBox`, `PaneView.EmptyPane`, `s_railSpacer`).
//
// THE FILTER BOX is the transient Playlists filter (design V.11): a 32-px field under the Playlists header, focused when it opens
// and again on a second Ctrl+F; Esc, or leaving it empty on blur, closes it and clears the query. The core pane keeps the state
// (which section hosts the box, the query signal, the focus ticket, `CloseFilter`) and reaches the box through
// `INavRowHost.FilterOpenFor` / `FilterQuery` / `FilterTicket` / `CloseFilter`; the section header slot (`ClassicNavRow`) places it.
//
// THE EMPTY BODY is `Shell.NavEmpty` (`NavEmptyProps`): the authored-empty pane names the state only (there is no customizer to
// offer a fix from), and the rail has no room for the hint, so an empty rail is just its head.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>The filter field: the pane's own search signal in a plain editor, focused when it opens and again on a re-press of
/// Ctrl+F (the ticket moves).</summary>
internal sealed class ClassicNavFilterBox(INavRowHost owner) : Component
{
    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var post = UsePost();
        var root = UseRef<NodeHandle>(default);
        // A fresh box takes focus on its first realization; a later Ctrl+F moves focus through the ticket.
        var pending = UseRef(true);
        int ticket = owner.FilterTicket.Value;
        void Focus(NodeHandle h) => post(() => hooks.FocusNode?.Invoke(hooks.FirstFocusableIn?.Invoke(h) ?? h, true));
        UseLayoutEffect(() =>
        {
            if (!root.Value.IsNull) Focus(root.Value);
        }, DepKey.From(ticket));

        var parts = UseMemo(() =>
        {
            var pr = new TemplateParts();
            pr[EditableText.PartRoot] = b => b with
            {
                Fill = ColorF.Transparent, HoverFill = ColorF.Transparent,
                OnRealized = h =>
                {
                    root.Value = h;
                    if (!pending.Value) return;
                    pending.Value = false;
                    Focus(h);
                },
            };
            return pr;
        }, DepKey.Empty);

        return new BoxEl
        {
            Direction = 1, Height = ClassicNavHeadLayout.FilterHeight,
            Margin = new Edges4(SidebarGeometry.HeaderTextX, 0f, SidebarGeometry.TrailingPad, 4f),
            Children =
            [
                Embed.Comp(() => new EditableText
                {
                    Text = owner.FilterQuery, Placeholder = Loc.Get(Strings.Sidebar.V3.SearchPlaceholder),
                    Width = float.NaN, Height = ClassicNavHeadLayout.FilterHeight, FontSize = ClassicNavHeadLayout.FilterFontSize, Parts = parts,
                    ShowDeleteButton = true,
                    PreviewKeyDown = e =>
                    {
                        if (e.KeyCode != Keys.Escape) return false;
                        owner.CloseFilter();
                        return true;
                    },
                    OnFocusChanged = gained =>
                    {
                        if (!gained && owner.FilterQuery.Peek().Length == 0) owner.CloseFilter();
                    },
                }),
            ],
        };
    }
}

/// <summary>Classic's <c>Shell.NavEmpty</c>: the pane body when the plan has no rows.</summary>
internal static class ClassicNavEmpty
{
    /// <summary>The rail's empty body: the rail has no room for the empty hint, so an empty rail is just its head.</summary>
    static readonly BoxEl s_railSpacer = new() { Key = "empty", Grow = 1f };

    /// <summary>The registered template of <see cref="ViewKeys.ShellNavEmpty"/>.</summary>
    public static Element Template(NavEmptyProps props, in FluentGpu.Theming.ViewContext ctx)
        => props.Compact ? s_railSpacer : EmptyPane();

    /// <summary>The authored-empty pane (ch 25 W8): it names the state only. There is no customizer to offer a fix from.</summary>
    static Element EmptyPane()
    {
        var kids = new List<Element>(2)
        {
            Icon(Icons.SplitView, 24f, Tok.TextTertiary),
            new TextEl(Loc.Get("sidebar.paneEmpty"))
            {
                Size = ClassicNavHeadLayout.EmptyTitleSize, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2,
            },
        };
        return new BoxEl
        {
            Key = "empty", Direction = 1, Grow = 1f, Gap = Spacing.S,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(16f, 24f, 16f, 24f),
            Children = [.. kids],
        };
    }
}
