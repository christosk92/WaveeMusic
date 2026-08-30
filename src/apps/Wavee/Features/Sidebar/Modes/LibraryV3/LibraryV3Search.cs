using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// §3.2.5 — the library-only search: a 28-DIP magnifier that EXPANDS into a plain filter field.
///
/// <para>Scope is the whole point: this filters ONLY the sidebar projection (<c>prefs.V3Search</c>, which the projection
/// binder folds into its rebuild trigger). It never navigates, never touches the omnibar, and never issues a catalog
/// query. Both the text and the open flag are SESSION-ONLY — a relaunch opens with an empty, closed field.</para>
///
/// <para>The field is <c>AutoSuggestBox</c> with an EMPTY suggestion source, which is the engine's documented
/// "plain filter field" usage (<c>HasSuggestionSource == false</c> ⇒ the suggestion popup can never open) — the same way
/// <c>LibraryPage.Toolbar</c> uses it, so the two filter fields cannot drift.</para>
/// </summary>
sealed class LibraryV3Search : Component
{
    /// <summary>Open/close motion (§3.2.16): the wrapper's width tweens over the Fast rung and the icon
    /// cross-fades (authored 180/100 ms; both legs snapped to the ladder, keeping the enter:exit ratio).</summary>
    static readonly LayoutTransition FieldReveal = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(WaveeMotion.Fast, Easing.SmoothOut),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(WaveeMotion.Faster, Easing.SmoothOut));

    static readonly string[] NoSuggest = [];

    readonly LibraryV3Session _session;

    public LibraryV3Search(LibraryV3Session session) => _session = session;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var hooks = UseContext(InputHooks.Current);
        var fieldNode = UseRef<NodeHandle>(default);
        var buttonNode = UseRef<NodeHandle>(default);

        // ONE memoised parts map: mutating a TemplateParts bumps its Epoch and invalidates the engine's apply-once
        // prototype cache, so it must be built at mount and never per render.
        var parts = UseMemo(() =>
        {
            var p = new TemplateParts();
            p[AutoSuggestBox.PartRoot] = b => b with { OnRealized = h => fieldNode.Value = h };
            return p;
        }, DepKey.Empty);

        bool open = prefs?.V3SearchOpen.Value ?? false;
        string text = prefs?.V3Search.Value ?? "";

        // Focus the editor once per OPEN. PartRoot is the ComboBox chrome; OnChar walks ancestors only, so
        // focusing chrome paints a ring that cannot type. FirstFocusableIn lands on the chromeless EditableText
        // (query button is later in document order). See .claude/skills/wavee/focus-pitfalls.md.
        UseLayoutEffect(() =>
        {
            if (!open) return;
            var chrome = fieldNode.Value;
            if (chrome.IsNull) return;
            var editor = hooks.FirstFocusableIn?.Invoke(chrome) ?? NodeHandle.Null;
            if (!editor.IsNull) hooks.FocusNode?.Invoke(editor, true);
        }, DepKey.From(open ? 1 : 0));

        // A search FLATTENS the tree (Foundation obligation 3), so there is no folder to be "inside" of: leave any
        // drill-in level the moment a query exists.
        UseLayoutEffect(() =>
        {
            if (text.Length > 0) _session.ResetDrill();
        }, DepKey.From(text.Length > 0 ? 1 : 0));

        if (prefs is not { } p) return new BoxEl();

        if (!open)
            return ToolTip.Wrap(new BoxEl
            {
                Key = "v3-search-button",
                Width = 28f, Height = 28f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                Animate = FieldReveal,
                OnRealized = h => buttonNode.Value = h,
                OnClick = () => p.SetV3SearchOpen(true),
                Children = [Icon(Icons.Search, 14f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle), Loc.Get(Strings.Sidebar.V3.SearchTooltip));

        return new BoxEl
        {
            Key = "v3-search-field",
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
            Animate = FieldReveal,
            // ONE Escape = clear the text (the filter is the thing you want gone first); a second Escape closes the
            // field and hands focus back to the magnifier. Key events from the editor bubble here.
            OnKeyDown = e =>
            {
                if (e.KeyCode != Keys.Escape) return;
                if (p.V3Search.Peek().Length > 0)
                {
                    p.V3Search.SetIfChanged("");
                }
                else
                {
                    p.SetV3SearchOpen(false);
                    var b = buttonNode.Value;
                    if (!b.IsNull) hooks.FocusNode?.Invoke(b, true);
                }
                e.Handled = true;
            },
            Children =
            [
                AutoSuggestBox.Create(NoSuggest, Loc.Get(Strings.Sidebar.V3.SearchPlaceholder), text: p.V3Search,
                                      queryIcon: Icons.Search, grow: 1f, maxFillWidth: 9999f,
                                      minHeight: 28f, cornerRadius: Radii.Control, parts: parts),
            ],
        };
    }
}
