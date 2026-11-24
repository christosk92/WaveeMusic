// ── Themes/Common/CommonLocalEditHome.cs — "Edit home": show, hide and reorder the collection's Home shelves (#240 P2, board 18) ──────────
//
// The concept's §3 ("Shelves can be hidden and reordered, the most-asked Roon request, from a small 'Edit home' affordance") and board 18's
// "Edit home" at the pivot row's end. One popup for every theme: a card of rows, each a shelf with its check box (shown) and two arrows (move
// up, move down). A change applies at once (the page behind re-renders its Home; the core persists it, `LocalHome`), so the popup has no OK.
// The trigger is the theme's: WinUI/Classic a subtle button with a glyph, Zune a lowercase text verb; both open this same popup.
//
//   CommonLocalEditHome (anchors the popup on the trigger's node)
//   └─ popup: Body — "Edit home" · a line · rows (key "lc-edit-row:<shelf>", their position eased on a reorder)

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using LR = Wavee.Themes.Common.CommonLocalCollectionRules;
using P = Wavee.Strings.LocalFiles.Page;
using X = Wavee.Themes.Common.CommonLocalCollectionParts;

namespace Wavee.Themes.Common;

/// <summary>The "Edit home" trigger and its popup (see the file header).</summary>
internal sealed class CommonLocalEditHome : Component
{
    /// <param name="Trigger">The theme's trigger, built from the toggle (its click) and the realize callback (its node anchors the popup).</param>
    public sealed record Props(LocalCollectionPageProps Page, Func<Action, Action<NodeHandle>, Element> Trigger, FlyoutPlacement Placement);

    NodeHandle _anchor = NodeHandle.Null;
    OverlayHandle? _open;
    IOverlayService? _overlay;
    Action? _toggle;
    Action<NodeHandle>? _realize;
    Props? _props;

    /// <summary>The trigger's key (a verb's one home: the pivot row's end in WinUI/Classic, the head's verbs in Zune).</summary>
    public const string TriggerKey = "lc-edit-home";

    public override Element Render()
    {
        var p = UseProps<Props>();
        _props = p;
        _overlay = UseContext(Overlay.Service);
        _toggle ??= Toggle;
        _realize ??= h => _anchor = h;
        return p.Trigger(_toggle, _realize);
    }

    void Toggle()
    {
        if (_open is { IsOpen: true } open) { open.Close(); return; }
        if (_overlay is not { } overlay || _props is not { } p) return;
        var body = new BodyProps(p.Page);
        OverlayHandle? opened = null;
        opened = overlay.Open(() => _anchor, () => Embed.Comp(body, static () => new Body()), p.Placement,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
        _open = opened;
        opened.ClosedAction = () => { if (ReferenceEquals(_open, opened)) _open = null; };
    }

    sealed record BodyProps(LocalCollectionPageProps Page);

    /// <summary>The popup's card: re-renders on a change of the shelves (the rows keep their nodes and glide to a new place).</summary>
    sealed class Body : Component
    {
        readonly Dictionary<LocalShelf, Signal<bool>> _checks = new();

        public override Element Render()
        {
            var page = UseProps<BodyProps>().Page;
            var shelves = page.Shelves.Value;
            var rows = new Element[shelves.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = Row(page, shelves[i], first: i == 0, last: i == rows.Length - 1);
            return new BoxEl
            {
                Key = "lc-edit-home:card", Direction = 1, Width = LR.EditW, Padding = Edges4.All(LR.EditPad), Gap = LR.EditGap,
                Children =
                [
                    R.Type.DenseTitle(Loc.Get(P.EditHome)) with { Color = Tok.TextPrimary },
                    R.Type.TrackMeta(Loc.Get(P.EditHomeSub)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Margin = new Edges4(0f, 0f, 0f, LR.EditGap) },
                    .. rows,
                ],
            };
        }

        Element Row(LocalCollectionPageProps page, LocalShelfSlot slot, bool first, bool last)
        {
            var shelf = slot.Shelf;
            if (!_checks.TryGetValue(shelf, out var check)) _checks[shelf] = check = new Signal<bool>(slot.Shown);
            var show = page.ShowShelf;
            var move = page.MoveShelf;
            return new BoxEl
            {
                Key = "lc-edit-row:" + shelf.ToString().ToLowerInvariant(), Direction = 0, AlignItems = FlexAlign.Center, Height = LR.EditRowH, Gap = LR.EditGap,
                Layout = LR.CardReflow,
                Children =
                [
                    new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [CheckBox.Create(X.ShelfName(shelf), check, on => show(shelf, on))] },
                    ToolTip.Wrap(IconButton.Create(Icons.ChevronUp, () => move(shelf, -1), isEnabled: !first) with { Key = "lc-edit-up:" + shelf.ToString().ToLowerInvariant() },
                        Loc.Get(P.MoveUp)),
                    ToolTip.Wrap(IconButton.Create(Icons.ChevronDown, () => move(shelf, +1), isEnabled: !last) with { Key = "lc-edit-down:" + shelf.ToString().ToLowerInvariant() },
                        Loc.Get(P.MoveDown)),
                ],
            };
        }
    }
}
