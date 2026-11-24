// ── Themes/Common/CommonActivity.cs — the library activity chip and its flyout (#240 UX S1, S2; board boards240/Activity) ───────────────
//
// One picture for WinUI and Classic (Classic uses the WinUI layout); Zune keeps its own chip in the band (`ZuneBandActivity`) and opens the same
// flyout. The words, states and verbs are the core's (`LibraryActivity`); this only draws them. Numbers: `Rules\CommonActivityRules.cs`.
//
//   LANE     under the "Local files" row (`nav-activity-lane`): 16 DIP in the docked pane, 40 in the overlay drawer (the Compact target), reserved
//            in every state (no music folder included) and at every tier of its pane, so nothing in the pane ever moves for the chip (a row of
//            the pane's virtualized list cannot ease its height: the rows below would snap). The rail draws none (its rows are re-planned).
//   CHIP     (`nav-activity`) 180 x 20 at the label's x, risen 7 into the row's foot (the board's 56-DIP cell): a mark (a ring while a scan
//            runs, a tick after a background change, the amber dot when something needs the user) and one line. Idle or no music folder: no
//            chip (it fades out; its place stays). A click opens the flyout, a second closes it.
//   FLYOUT   (`activity-flyout`) the popup's own chrome, 420 wide (narrower in a narrow window): the head, the phases, "What changed since you
//            opened Wavee" (each count a link to Local Files), "Needs attention" (one line and one verb each), the footer (the last full scan,
//            "Rescan now", "Settings"). The phases and the sections reveal when they change size while it is open.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Roles;
using Rules = Wavee.Themes.Common.CommonActivityRules;

namespace Wavee.Themes.Common;

/// <summary>What every activity chip shares: the state it reads (with the "Updated" window), the flyout it opens and the mark it draws.</summary>
internal abstract class CommonActivityChipBase : CommonTierComponent
{
    NodeHandle _anchor = NodeHandle.Null;
    OverlayHandle? _flyout;
    IOverlayService? _overlay;
    Action<NodeHandle>? _realize;
    Action? _toggle;

    /// <summary>The flyout's side of the chip.</summary>
    protected abstract FlyoutPlacement Placement { get; }

    /// <summary>The chip's realize callback (its node anchors the flyout) and its click (open, or close the open one).</summary>
    protected Action<NodeHandle> Realize => _realize ??= h => _anchor = h;
    protected Action Toggle => _toggle ??= ToggleFlyout;

    /// <summary>The chip now. Call once per render, unconditionally (it owns hooks): it follows the library's two signals, and after a restate
    /// that brought news it says "Updated" for <see cref="LibraryActivity.UpdatedMs"/>. A restate seen at mount is never announced.</summary>
    protected LibraryActivity.ChipState UseChip()
    {
        _overlay = UseContext(Overlay.Service);
        var announcing = UseSignal(false);
        var announced = UseSignal(0);
        var seen = UseRef(-1);
        UseSignalEffect(() =>
        {
            int version = LibraryActivity.Restated();
            if (seen.Value == version) return;
            bool first = seen.Value < 0;
            seen.Value = version;
            if (first || !LibraryActivity.HasNews) return;
            announcing.Value = true;
            announced.Value = announced.Peek() + 1;
        });
        int round = announced.Value;
        UseTimeout(() => announcing.Value = false, LibraryActivity.UpdatedMs, DepKey.From(round));
        var chip = UseComputed(() =>
        {
            LibraryActivity.Track();
            return LibraryActivity.Chip(announcing.Value);
        });
        return chip.Value;
    }

