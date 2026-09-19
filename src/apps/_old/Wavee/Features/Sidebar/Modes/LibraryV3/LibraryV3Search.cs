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
/// §3.2.5 — the library-only search. TWO shapes, decided by <see cref="LibraryV3SearchRules.Resolve"/>:
/// <list type="bullet">
/// <item><b>Inline</b> (pane ≥ <see cref="LibraryV3SearchRules.InlineWidth"/>): the field is simply THERE — a
///   transparent, borderless lane with the magnifier and "Search in Your Library", growing to fill the toolbar beside
///   the full sort/view pill. Nothing to open, nothing to morph: if there is room, the field is already expanded.</item>
/// <item><b>Narrow</b>: a 32-DIP magnifier button; a click morphs the SAME keyed host (<c>Key="v3-search"</c>) to the
///   row's width with a <see cref="SizeMode.Reflow"/> tween (so the sort pill is genuinely pushed to icon-only), and an
///   empty blur / a second Escape collapses it again.</item>
/// </list>
///
/// <para>THE MAGNIFIER NEVER MOVES. It is one always-mounted glyph pinned to the host's leading 32 DIP; the field layer
/// behind it pads its text lane past that glyph. In the narrow morph the only things that change are the host's width
/// and the text appearing — the icon the eye is on stays exactly where it was.</para>
///
/// <para>Scope is the whole point: this filters ONLY the sidebar projection (<c>prefs.V3Search</c>, which the
/// projection binder folds into its rebuild trigger, debounced). It never navigates, never touches the omnibar, and
/// never issues a catalog query. The narrow-mode open flag is SESSION-ONLY (<see cref="LibraryV3Session.SearchOpen"/>);
/// the text itself is <c>prefs.V3Search</c>, likewise never persisted.</para>
/// </summary>
sealed class LibraryV3Search : Component
{
    readonly LibraryV3Session _session;

    public LibraryV3Search(LibraryV3Session session) => _session = session;

    /// <summary>The host's edge in both shapes (the closed button is a 32-DIP square; the field is 32 tall). One number,
    /// so opening never changes the row's height — only its width.</summary>
    const float HostEdge = LibraryV3SearchRules.ClosedWidth;

