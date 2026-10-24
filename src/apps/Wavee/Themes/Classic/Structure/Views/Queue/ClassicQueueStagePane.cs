// ── Themes/Classic/Structure/Views/Queue/ClassicQueueStagePane.cs — Classic's `Queue.StagePane` (themes plan 6.1, T7d) ─────────────
//
// From `Shell/Stage.UI.cs` (the column: "Up next" and the Autoplay switch over a scroller) and `Entities/Queue.UI.cs` (the rows). The core host
// (`Queue.StagePaneHost`) keeps the reads, the split, the ONE page counter of the continuous list, the realized slots, the reorder lane and
// the verbs; this view draws the column in the stage's ink.
//
//   column  [ "Up next" · Autoplay ToggleSwitch ] · ScrollEl[ lane[ headers · Next row · rows · Show more ] | "Nothing up next" ]
//
// Every metric scales by `Stage.Layout.QueueScale` (1 on the board, 1.25 on a 2560×1080 ultrawide) and is rounded as rendered, because the
// lane's drag maths needs each slot's height EXACT. The first upcoming song is the NEXT row (larger art, an eyebrow, display type); each section
// opens with a caption and its count; a row shows its position, which turns into the grip on hover while the list owns the drag; an end-aligned
// duration, a hover-revealed ✕, glass instead of the row-hover plate, autoplay at 0.68. The stage's accent, scale and width are the stage's
// ambient context (`Stage.StageContext`); the Autoplay switch's signal mirrors the queue's autoplay fact (a switch binds a signal).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>The stage's Up next column.</summary>
internal sealed class ClassicQueueStagePane : Component
{
    readonly Signal<bool> _autoplay = new(true);   // seeded once, mirrored from the fact by the effect — never a new signal per render
    Func<QueueSlot, float>? _extentOf;
    Action<bool>? _setAutoplay;
    QueueProps _queue = null!;
    float _s = 1f;

    /// <summary>The first row of the whole upcoming list is the Next row: its extent includes the gap under it.</summary>
    bool IsNextSlot(QueueSlot slot) => slot.IsRow && _queue.TryRow(slot.Section, slot.Pos, out var row) && row.Index == 0;

    float ExtentOf(QueueSlot slot) => slot.Kind switch
    {
        QueueSlotKind.More => Px(ClassicQueueRules.MoreRowH) + Px(Spacing.XS) + Px(Spacing.XXS),   // = ShowMore's rendered height + margins (each term rounded as rendered)
        QueueSlotKind.Header => ClassicQueueRules.StageHeaderH * _s,
        _ => IsNextSlot(slot) ? (ClassicQueueRules.StageNextH + ClassicQueueRules.StageNextGap) * _s : ClassicQueueRules.StageRowH * _s,
    };

    float Px(float v) => MathF.Round(v * _s);