    void ToggleFlyout()
    {
        if (_flyout is { IsOpen: true } open) { open.Close(); return; }
        if (_overlay is not { } overlay) return;
        OverlayHandle? opened = null;
        var props = new CommonActivityFlyout.Props(() => opened?.Close());
        opened = overlay.Open(() => _anchor, () => Embed.Comp(props, static () => new CommonActivityFlyout()), Placement,
            // A card of words and verbs, not a menu: the popup's own chrome (acrylic, stroke, corners, shadow), kept inside the window.
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
        _flyout = opened;
        opened.ClosedAction = () => { if (ReferenceEquals(_flyout, opened)) _flyout = null; };
    }

    /// <summary>The chip's mark: a ring while a scan runs, a tick after a background change, the amber dot when something needs the user.</summary>
    internal static Element Mark(LibraryActivity.ChipKind kind) => kind switch
    {
        LibraryActivity.ChipKind.Scanning => ProgressRing.Indeterminate(size: Rules.ChipRing),
        LibraryActivity.ChipKind.Updated => Ui.Icon(Icons.Accept, Rules.ChipTick, Tok.AccentDefault),
        LibraryActivity.ChipKind.Paused => Ui.Icon(Icons.Pause, Rules.ChipTick, Tok.SystemFillCaution),
        _ => new BoxEl { Width = Rules.Dot, Height = Rules.Dot, Shrink = 0f, Corners = Radii.FullAll, Fill = Tok.SystemFillCaution },
    };
}

/// <summary>The chip under "Local files" in the WinUI and Classic nav panes (see the file header).</summary>
internal sealed class CommonActivityChip : CommonActivityChipBase
{
    /// <summary>The row's label x (the chip stands on it), the slot's pane mode (the rail draws no lane) and whether the pane is the overlay
    /// drawer (its lane is the touch target's height at every tier the drawer opens in).</summary>
    public sealed record Props(float LabelX, bool Rail, bool Drawer);

    protected override FlyoutPlacement Placement => FlyoutPlacement.RightEdgeAlignedBottom;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var chip = UseChip();
        bool words = !p.Rail && chip.Kind is not (LibraryActivity.ChipKind.Hidden or LibraryActivity.ChipKind.Idle);
        bool drawer = p.Drawer;

        var kids = new List<Element>(1);
        if (words)
            kids.Add(new BoxEl
            {
                Key = Rules.ChipKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.ChipGap,
                Width = Rules.ChipWidth, Height = Rules.ChipHeight, Shrink = 1f, MinWidth = 0f,
                // Docked: it rises into the row's foot, as the board draws it. In the drawer it stands in the middle of its 40-DIP lane and
                // takes the whole lane as its hit rect (the Compact target), never reaching into the row above.
                Margin = new Edges4(0f, drawer ? 0f : -Rules.Lift, 0f, 0f),
                HitSlop = drawer ? new Edges4(0f, Rules.DrawerSlop, 0f, Rules.DrawerSlop) : default,
                Padding = new Edges4(Rules.ChipPadLead, 0f, Rules.ChipPadEnd, 0f),
                Corners = Radii.FullAll, Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = Toggle, OnRealized = Realize,
                Enter = Rules.Fade, Exit = Rules.Fade, Transition = Rules.FadeMotion,
                Children =
                [
                    Mark(chip.Kind),
                    Ui.Caption(chip.Text) with
                    {
                        Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    },
                ],
            });

        // The lane is RESERVED: the same height in every state, with or without a music folder and at every tier of its pane (a row of the
        // pane's virtualized list cannot ease its height without the rows below snapping: reserve, never resize).
        return new BoxEl
        {
            Key = Rules.LaneKey, Direction = 0, AlignItems = drawer ? FlexAlign.Center : FlexAlign.Start, MinWidth = 0f, Shrink = 0f,
            Height = p.Rail ? 0f : drawer ? Rules.DrawerLane : Rules.Lane,
            Padding = new Edges4(p.LabelX, 0f, Rules.ChipPadEnd, 0f),
            Children = kids.ToArray(),
        };
    }
}

/// <summary>The activity flyout: the head, the phases, what changed, what needs the user and the footer (see the file header).</summary>
internal sealed class CommonActivityFlyout : CommonTierComponent
{
    /// <summary>Closes the flyout (a verb that leaves it: a link, Review, Settings).</summary>
    public sealed record Props(Action Close);

