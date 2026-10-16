// ── Themes/Common/CommonDiagnostics.cs — the Common `Pages.Diagnostics.*` blocks (themes plan 6.5, T11c; P15 hero tones → roles) ───────
//
// From `Screens\Diagnostics.UI.cs` (the shared diagnostics chrome: ONE copy — 0.2.9 had it twice — and the two pages) and
// `Diagnostics.Capture.UI.cs`: the playback runtime page, the Connect page and the realtime capture page. One copy of each view for every
// theme (Classic registers them too). A page is a pinned header over ONE scroll keyed on the route (ch 27 W28: nothing compacts, nothing is
// sticky) over the DOCUMENT the core builds (`DiagnosticsPageProps.Build`, run inside this render so the signals it reads subscribe the page).
//
// THE RULE THIS FILE KEEPS: it lays out and binds, it never decides. What a fact is and how it reads ("—" for an unknown one, the number
// formats, the stamps) is the core builder's; the cards, rows, chips and buttons are drawn here. A status glyph's tone is a `DiagTone` mapped
// to the system fills here (P15: the hero tone is a role, not a literal at the call site).
//
// TIERS (themes plan 3.2, TC10): the pages read `ctx.Tier()` into a `CommonDiagnosticsLayout` (Rules\Tiers.cs) and pass it down in the `Env`; a card component reads
// the same fact through `CommonTierComponent`. Compact stacks a label over its value, a table row into stacked lines and a control row into a wrapping one, and every
// button, link and field is a 40-DIP target; the scroll column is capped on every tier (the Huge fill rule: the column stops at 1000 and keeps the page's left edge).
//
// NOTHING HERE READS A CLOCK PER FRAME (ch 27 N12). No surface carries a context menu, a drag or a shortcut (ch 27 §6).
//
// PROPS FREEZE AT MOUNT: every control whose interesting fields freeze is REMOUNTED through a `Key` that changes exactly when its frozen input
// does, and every toggle that triggers such a remount runs through `UsePost` so the click finishes before its node is torn down.
// The stateful widgets (the health card, the wire check, the scroll probe, the tiles card, the evidence card) are components in
// `CommonDiagnostics.Widgets.cs`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The Common diagnostics pages.</summary>
internal static partial class CommonDiagnostics
{
    /// <summary>Registers the three diagnostics pages (one template, three keys: the page frame is the same, the document differs).</summary>
    public static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.DiagnosticsPlayback, Template, "common.diagnostics.playback");
        views.Add(ViewKeys.DiagnosticsConnect, Template, "common.diagnostics.connect");
        views.Add(ViewKeys.DiagnosticsCapture, Template, "common.diagnostics.capture");
    }

    /// <summary>What a drawn node may reach: the page's refresh and expanded-root signals, the clipboard, and (inside the tiles card) the
    /// pass-timing switch.</summary>
    internal sealed class Env(Signal<int>? refresh, Signal<long>? expanded, InputHooks? input, Func<DiagToggle, Element>? toggle, CommonDiagnosticsLayout layout)
    {
        public readonly Signal<int>? Refresh = refresh;
        public readonly Signal<long>? Expanded = expanded;
        public readonly InputHooks? Input = input;
        public readonly Func<DiagToggle, Element>? Toggle = toggle;

        /// <summary>The tier layout the page (or the card's own read) resolved.</summary>
        public readonly CommonDiagnosticsLayout Layout = layout;

        /// <summary>For a widget card that has no page signals.</summary>
        public static Env Of(CommonDiagnosticsLayout layout) => new(null, null, null, null, layout);
    }

    /// <summary>The registered template of the three keys.</summary>
    public static Element Template(DiagnosticsPageProps props, in ViewContext ctx)
    {
        var h = ctx.Hooks;
        var refresh = h.UseSignal(0);
        var expanded = h.UseSignal(-1L);
        var input = h.UseContext(InputHooks.Current);
        _ = refresh.Value;
        long open = expanded.Value;

        // The live poll: enabled only while the page's own gate holds — off, nothing is being written to the store,
        // so polling would just repaint the same empty snapshot every tick for no reason.
        bool poll = props.PollWhile?.Value ?? false;
        h.UseInterval(() => refresh.Value = refresh.Peek() + 1, props.PollMs, enabled: poll);

        var layout = CommonDiagnosticsLayout.For(ctx.Tier());
        var env = new Env(refresh, expanded, input, null, layout);
        var body = new List<Element>(10);
        foreach (var node in props.Build(open))
            Draw(node, env, body);
        return PageFrame(props.Glyph, props.Title, props.ScrollKey, body, layout);
    }

    // ══ THE SHARED DIAGNOSTICS CHROME (ONE copy — 0.2.9 had it twice) ════════════════════════════════════════════════

    static Element PageHeader(string glyph, string title) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.M),
        Children =
        [
            FluentGpu.Dsl.Ui.Icon(glyph, CommonDiagnosticsRules.HeaderGlyphSize, Tok.TextPrimary),
            R.Type.PageHero(title) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = CommonDiagnosticsRules.HeaderTitleLines, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    /// <summary>The pinned header over ONE scroll keyed on the route (ch 27 W28: nothing compacts, nothing is sticky). A scroll's content fills its viewport and
    /// ignores its own max width, so the capped column is the one child of a row that is the content: it grows to the tier's max width and keeps the left edge.</summary>
    static Element PageFrame(string glyph, string title, string scrollKey, List<Element> body, CommonDiagnosticsLayout layout) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children =
        [
            PageHeader(glyph, title),
            FluentGpu.Dsl.Ui.ScrollView(new BoxEl
            {
                Direction = 0, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = CommonDiagnosticsRules.PageGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = layout.MaxWidth,
                        Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.XXL), Children = body.ToArray(),
                    },
                ],
            }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = scrollKey },
        ],
    };

    internal static Element Card(string title, List<Element> rows)
    {
        rows.Insert(0, new TextEl(title) { Size = CommonDiagnosticsRules.TextSize, Weight = 600, Color = Tok.TextSecondary });
        return new BoxEl
        {
            Direction = 1, Gap = CommonDiagnosticsRules.CardGap, Padding = Edges4.All(CommonDiagnosticsRules.CardPadding), Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = rows.ToArray(),
        };
    }

    internal static Element Card(string title, params Element[] rows) => Card(title, new List<Element>(rows));

    /// <summary>Label + value (a blank value is already a dash: ch 27 W23 — an unknown fact is a dash, never an empty column).</summary>
    internal static Element Row(string label, string value, float labelWidth, bool stacked) => stacked
        ? new BoxEl
        {
            Direction = 1, Gap = CommonDiagnosticsRules.StackedRowGap,
            Children =
            [
                new TextEl(label) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary },
                new TextEl(value) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f },
            ],
        }
        : new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start,
            Children =
            [
                new TextEl(label) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
                new TextEl(value) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Grow = 1f, Shrink = 1f, MinWidth = 0f },
            ],
        };

    /// <summary>The presence chip: text is TextPrimary in both states; only the background carries the severity.</summary>
    static Element Chip(DiagChip chip) => new BoxEl
    {
        Padding = new Edges4(CommonDiagnosticsRules.ChipSideX, CommonDiagnosticsRules.ChipSideY, CommonDiagnosticsRules.ChipSideX, CommonDiagnosticsRules.ChipSideY),
        Corners = CornerRadius4.All(Radii.Control),
        Fill = chip.Present ? Tok.SystemFillSuccessBackground : Tok.SystemFillCriticalBackground,
        Children = [R.Type.MicroMeta((chip.Present ? "✓ " : "✕ ") + chip.Label) with { Color = Tok.TextPrimary }],
    };

    internal static TextEl Body(string text) => new(text) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap };

    internal static TextEl Caption(string text) => R.Type.MicroMeta(text) with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap };

    static BoxEl Separator(DiagGap gap) => new()
    {
        Height = 1f, Fill = Tok.StrokeCardDefault,
        Margin = new Edges4(0f, gap switch
        {
            DiagGap.Hairline => CommonDiagnosticsRules.GapHairline,
            DiagGap.Tight => CommonDiagnosticsRules.GapTight,
            DiagGap.Wide => CommonDiagnosticsRules.GapWide,
            _ => CommonDiagnosticsRules.GapNormal,
        }, 0f, CommonDiagnosticsRules.SeparatorBelow),
    };

    /// <summary>The status glyph's ink by tone: the system fills (muted is the tertiary text ink).</summary>
    static ColorF ToneInk(DiagTone tone) => tone switch
    {
        DiagTone.Attention => Tok.SystemFillAttention,
        DiagTone.Success => Tok.SystemFillSuccess,
        DiagTone.Critical => Tok.SystemFillCritical,
        DiagTone.Caution => Tok.SystemFillCaution,
        _ => Tok.TextTertiary,
    };

    static InfoBadgeSeverity ToneBadge(DiagTone tone) => tone switch
    {
        DiagTone.Critical => InfoBadgeSeverity.Critical,
        DiagTone.Caution => InfoBadgeSeverity.Caution,
        _ => InfoBadgeSeverity.Success,
    };

    /// <summary>A coloured status glyph + heading, then wrapped body copy (the setup card's inline block: one feature).</summary>
    static Element Status(DiagStatus s) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start, Padding = Edges4.All(CommonDiagnosticsRules.CardPadding), Fill = Tok.FillLayerAlt,
        Corners = CornerRadius4.All(Radii.Control), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(s.Glyph) { Size = CommonDiagnosticsRules.StatusGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = ToneInk(s.Tone), Shrink = 0f, Margin = new Edges4(0f, 1f, 0f, 0f) },
            new BoxEl
            {
                Direction = 1, Gap = CommonDiagnosticsRules.StatusGap, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    R.Type.TrackTitle(s.Heading) with { Wrap = TextWrap.Wrap },
                    R.Type.DenseMeta(s.Body) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
        ],
    };

    // ══ THE NODES ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Draws one node into <paramref name="into"/> (a capture root draws two elements: its row and, when open, its tree).</summary>
    internal static void Draw(DiagNode node, Env env, List<Element> into)
    {
        switch (node)
        {
            case DiagStatus s:
                into.Add(Status(s));
                break;
            case DiagCard c:
                into.Add(CardOf(c, env));
                break;
            case DiagRow r:
                into.Add(Row(r.Label, r.Value, r.Wide ? CommonDiagnosticsRules.ConnectLabelWidth : CommonDiagnosticsRules.RuntimeLabelWidth, env.Layout.Stacked));
                break;
            case DiagBody b:
                into.Add(Body(b.Text));
                break;
            case DiagNote n:
                into.Add(Caption(n.Text));
                break;
            case DiagSeparator sep:
                into.Add(Separator(sep.Gap));
                break;
            case DiagHeading h:
                into.Add(new TextEl(h.Text) { Size = CommonDiagnosticsRules.TextSize, Weight = 600, Color = h.Primary ? Tok.TextPrimary : Tok.TextSecondary });
                break;
            case DiagLine l:
                into.Add(Line(l));
                break;
            case DiagGroup g:
                {
                    var kids = new List<Element>(g.Items.Count);
                    foreach (var item in g.Items) Draw(item, env, kids);
                    into.Add(new BoxEl { Direction = 1, Gap = CommonDiagnosticsRules.ReasonsGap, Children = kids.ToArray() });
                    break;
                }
            case DiagPassRow pass:
                into.Add(PassRow(pass, env.Layout));
                break;
            case DiagNamed named:
                into.Add(Named(named, env.Layout));
                break;
            case DiagButtons buttons:
                into.Add(Buttons(buttons, env));
                break;
            case DiagLink link:
                into.Add(new BoxEl
                {
                    Direction = 1, Gap = CommonDiagnosticsRules.LinkGap, Margin = new Edges4(0f, CommonDiagnosticsRules.LinkTop, 0f, 0f),
                    Children = [CommonTouch.Link(link.Label, link.Click, env.Layout.Target), Caption(link.Hint)],
                });
                break;
            case DiagAnomaly a:
                into.Add(AnomalyRow(a, env.Layout));
                break;
            case DiagRoot root:
                into.Add(RootRow(root, env));
                if (root.Tree is { } tree) into.Add(TreeView(tree, root.EmptyTree, env.Layout));
                break;
            case DiagToggle toggle:
                if (env.Toggle is { } draw) into.Add(draw(toggle));
                break;
            case DiagHealth health:
                into.Add(Embed.Comp(health, static () => new HealthCard()));
                break;
            case DiagWireCheck wire:
                into.Add(Embed.Comp(wire, static () => new WireCheckCard()));
                break;
            case DiagScroll scroll:
                into.Add(Embed.Comp(scroll, static () => new ScrollCard()) with { Key = "scroll-card" });
                break;
            case DiagTiles tiles:
                into.Add(Embed.Comp(tiles, static () => new TileCard()) with { Key = "tiles-card" });
                break;
            case DiagEvidence evidence:
                into.Add(Embed.Comp(evidence, static () => new EvidenceCard()) with { Key = "evidence-card" });
                break;
        }
    }

    internal static Element CardOf(DiagCard card, Env env)
    {
        var rows = new List<Element>(card.Items.Count + 1);
        foreach (var item in card.Items) Draw(item, env, rows);
        return Card(card.Title, rows);
    }

    static Element Line(DiagLine l)
    {
        var text = new TextEl(l.Text) { Size = CommonDiagnosticsRules.TextSize, Color = l.Primary ? Tok.TextPrimary : Tok.TextSecondary };
        return l.Wrap ? text with { Wrap = TextWrap.Wrap } : text;
    }

    static Element PassRow(DiagPassRow p, CommonDiagnosticsLayout layout) => layout.Stacked
        ? new BoxEl
        {
            Direction = 1, Gap = CommonDiagnosticsRules.StackedRowGap,
            Children =
            [
                new TextEl(p.Kind) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                new TextEl(p.Target + "  " + p.Ms) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap, MinWidth = 0f },
            ],
        }
        : new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(p.Kind) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextPrimary, Width = CommonDiagnosticsRules.PassKindWidth, Shrink = 0f },
            new TextEl(p.Target) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = CommonDiagnosticsRules.PassTargetWidth, Shrink = 0f },
            new TextEl(p.Ms) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
        ],
    };

    static Element Named(DiagNamed named, CommonDiagnosticsLayout layout)
    {
        var kids = new List<Element>(named.Chips.Count + 1)
        {
            new TextEl(named.Name) { Size = CommonDiagnosticsRules.TextSize, Weight = 600, Color = Tok.TextPrimary },
        };
        foreach (var chip in named.Chips) kids.Add(Chip(chip));
        return new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = layout.Stacked, Children = kids.ToArray() };
    }

    static Element Buttons(DiagButtons row, Env env)
    {
        var kids = new Element[row.Buttons.Count];
        for (int i = 0; i < kids.Length; i++)
        {
            var b = row.Buttons[i];
            Action? click = b.Click;
            if (b.Refresh && env.Refresh is { } refresh) click = () => refresh.Value = refresh.Peek() + 1;
            else if (b.CopyText is { } copy)
            {
                var input = env.Input;
                var after = b.Click;
                click = () =>
                {
                    input?.Clipboard?.SetText(copy());
                    after?.Invoke();
                };
            }
            click ??= (static () => { });
            kids[i] = CommonTouch.Button(b.Label, click, env.Layout.Target, b.Accent);
        }
        return new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = env.Layout.Stacked, Children = kids };
    }

    // ── the capture page's anomalies and roots ──────────────────────────────────────────────────────────────────

    static Element AnomalyRow(DiagAnomaly a, CommonDiagnosticsLayout layout) => layout.Stacked
        ? new BoxEl
        {
            Direction = 1, Gap = CommonDiagnosticsRules.StackedRowGap,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        InfoBadge.Dot(InfoBadgeSeverity.Critical),
                        new TextEl(a.Kind) { Size = CommonDiagnosticsRules.TextSize, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(a.RootId) { Size = CommonDiagnosticsRules.MonoSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Shrink = 0f },
                    ],
                },
                new TextEl(a.Summary) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
            ],
        }
        : new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            InfoBadge.Dot(InfoBadgeSeverity.Critical),
            new TextEl(a.Kind) { Size = CommonDiagnosticsRules.TextSize, Weight = 600, Color = Tok.TextPrimary, Width = CommonDiagnosticsRules.AnomalyKindWidth, Shrink = 0f },
            new TextEl(a.Summary) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
            new TextEl(a.RootId) { Size = CommonDiagnosticsRules.MonoSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Shrink = 0f },
        ],
    };

    static Element RootRow(DiagRoot r, Env env)
    {
        bool expanded = r.Expanded;
        long rootId = r.RootId;
        var open = env.Expanded;
        var toggle = CommonTouch.Link(r.ToggleLabel, () => { if (open is not null) open.Value = expanded ? -1 : rootId; }, env.Layout.Target);
        if (env.Layout.Stacked)
            return new BoxEl
            {
                Direction = 1, Gap = CommonDiagnosticsRules.StackedRowGap,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            InfoBadge.Dot(ToneBadge(r.Tone)),
                            new TextEl(r.Time) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Grow = 1f, Shrink = 1f, MinWidth = 0f },
                            new TextEl(r.EffectCount) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextTertiary, Shrink = 0f },
                            toggle,
                        ],
                    },
                    new TextEl(r.Label) { Size = CommonDiagnosticsRules.TextSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                ],
            };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                InfoBadge.Dot(ToneBadge(r.Tone)),
                new TextEl(r.Time) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = CommonDiagnosticsRules.RootTimeWidth, Shrink = 0f },
                new TextEl(r.Label) { Size = CommonDiagnosticsRules.TextSize, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
                new TextEl(r.EffectCount) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextTertiary, Shrink = 0f },
                toggle,
            ],
        };
    }

    /// <summary>The expanded tree, indented under its own root row — input → decisions → requests → echoes →
    /// UI outcomes → errors, in causal + time order (§2.5). Header fields only; no payload is ever read here.</summary>
    static Element TreeView(IReadOnlyList<DiagTreeRow> tree, string emptyText, CommonDiagnosticsLayout layout)
    {
        if (tree.Count == 0) return new BoxEl
        {
            Padding = new Edges4(CommonDiagnosticsRules.TreeInsetLeft, CommonDiagnosticsRules.TreeInsetTop, 0f, CommonDiagnosticsRules.TreeEmptyBottom),
            Children = [Body(emptyText)],
        };

        var rows = new Element[tree.Count];
        for (int i = 0; i < tree.Count; i++) rows[i] = TreeRow(tree[i], layout);
        return new BoxEl
        {
            Direction = 1, Gap = CommonDiagnosticsRules.TreeGap,
            Padding = new Edges4(CommonDiagnosticsRules.TreeInsetLeft, CommonDiagnosticsRules.TreeInsetTop, 0f, CommonDiagnosticsRules.TreeInsetBottom), Children = rows,
        };
    }

    static Element TreeRow(DiagTreeRow row, CommonDiagnosticsLayout layout) => layout.Stacked
        ? new BoxEl
        {
            Direction = 1, Gap = CommonDiagnosticsRules.StackedRowGap, Margin = new Edges4(row.Depth * layout.TreeStep, 0f, 0f, 0f),
            Children =
            [
                new TextEl(row.Label) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                new TextEl(row.Time) { Size = CommonDiagnosticsRules.MonoSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
            ],
        }
        : new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Margin = new Edges4(row.Depth * layout.TreeStep, 0f, 0f, 0f),
        Children =
        [
            new TextEl("└─") { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Shrink = 0f },
            new TextEl(row.Time) { Size = CommonDiagnosticsRules.MonoSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Width = CommonDiagnosticsRules.TreeTimeWidth, Shrink = 0f },
            new TextEl(row.Label) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
        ],
    };
}