    public override Element Render()
    {
        var props = UseProps<QueueStagePaneProps>();
        var lane = props.Lane;
        _queue = props.Queue;
        _setAutoplay ??= props.Verbs.SetAutoplay;
        var ctx = UseContext(Stage.StageContext)!;
        var set = ctx.Accent.Value;
        bool autoplay = _queue.Autoplay.Enabled;
        UseEffect(() => _autoplay.SetIfChanged(autoplay), DepKey.From(autoplay));
        // the stage's own ink for the OFF state too: the stock Off* ramp is the app THEME's, and the stage's ground is not (V-U30)
        var toggle = ToggleSwitch.DefaultStyle with
        {
            OnFill = set.Fill, OnHover = set.FillSecondary, OnPressed = set.FillTertiary, OnKnob = set.Ink,
            OffFill = Ink.GlassRest, OffHover = Ink.GlassHover, OffPressed = Ink.GlassPressed, OffBorder = Ink.InkSecondary, OffKnob = Ink.InkSecondary,
            Foreground = Ink.Ink, MinWidth = ClassicQueueRules.SwitchMinW,
        };
        var layout = ctx.Layout.Value;
        // the Huge 4K stage's Up next column beside the lyrics (C-Stage-Huge4K) stands in its own frame: the board's 1x rows across that frame's
        // width, not the pane region's scale and list cap
        _s = props.Aside ? 1f : layout.QueueScale;
        lane.SetMetrics(ClassicQueueRules.StageRowH * _s, _extentOf ??= ExtentOf);
        bool art = !ClassicOptions.HideTrackArt();

        var kids = new List<Element>(2);
        if (lane.SlotCount > 0)
        {
            var slots = new Element[lane.SlotCount];
            for (int i = 0; i < slots.Length; i++)
            {
                int item = lane.ItemAt(i);
                var slot = lane.SlotAt(item);
                if (slot.Kind == QueueSlotKind.More) { slots[i] = ShowMore(props, slot.Section); continue; }
                if (slot.Kind == QueueSlotKind.Header) { slots[i] = StageHeader(slot.Section); continue; }
                slots[i] = _queue.TryRow(slot.Section, slot.Pos, out var row)
                    ? lane.Lane(item, row.Index == 0 ? NextRow(props, row, art) : Row(props, row, art), lane.RowKey("s", row.ItemId, row.Index))
                    : new BoxEl();
            }
            kids.Add(lane.List(new BoxEl { Key = "stageupcoming", Direction = 1, Children = slots }, "stagelane:upcoming"));
        }
        if (_queue.Empty)
            kids.Add(EmptySentence());
        Element body = new BoxEl { Direction = 1, MinHeight = 0f, Children = kids.ToArray() };
        Element rows = lane.SlotCount == 0 ? lane.List(body, "stagelane:empty") : body;

        float s = _s;
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true, MaxWidth = props.Aside ? float.NaN : layout.QueueListMaxW,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Margin = new Edges4(0f, 0f, 0f, Spacing.M),
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.UpNext)) { Size = MathF.Round(28f * s), LineHeight = MathF.Round(36f * s), Weight = 600, FontFamily = ClassicStageLayerGeometry.DisplayFace, Color = Ink.Ink },
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                            Children = [new TextEl(Loc.Get(Strings.Player.Autoplay)) { Size = MathF.Round(14f * s), LineHeight = MathF.Round(20f * s), Color = Ink.Ink },
                                        ToggleSwitch.Create(_autoplay, _setAutoplay, style: toggle)],
                        },
                    ],
                },
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = props.Aside ? "stagequeue:aside" : "stagequeue",
                    Content = rows,
                },
            ],
        };
    }

    /// <summary>"Nothing up next": the one sentence under an empty list.</summary>
    static Element EmptySentence() => new BoxEl
    {
        Padding = new Edges4(0f, Spacing.XXL, 0f, 0f),
        Children = [new TextEl(Loc.Get(Strings.Player.QueueEmpty)) { Size = ClassicQueueRules.EmptySize, LineHeight = ClassicQueueRules.EmptyLine, Color = Ink.InkTertiary }],
    };

    Element Row(QueueStagePaneProps props, in QueueRowProps row, bool art)
    {
        var lane = props.Lane;
        var verbs = props.Verbs;
        EntityRef r = row.Ref;
        QueueRowText text = lane.TextOf(r);
        ulong itemId = row.ItemId;
        int index = row.Index;
        bool viewer = _queue.Viewer;
        var kids = new List<Element>(5)
        {
            // The row's position; on hover it gives way to the grip (an affordance, not a control: the whole row is the
            // drag source, and a viewer never sees the grip).
            new BoxEl
            {
                Width = Px(ClassicQueueRules.StageGripW), Shrink = 0f, ZStack = true, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                        HoverOpacity = viewer ? 1f : 0f, HitTestVisible = false,
                        Children = [new TextEl(FormatCache.Int(index + 1)) { Size = Px(12f), LineHeight = Px(16f), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap }],
                    },
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                        Opacity = 0f, HoverOpacity = viewer ? 0f : 1f, HitTestVisible = false,
                        Children = [new TextEl(Icons.GripperBar) { Size = Px(14f), FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkTertiary }],
                    },
                ],
            },
        };
        if (art) kids.Add(ArtTile(text.Art, text.Uri, () => verbs.Play(itemId, index, r), Px(ClassicQueueRules.StageRowArt), Px(26f), (int)Px(96f)));
        kids.Add(TitleBlock(text, Px(14f), Px(20f), 600, null, Px(12f), Px(16f)));
        // Tabular by geometry: a fixed end-aligned slot, so every colon lands on the same x.
        kids.Add(new BoxEl
        {
            Width = Px(ClassicQueueRules.StageTimeW), Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Children = [new TextEl(text.DurationMs > 0 ? lane.Clock(text.DurationMs) : "") { Size = Px(12f), LineHeight = Px(16f), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap }],
        });
        kids.Add(RemoveSlot(verbs, row));

        var el = new BoxEl
        {
            Key = lane.RowKey("s", itemId, index) + ":art=" + art + ":s=" + Px(100f),
            Draggable = lane.OwnDrag(itemId, index, r),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.M), MinHeight = Px(ClassicQueueRules.StageRowH), Height = Px(ClassicQueueRules.StageRowH),
            Padding = new Edges4(Px(Spacing.S), 0f, Px(Spacing.S), 0f), Corners = Radii.ControlAll,
            Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
            PressScale = R.Motion.ScaleSubtle.Press,
            Opacity = row.Section == QueueSection.Autoplay ? ClassicQueueRules.StageAutoplayDim : 1f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => verbs.Play(itemId, index, r),
            Enter = ClassicQueue.RowEnter,
            Exit = ClassicQueue.RowExit,
            Layout = LayoutTransition.Slide,
            Children = kids.ToArray(),
        };
        return lane.Attach(el, itemId, index, r);
    }

    /// <summary>The first upcoming song, set apart: larger art, a "Next" eyebrow, the title in display type, on a quiet
    /// plate. Same verbs as any row (click skips to it, drag reorders it, ✕ removes it).</summary>
    Element NextRow(QueueStagePaneProps props, in QueueRowProps row, bool art)
    {
        var lane = props.Lane;
        var verbs = props.Verbs;
        EntityRef r = row.Ref;
        QueueRowText text = lane.TextOf(r);
        ulong itemId = row.ItemId;
        int index = row.Index;
        var kids = new List<Element>(4);
        if (art) kids.Add(ArtTile(text.Art, text.Uri, () => verbs.Play(itemId, index, r), Px(ClassicQueueRules.StageNextArt), Px(32f), (int)Px(192f)));
        kids.Add(TitleBlock(text, Px(22f), Px(28f), 600, Loc.Get(Strings.Stage.NextTrack), Px(14f), Px(20f)));
        kids.Add(new BoxEl
        {
            Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Children = [new TextEl(text.DurationMs > 0 ? lane.Clock(text.DurationMs) : "") { Size = Px(14f), LineHeight = Px(20f), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap }],
        });
        kids.Add(RemoveSlot(verbs, row));

        var el = new BoxEl
        {
            Key = lane.RowKey("s", itemId, index) + ":next:art=" + art + ":s=" + Px(100f),
            Draggable = lane.OwnDrag(itemId, index, r),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.L), Height = Px(ClassicQueueRules.StageNextH),
            Margin = new Edges4(0f, 0f, 0f, Px(ClassicQueueRules.StageNextGap)),
            Padding = new Edges4(Px(Spacing.M), 0f, Px(Spacing.S), 0f), Corners = Radii.CardAll,
            Fill = Ink.Plate, HoverFill = Ink.PlateHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
            BorderWidth = 1f, BorderColor = Ink.Stroke,
            PressScale = R.Motion.ScaleSubtle.Press,
            Opacity = row.Section == QueueSection.Autoplay ? ClassicQueueRules.StageAutoplayDim : 1f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => verbs.Play(itemId, index, r),
            Enter = ClassicQueue.RowEnter,
            Exit = ClassicQueue.RowExit,
            Layout = LayoutTransition.Slide,
            Children = kids.ToArray(),
        };
        return lane.Attach(el, itemId, index, r);
    }

    /// <summary>Title over artists (an explicit badge before the artists when the track is explicit), with an optional
    /// eyebrow above. A THIN row (title unknown) keeps the slot empty.</summary>
    static Element TitleBlock(QueueRowText text, float titleSize, float titleLine, ushort titleWeight, string? eyebrow, float subSize, float subLine)
    {
        if (text.Thin) return new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f };
        var lines = new List<Element>(3);
        if (eyebrow is not null)
            lines.Add(R.Type.Eyebrow(eyebrow) with { Size = MathF.Round(subSize * 0.86f), Color = Ink.InkSecondary, MaxLines = 1 });
        lines.Add(new TextEl(text.Title)
        {
            Size = titleSize, LineHeight = titleLine, Weight = titleWeight, FontFamily = eyebrow is null ? null : R.Type.DisplayFace,
            Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        });
        Element artists = new TextEl(text.Artists) { Size = subSize, LineHeight = subLine, Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        lines.Add(text.Explicit
            ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                          Children = [Controls.ExplicitBadge(MathF.Round(subLine * 0.8f), Ink.InkTertiary), artists] }
            : artists);
        return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f, Children = lines.ToArray() };
    }

    /// <summary>A cover with the now-playing overlay's play FAB centred on it.</summary>
    static Element ArtTile(string? url, string uri, Action play, float edge, float fab, int decode) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = Radii.ControlAll,
        Children = [Controls.Artwork(url, edge, edge, Radii.Control, decodePx: decode), Controls.NowPlayingOverlay(uri, play, fab, centred: true)],
    };

    Element RemoveSlot(IQueueVerbs verbs, in QueueRowProps row)
    {
        ulong itemId = row.ItemId;
        int index = row.Index;
        EntityRef r = row.Ref;
        return row.Removable
            ? new BoxEl { Opacity = 0f, HoverOpacity = 1f, Shrink = 0f, BlocksDragArm = true, Children = [StageGlyph(Icons.ChromeClose, () => verbs.Remove(itemId, index, r))] }
            : new BoxEl { Width = Controls.IconButtonSize, Shrink = 0f };
    }

    /// <summary>A section caption in the stage's ink: the section's name and its count (the rail's grammar, on media).</summary>
    Element StageHeader(QueueSection section)
    {
        string title = ClassicQueue.SectionTitle(section);
        return new BoxEl
        {
            Key = "stagehdr:" + QueueSections.Tag(section),
            Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.S, Height = Px(ClassicQueueRules.StageHeaderH),
            Padding = new Edges4(Px(Spacing.S), 0f, Px(Spacing.S), Px(Spacing.XS)),
            Layout = LayoutTransition.Slide,
            BlocksDragArm = true,                                              // a slot, never a handle
            Children =
            [
                R.Type.Eyebrow(title) with { Size = Px(12f), Color = Ink.InkTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new TextEl(FormatCache.Int(_queue.Counts.Of(section))) { Size = Px(12f), LineHeight = Px(16f), Weight = 600, Color = Ink.InkTertiary },
            ],
        };
    }

    Element ShowMore(QueueStagePaneProps props, QueueSection section)
    {
        int remaining = props.Lane.Remaining(section);
        var verbs = props.Verbs;
        return new BoxEl
        {
            Key = "stagemore:" + QueueSections.Tag(section),
            // scaled like every other stage slot: height + margins sum to ExtentOf's More extent (the virtualizer's offsets)
            Direction = 0, Height = Px(ClassicQueueRules.MoreRowH), Gap = Px(Spacing.S), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            BlocksDragArm = true, Margin = new Edges4(0f, Px(Spacing.XS), 0f, Px(Spacing.XXS)), Corners = Radii.ControlAll,
            Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = () => verbs.ShowMore(section),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new TextEl(Icons.ChevronDown) { Size = Px(12f), FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
                new TextEl("·  " + remaining) { Size = Px(12f), LineHeight = Px(16f), Color = Ink.InkTertiary },
            ],
        };
    }

    static BoxEl StageGlyph(string glyph, Action onClick) => new()
    {
        Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll, Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = R.Motion.ScaleEmphatic.Hover, PressScale = R.Motion.ScaleEmphatic.Press,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(glyph) { Size = ClassicQueueRules.GlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary, HoverColor = Ink.Ink }],
    };
}