    public override Element Render()
    {
        var p = UseProps<Props>();
        LibraryActivity.Track();
        // The last scan's age ("2 min ago") moves with the clock.
        var tick = UseSignal(0);
        UseInterval(() => tick.Value = tick.Peek() + 1, Rules.AgoTickMs);
        _ = tick.Value;
        float target = CommonTiers.Target(TierContext.WindowOf(ReadTier()));
        var viewport = UseContextSignal(Viewport.Size);
        var overlay = UseContext(Overlay.Service);
        float width = MathF.Max(0f, MathF.Min(Rules.Width, viewport.Value.Width - 2f * Rules.WindowEdge));
        Action close = p.Close;

        // The running phase's bar spans its column (the engine's bar needs a definite width: the flyout's less its padding and the mark's cell).
        float bar = MathF.Max(0f, width - 2f * Rules.Pad - Rules.Mark - Rules.MarkGap);
        var kids = new List<Element>(8) { Head(target), Phases(LibraryActivity.Phases(), bar) };
        var changes = LibraryActivity.Changes();
        if (changes.Length > 0) kids.Add(Section(Rules.ChangesKey, Changes(changes, target, close)));
        var attention = LibraryActivity.Attention();
        if (attention.Length > 0) kids.Add(Section(Rules.AttentionKey, Attention(attention, target, close, overlay)));
        kids.Add(Footer(target, close));
        return new BoxEl
        {
            Key = Rules.FlyoutKey, Direction = 1, Width = width, MinWidth = 0f, Shrink = 0f,
            Children = kids.ToArray(),
        };
    }