    // Reflow, never Reveal: the narrow-mode host must PUSH the spacer/sort pill through real layout every tick (Reveal
    // lays out at the FINAL size immediately and only eases a clip window, which snaps the neighbours on frame 1).
    static readonly LayoutTransition HostMorph = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(WaveeMotion.Fast, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    // The field layer fades in over the pinned glyph and out again on collapse (a keyed Enter/Exit, so the editor
    // genuinely mounts and unmounts — its focus latch and caret state must not survive a collapse).
    static readonly LayoutTransition FieldFade = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(WaveeMotion.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(WaveeMotion.Faster, Easing.FluentAccelerate));

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var hooks = UseContext(InputHooks.Current);
        var post = UsePost();
        var hostNode = UseRef<NodeHandle>(default);
        // Set by the narrow-mode click, consumed by the field's first realization: the inline field mounts at startup
        // and on every seam drag past the threshold, and must NOT steal focus then — only a deliberate open focuses.
        var focusOnMount = UseRef(false);

        if (prefs is not { } p) return new BoxEl();

        string text = p.V3Search.Value;
        bool hasText = text.Length > 0;
        // Equality-gated memo: a seam drag re-renders this component only when the SHAPE flips, not per frame.
        var layout = UseComputed(() => LibraryV3SearchRules.Resolve(
            _session.Width.Value, _session.SearchOpen.Value, _session.Prefs?.V3Search.Value is { Length: > 0 })).Value;
        bool inline = layout.Inline;
        bool expanded = layout.Expanded;

        // Narrow-mode open width, quantized (MathF.Round) so a drag re-renders only when the INTEGER width moves.
        var openWidth = UseComputed(() => MathF.Round(LibraryV3SearchRules.OpenWidth(
            _session.Width.Value, SidebarPaneMetrics.LeadInset + SidebarPaneMetrics.ContentLaneEnd)));

        // ONE memoised parts map (mutating a TemplateParts bumps its Epoch — never rebuild this per render).
        //  · PartRoot: focus the EDITOR after commit (UsePost — the node is not laid out yet inside OnRealized) when a
        //    click opened the field, through FirstFocusableIn so programmatic focus never lands on chrome that cannot
        //    type (.claude/skills/wavee/focus-pitfalls.md). The editor's own state fills are made transparent: the field
        //    is a transparent lane in every state, so focusing cannot flip it into WinUI's dark InputActive plate.
        //  · PartLane: the text lane starts past the pinned magnifier (HostEdge) so the glyph and the text never overlap.
        var parts = UseMemo(() =>
        {
            var pr = new TemplateParts();
            pr[EditableText.PartRoot] = b => b with
            {
                Fill = ColorF.Transparent, HoverFill = ColorF.Transparent,
                OnRealized = h =>
                {
                    if (!focusOnMount.Value) return;
                    focusOnMount.Value = false;
                    post(() =>
                    {
                        var ed = hooks.FirstFocusableIn?.Invoke(h) ?? h;
                        hooks.FocusNode?.Invoke(ed, true);
                    });
                },
            };
            pr[EditableText.PartLane] = b => b with { Padding = new Edges4(HostEdge, 0f, 4f, 0f) };
            return pr;
        }, DepKey.Empty);

        void Open()
        {
            focusOnMount.Value = true;
            _session.OpenSearch();
        }

        // The pinned magnifier — the one element both shapes share. Inert to the pointer so a press anywhere on the
        // field places the caret, and a press on the closed button reaches the host.
        var glyph = new BoxEl
        {
            Key = "search:glyph",
            Width = HostEdge, Height = HostEdge, Shrink = 0f, HitTestVisible = false,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start,
            Children = [Icon(Icons.Search, 16f, Tok.TextSecondary)],
        };

        // The query region is GROW-based, not width-computed: it tracks the HOST's width every tick (in the narrow
        // morph the host is the thing animating), so the field never measures against a stale final width.
        Element layer = expanded
            ? new BoxEl
            {
                Key = "search:field", Animate = FieldFade, Direction = 1, Grow = 1f, MinWidth = 0f, Height = HostEdge,
                Children =
                [
                    Embed.Comp(() => new EditableText
                    {
                        Text = p.V3Search, Placeholder = Loc.Get(Strings.Sidebar.V3.SearchPlaceholder),
                        Width = float.NaN, Height = HostEdge, FontSize = 14f, Chromeless = true, Parts = parts,
                        ShowDeleteButton = true,   // the WinUI inline ✕ — clears the query, keeps focus
                        // Runs BEFORE the editor's own Escape handling (revert-to-snapshot then blur), and returning
                        // true pre-empts it: the field must never "un-cancel" back to whatever it held at focus time.
                        PreviewKeyDown = e =>
                        {
                            if (e.KeyCode != Keys.Escape) return false;
                            switch (LibraryV3SearchRules.OnEscape(p.V3Search.Peek()))
                            {
                                case LibraryV3SearchRules.EscapeAction.Clear:
                                    p.V3Search.SetIfChanged("");
                                    return true;
                                default:
                                    // Inline: nothing to close — let the editor's own Escape blur the (empty) field.
                                    if (LibraryV3SearchRules.Resolve(_session.Width.Peek(), true, false).Inline) return false;
                                    _session.CloseSearch();
                                    var b = hostNode.Value;
                                    if (!b.IsNull) hooks.FocusNode?.Invoke(b, true);   // hand focus back to the magnifier
                                    return true;
                            }
                        },
                        OnFocusChanged = gained =>
                        {
                            if (gained) return;
                            // Narrow only: an empty field that lost focus collapses back to the button. Inline has
                            // nothing to collapse to.
                            if (!LibraryV3SearchRules.Resolve(_session.Width.Peek(), true, false).Inline
                                && LibraryV3SearchRules.ClosesOnBlur(p.V3Search.Peek()))
                                _session.CloseSearch();
                        },
                    }),
                ],
            }
            // COLLAPSED (narrow only): a hit layer that carries the tooltip. The tooltip rides THIS layer and not the
            // host, so it exists only while the control is a button — a tooltip on the host kept popping over
            // "Your Library" while the user was typing into the field.
            : ToolTip.Wrap(new BoxEl
            {
                Key = "search:hit", Width = HostEdge, Height = HostEdge,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                OnClick = Open,
            }, Loc.Get(Strings.Sidebar.V3.SearchTooltip));

        return new BoxEl
        {
            Key = "v3-search", ZStack = true, ClipToBounds = true, Height = HostEdge,
            // INLINE fills the row (Grow) AND yields (Shrink): the placeholder's natural width is wider than the lane a
            // 300-DIP pane leaves beside the labelled sort pill, and a non-shrinking host pushed that pill off the pane.
            // NARROW is an explicit width so the reflow tween has a from and a to; it never shrinks (the tween owns the
            // width). The morph recipe is attached only where a morph can happen — a growing node has no declared width.
            Grow = inline ? 1f : 0f, Shrink = inline ? 1f : 0f, MinWidth = 0f,
            Width = inline ? float.NaN : expanded ? openWidth.Value : HostEdge,
            Animate = inline ? null : HostMorph,
            // Transparent and borderless in every state (the field is a lane, not a box); the only surface is the
            // collapsed button's hover/press, cross-faded over the same duration as the width tween.
            Corners = Radii.ControlAll,
            Fill = ColorF.Transparent,
            HoverFill = expanded ? ColorF.Transparent : Tok.FillSubtleSecondary,
            PressedFill = expanded ? ColorF.Transparent : Tok.FillSubtleTertiary,
            BrushTransitionMs = WaveeMotion.Fast,
            Role = AutomationRole.Button, Focusable = !expanded, Cursor = expanded ? null : CursorId.Hand,
            OnRealized = h => hostNode.Value = h,
            OnClick = expanded ? null : Open,
            Children = [glyph, layer],
        };
    }
}