    /// <summary>"Music on this PC · Up to date", and while a scan runs or is held its Pause / Resume at the end (board 8). The verb's lane is kept
    /// at rest (empty), so a scan starting never moves the head's words.</summary>
    static Element Head(float target) => new BoxEl
    {
        Key = Rules.HeadKey, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = target,
        Padding = new Edges4(Rules.Pad, Rules.HeadTop, Rules.HeadEnd, Rules.HeadBottom),
        Children =
        [
            Ui.BodyStrong(LibraryActivity.Title) with { Shrink = 0f, MaxLines = 1, Case = R.Case.For(LabelKind.Heading) },
            // The state may take a second line beside the Pause verb on a narrow flyout ("Paused on battery saver"), never an ellipsis.
            Ui.Body(" · " + LibraryActivity.StateWord()) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.WrapWholeWords, MaxLines = 2, Trim = TextTrim.WordEllipsis, Shrink = 1f, MinWidth = 0f,
                Case = R.Case.For(LabelKind.Label),
            },
            Ui.Spacer(),
            LibraryActivity.CanPause
                ? CommonTouch.Button(LibraryActivity.PauseLabel, LibraryActivity.TogglePause, target,
                    glyph: LibraryActivity.PausedNow ? Icons.Play : Icons.Pause) with { Key = Rules.PauseKey, Shrink = 0f, Margin = new Edges4(Rules.MarkGap, 0f, 0f, 0f) }
                : new BoxEl { Key = Rules.PauseKey, Shrink = 0f },
        ],
    };

    /// <summary>The four phases (a reveal box: the running phase's bar and rate line come and go with the scan).</summary>
    static Element Phases(LibraryActivity.Phase[] phases, float bar)
    {
        var rows = new Element[phases.Length];
        for (int i = 0; i < phases.Length; i++) rows[i] = PhaseRow(phases[i], i, bar);
        return new BoxEl
        {
            Key = Rules.PhasesKey, Direction = 1, MinWidth = 0f, Padding = new Edges4(Rules.Pad, 0f, Rules.Pad, Rules.PhasesBottom),
            Animate = Design.Reveal.Resize, Children = rows,
        };
    }

    static Element PhaseRow(in LibraryActivity.Phase phase, int index, float bar)
    {
        bool pending = phase.Mark == LibraryActivity.PhaseMark.Pending;
        TextEl label = Ui.Body(phase.Label) with
        {
            Color = pending ? Tok.TextSecondary : Tok.TextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Case = R.Case.For(LabelKind.Label),
        };
        Element aside = Ui.Caption(phase.Aside) with { Shrink = 0f, MaxLines = 1 };
        if (phase.Mark != LibraryActivity.PhaseMark.Active)
            return new BoxEl
            {
                Key = Rules.PhaseKey + index, Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.MarkGap, Height = Rules.PhaseRow, MinWidth = 0f,
                Children = [PhaseMark(phase.Mark), label, aside],
            };

        // The running phase: its name over its bar and its rate line; the ring stands on the name's line.
        var column = new List<Element>(3)
        {
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.MarkGap, MinWidth = 0f, Children = [label with { Weight = Rules.HeadWeight }, aside] },
        };
        if (phase.Fraction >= 0f)
            column.Add(ProgressBar.Determinate(phase.Fraction, bar) with { Margin = new Edges4(0f, Rules.BarGap, 0f, 0f) });
        if (phase.Meta.Length > 0)
            column.Add(Ui.Caption(phase.Meta) with { Margin = new Edges4(0f, Rules.BarGap, 0f, 0f), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        return new BoxEl
        {
            Key = Rules.PhaseKey + index, Direction = 0, AlignItems = FlexAlign.Start, Gap = Rules.MarkGap, MinWidth = 0f,
            Padding = new Edges4(0f, Rules.ActiveTop, 0f, Rules.ActiveBottom),
            Children =
            [
                new BoxEl { Width = Rules.Mark, Height = Rules.ActiveLine, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [PhaseMark(phase.Mark)] },
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = column.ToArray() },
            ],
        };
    }

    /// <summary>A phase's mark in its 16-DIP cell: the tick (done), the ring (running), the hollow circle (not started).</summary>
    static Element PhaseMark(LibraryActivity.PhaseMark mark) => new BoxEl
    {
        Width = Rules.Mark, Height = Rules.Mark, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = mark switch
        {
            LibraryActivity.PhaseMark.Done => [Ui.Icon(Icons.Accept, Rules.Mark, Tok.AccentDefault)],
            LibraryActivity.PhaseMark.Active => [ProgressRing.Indeterminate(size: Rules.Mark)],
            _ => [new BoxEl { Width = Rules.Pending, Height = Rules.Pending, Corners = Radii.FullAll, BorderWidth = Rules.PendingStroke, BorderColor = Tok.TextTertiary }],
        },
    };

    /// <summary>A section under a hairline (a reveal box: it opens when its first item arrives while the flyout is open).</summary>
    static Element Section(string key, Element body) => new BoxEl
    {
        Key = key, Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer,
        Children = [Hairline(), body],
    };

    static Element Hairline() => new BoxEl { Height = Rules.Hairline, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault };

    static TextEl SectionHead(string text) => Ui.Caption(text) with
    {
        Weight = Rules.HeadWeight, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f, Case = R.Case.For(LabelKind.Heading),
    };

    /// <summary>"+46 added · 3 moved · 2 missing · 1 changed": each a link to Local Files (it has no filter by change yet).</summary>
    static Element Changes(LibraryActivity.ChangeLink[] links, float target, Action close)
    {
        var row = new List<Element>(links.Length * 2);
        for (int i = 0; i < links.Length; i++)
        {
            if (i > 0) row.Add(Ui.Caption("·") with { Color = Tok.TextSecondary, Shrink = 0f });
            row.Add(Link(Rules.ChangeKey + links[i].Id, links[i].Text, () => { close(); LibraryActivity.OpenLocalFiles(); }, target));
        }
        return new BoxEl
        {
            Direction = 1, Gap = Rules.SectionGap, MinWidth = 0f, Padding = new Edges4(Rules.Pad, Rules.SectionTop, Rules.Pad, Rules.SectionBottom),
            Children =
            [
                SectionHead(LibraryActivity.ChangedHead),
                new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Rules.LinkGap, MinWidth = 0f, Children = row.ToArray() },
            ],
        };
    }

    /// <summary>A link set as words (the board's: the accent ink, no plate, so the first one stands on the section's lead), lifted to the
    /// target's height at Compact.</summary>
    static Element Link(string key, string text, Action click, float target) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinHeight = target, MinWidth = target,
        Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = click,
        Children = [Ui.Body(text) with { Wrap = TextWrap.NoWrap, MaxLines = 1, Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary, PressedColor = Tok.AccentTextTertiary }],
    };

    /// <summary>"Needs attention": one line and one verb each (Retry rescans the folder and keeps the flyout open; Review and See which open Local Files).</summary>
    static Element Attention(LibraryActivity.AttentionLine[] lines, float target, Action close, IOverlayService? overlay)
    {
        var rows = new List<Element>(lines.Length + 1)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.LinkGap, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Width = Rules.Dot, Height = Rules.Dot, Shrink = 0f, Corners = Radii.FullAll, Fill = Tok.SystemFillCaution },
                    SectionHead(LibraryActivity.AttentionHead),
                ],
            },
        };
        foreach (var line in lines)
        {
            // Review opens the missing songs' dialog over the page (S2, S6); the other verbs act on their own.
            Action act = line.ActOver is { } over ? () => over(overlay) : line.Act;
            bool leaves = line.Mark == LibraryActivity.AttentionMark.Files;
            var words = new List<Element>(2)
            {
                Ui.Body(line.Text) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis, Shrink = 1f, MinWidth = 0f },
            };
            if (line.Sub.Length > 0) words.Add(Ui.Caption(line.Sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            rows.Add(new BoxEl
            {
                Key = Rules.AttentionItemKey + line.Id, Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.MarkGap, MinWidth = 0f,
                MinHeight = Rules.AttentionRow,
                Children =
                [
                    Ui.Icon(Icons.Warning, Rules.AttentionMark, Tok.SystemFillCaution),
                    new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = words.ToArray() },
                    CommonTouch.Button(line.Verb, () => { if (leaves) close(); act(); }, target) with { Key = Rules.VerbKey + line.Id, Shrink = 0f },
                ],
            });
        }
        return new BoxEl
        {
            Direction = 1, Gap = Rules.AttentionGap, MinWidth = 0f, Padding = new Edges4(Rules.Pad, Rules.SectionTop, Rules.Pad, Rules.AttentionBottom),
            Children = rows.ToArray(),
        };
    }

    /// <summary>"Last full scan 2 min ago · 1.8 s", "Rescan now" and "Settings" (Settings › Storage › Music on this PC; the insights page is
    /// another surface's). At Compact the line stands over the two buttons.</summary>
    static Element Footer(float target, Action close)
    {
        bool stack = target > 0f;
        TextEl line = Ui.Caption(LibraryActivity.LastScanLine()) with
        {
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = stack ? 0f : 1f, Shrink = 1f, Basis = stack ? float.NaN : 0f, MinWidth = 0f,
            Case = R.Case.For(LabelKind.Label),
        };
        var buttons = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.LinkGap, Shrink = 0f,
            Children =
            [
                CommonTouch.Button(LibraryActivity.RescanLabel, LibraryActivity.RescanAll, target, enabled: LibraryActivity.CanRescan) with { Key = Rules.RescanKey },
                CommonTouch.Button(LibraryActivity.SettingsLabel, () => { close(); LibraryActivity.OpenSettings(); }, target) with { Key = Rules.SettingsKey },
            ],
        };
        return new BoxEl
        {
            Key = Rules.FooterKey, Direction = 1, MinWidth = 0f,
            Children =
            [
                Hairline(),
                new BoxEl
                {
                    Direction = (byte)(stack ? 1 : 0), AlignItems = stack ? FlexAlign.Start : FlexAlign.Center, Gap = Rules.LinkGap, MinWidth = 0f,
                    Padding = new Edges4(Rules.Pad, Rules.FooterTop, Rules.Pad, Rules.FooterBottom),
                    Fill = Tok.FillCardSecondary,
                    Children = [line, buttons],
                },
            ],
        };
    }
}
