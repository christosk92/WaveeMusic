// ── Themes/Zune/Structure/Views/Stage/ZuneStageUpNext.cs — Zune's `Queue.StagePane` (P5 Z4c) ──────────────────────────────────────────────
//
// The stage's Up next column (Z-Queue-Page). The core host (`Queue.StagePaneHost`) keeps the reads, the section split, the ONE page counter
// of the continuous list, the realized slots, the reorder lane and the verbs; this view draws the column in the stage's ink:
//
//   column  [ "autoplay" word ] · ScrollEl[ lane[ section headings · rows · "show more" ] | "Nothing up next" ]
//
//   · a SECTION opens with its heading in the display face (26/32 at 300) and, beside it, a quiet word: "clear" on the user's own queue,
//     "from <the context> · n more" on the continuation, the count on autoplay;
//   · a ROW is 60 tall and flat: the position in the display face (22 at 300, tertiary), the 44 square cover, the title (17/22) over the
//     artists (13/18), the duration at the right and a remove target (✕), 12 apart. No plate at rest: the stage's glass lights it on hover.
//     The whole row is the drag source and the click plays it. Autoplay rows dim.
//   · the board's "shuffle" and "repeat" words are not on this view's verbs (the transport has them); the "autoplay" word is the toggle.
//
// Every metric scales by `Stage.Layout.QueueScale` (1 on the board) and is rounded as rendered, because the lane's drag maths needs each
// slot's height EXACT. The stage's accent, scale and width are the stage's ambient context (`Stage.StageContext`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using Ink = Wavee.Theming.Roles.StageInk;
using UR = Wavee.Themes.Zune.ZuneStageUpNextRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage Up next column: the registered template of <see cref="ViewKeys.QueueStagePane"/> mounts the stateful column.</summary>
internal static class ZuneStageUpNext
{
    /// <summary>The registered template of <see cref="ViewKeys.QueueStagePane"/>.</summary>
    public static Element Template(QueueStagePaneProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ZuneStageUpNextColumn());
}

/// <summary>The stage's Up next column.</summary>
internal sealed class ZuneStageUpNextColumn : Component
{
    Func<QueueSlot, float>? _extentOf;
    QueueProps _queue = null!;
    QueueStagePaneProps _props = null!;
    float _s = 1f;
    float _target;

    float ExtentOf(QueueSlot slot) => slot.Kind switch
    {
        QueueSlotKind.More => Px(UR.MoreRowH) + Px(Spacing.XS) + Px(Spacing.XXS),   // = ShowMore's rendered height + margins (each term rounded as rendered)
        QueueSlotKind.Header => Px(UR.HeaderH),
        _ => Px(UR.RowH),
    };

    float Px(float v) => MathF.Round(v * _s);

    public override Element Render()
    {
        var props = UseProps<QueueStagePaneProps>();
        var ctx = UseContext(Stage.StageContext)!;
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        var lane = props.Lane;
        _props = props;
        _queue = props.Queue;
        _target = ZuneStageTiers.StageOf(in tiers).TargetH;
        var set = ctx.Accent.Value;
        var layout = ctx.Layout.Value;
        // the Huge 4K stage's Up next column beside the lyrics stands in its own frame: the board's 1x rows across that frame's width, not the
        // pane region's scale and list cap
        _s = props.Aside ? 1f : layout.QueueScale;
        lane.SetMetrics(Px(UR.RowH), _extentOf ??= ExtentOf);

        var kids = new List<Element>(2);
        if (lane.SlotCount > 0)
        {
            var slots = new Element[lane.SlotCount];
            for (int i = 0; i < slots.Length; i++)
            {
                int item = lane.ItemAt(i);
                var slot = lane.SlotAt(item);
                if (slot.Kind == QueueSlotKind.More) { slots[i] = ShowMore(props, slot.Section); continue; }
                if (slot.Kind == QueueSlotKind.Header) { slots[i] = Heading(slot.Section, set); continue; }
                slots[i] = _queue.TryRow(slot.Section, slot.Pos, out var row)
                    ? lane.Lane(item, Row(props, row), lane.RowKey("s", row.ItemId, row.Index))
                    : new BoxEl();
            }
            kids.Add(lane.List(new BoxEl { Key = "stageupcoming", Direction = 1, Children = slots }, "stagelane:upcoming"));
        }
        if (_queue.Empty)
            kids.Add(EmptySentence());
        Element body = new BoxEl { Direction = 1, MinHeight = 0f, Children = kids.ToArray() };
        Element rows = lane.SlotCount == 0 ? lane.List(body, "stagelane:empty") : body;

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                AutoplayWord(props, set),
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = props.Aside ? "stagequeue:aside" : "stagequeue",
                    Content = rows,
                },
            ],
        };
    }

    // ── the toggle row ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The "autoplay" word: the accent while on, the secondary ink while off; a press flips it.</summary>
    Element AutoplayWord(QueueStagePaneProps props, AccentSet set)
    {
        bool on = _queue.Autoplay.Enabled;
        Action toggle = props.Verbs.ToggleAutoplay;
        return new BoxEl
        {
            Key = "stage:upnext:autoplay", Direction = 0, AlignItems = FlexAlign.Center, Height = MathF.Max(Px(UR.ToggleRowH), _target), Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinHeight = _target, Shrink = 0f,
                    Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = toggle,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Player.Autoplay))
                        {
                            Size = Px(UR.ToggleSize), LineHeight = Px(UR.ToggleLine), Color = on ? set.Text : Ink.InkSecondary, HoverColor = Ink.Ink,
                            Case = R.Case.For(LabelKind.Command), MaxLines = 1,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>"Nothing up next": the one sentence under an empty list.</summary>
    Element EmptySentence() => new BoxEl
    {
        Padding = new Edges4(0f, Px(UR.EmptyTop), 0f, 0f),
        Children = [new TextEl(Loc.Get(Strings.Player.QueueEmpty)) { Size = Px(UR.EmptySize), LineHeight = Px(UR.EmptyLine), Color = Ink.InkTertiary }],
    };

    // ── the rows ────────────────────────────────────────────────────────────────────────────────────────────────────────

    Element Row(QueueStagePaneProps props, in QueueRowProps row)
    {
        var lane = props.Lane;
        var verbs = props.Verbs;
        EntityRef r = row.Ref;
        QueueRowText text = lane.TextOf(r);
        ulong itemId = row.ItemId;
        int index = row.Index;
        var kids = new List<Element>(5)
        {
            new BoxEl
            {
                Width = Px(UR.NumberW), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(Px(UR.NumberPadL), 0f, 0f, 0f), HitTestVisible = false,
                Children =
                [
                    new TextEl(FormatCache.Int(index + 1))
                    {
                        Size = Px(UR.NumberSize), LineHeight = Px(UR.NumberLine), Weight = UR.NumberWeight, FontFamily = R.Type.DisplayFace,
                        Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap,
                    },
                ],
            },
            ArtTile(text.Art, Px(UR.ArtEdge), (int)Px(UR.ArtDecode)),
            TitleBlock(text),
            // Tabular by geometry: a fixed end-aligned slot, so every colon lands on the same x.
            new BoxEl
            {
                Width = Px(UR.TimeW), Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                Children = [new TextEl(text.DurationMs > 0 ? lane.Clock(text.DurationMs) : "") { Size = Px(UR.TimeSize), LineHeight = Px(UR.TimeLine), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap }],
            },
            RemoveSlot(verbs, row),
        };

        var el = new BoxEl
        {
            Key = lane.RowKey("s", itemId, index) + ":s=" + Px(100f),
            Draggable = lane.OwnDrag(itemId, index, r),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(UR.ColGap), MinHeight = Px(UR.RowH), Height = Px(UR.RowH),
            Corners = R.Shape.All(ShapeRole.Control),
            HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
            PressScale = R.Motion.ScaleSubtle.Press,
            Opacity = row.Section == QueueSection.Autoplay ? UR.AutoplayDim : 1f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => verbs.Play(itemId, index, r),
            Enter = new EnterExit(Dy: UR.RowEnterDy, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: UR.RowExitDy, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children = kids.ToArray(),
        };
        return lane.Attach(el, itemId, index, r);
    }

    /// <summary>Title over artists (an explicit badge before the artists when the track is explicit). A THIN row (title unknown) keeps the slot empty.</summary>
    Element TitleBlock(QueueRowText text)
    {
        if (text.Thin) return new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f };
        var title = new TextEl(text.Title)
        {
            Size = Px(UR.TitleSize), LineHeight = Px(UR.TitleLine), Weight = UR.TitleWeight, Color = Ink.Ink,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element artists = new TextEl(text.Artists) { Size = Px(UR.ArtistSize), LineHeight = Px(UR.ArtistLine), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        Element sub = text.Explicit
            ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                          Children = [Controls.ExplicitBadge(Px(UR.ExplicitBadge), Ink.InkTertiary), artists] }
            : artists;
        return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Children = [title, sub] };
    }

    /// <summary>A square cover; the row's click plays.</summary>
    static Element ArtTile(string? url, float edge, int decode) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, ClipToBounds = true, Corners = CornerRadius4.All(R.Shape.Cover), HitTestVisible = false,
        Children = [Controls.Artwork(url, edge, edge, R.Shape.Cover, decodePx: decode)],
    };

    Element RemoveSlot(IQueueVerbs verbs, in QueueRowProps row)
    {
        ulong itemId = row.ItemId;
        int index = row.Index;
        EntityRef r = row.Ref;
        float w = MathF.Max(Px(UR.RemoveW), _target);
        return row.Removable
            ? new BoxEl
            {
                Width = w, Height = MathF.Max(Px(UR.RowH), _target), Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, BlocksDragArm = true,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = () => verbs.Remove(itemId, index, r),
                Children = [new TextEl(Icons.ChromeClose) { Size = Px(UR.RemoveGlyph), FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary, HoverColor = Ink.Ink }],
            }
            : new BoxEl { Width = w, Shrink = 0f };
    }

    // ── the section headings and "show more" ────────────────────────────────────────────────────────────────────────────

    /// <summary>A section heading (display face, 26/32 at 300) with its quiet word: "clear" on the user's queue, "from … · n more" on the continuation,
    /// the count on autoplay. A slot of the lane, never a drag handle.</summary>
    Element Heading(QueueSection section, AccentSet set)
    {
        string title = SectionTitle(section);
        Action clear = _props.Verbs.Clear;
        var word = new List<Element>(4);
        switch (section)
        {
            case QueueSection.Queue when !_queue.Viewer:
                word.Add(new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinHeight = _target, Shrink = 0f,
                    Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = clear,
                    Children = [new TextEl(Loc.Get(Strings.Player.Clear)) { Size = Px(UR.SubSize), LineHeight = Px(UR.SubLine), Color = Ink.InkSecondary, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
                });
                break;
            case QueueSection.NextUp:
                if (_queue.PlayingFrom is { } pf && pf.Name.Length > 0)
                {
                    word.Add(Sub(Loc.Get(Strings.Theme.Zune.Stage.UpNextFrom), cased: true));
                    word.Add(Sub(pf.Name, cased: false, shrink: true));
                }
                int more = _props.Lane.Remaining(section);
                if (more > 0) word.Add(Sub(Strings.Theme.Zune.Stage.UpNextMore(FormatCache.Int(more)), cased: true));
                break;
            default:
                word.Add(Sub(FormatCache.Int(_queue.Counts.Of(section)), cased: false));
                break;
        }
        return new BoxEl
        {
            Key = "stagehdr:" + QueueSections.Tag(section),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(UR.HeadingGap), Height = Px(UR.HeaderH),
            Layout = LayoutTransition.Slide,
            BlocksDragArm = true,                                              // a slot, never a handle
            Children =
            [
                new TextEl(title)
                {
                    Size = Px(UR.HeadingSize), LineHeight = Px(UR.HeadingLine), Weight = UR.HeadingWeight, FontFamily = R.Type.DisplayFace, Color = Ink.Ink,
                    Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                },
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.XS), MinWidth = 0f, Shrink = 1f, Children = word.ToArray() },
            ],
        };

        TextEl Sub(string text, bool cased, bool shrink = false) => new(text)
        {
            Size = Px(UR.SubSize), LineHeight = Px(UR.SubLine), Color = Ink.InkTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            Case = cased ? R.Case.For(LabelKind.Label) : TextCase.AsWritten, MinWidth = 0f, Shrink = shrink ? 1f : 0f,
        };
    }

    static string SectionTitle(QueueSection section) => Loc.Get(section switch
    {
        QueueSection.Queue => Strings.Player.NextInQueue,
        QueueSection.NextUp => Strings.Player.NextUp,
        _ => Strings.Player.Autoplay,
    });

    Element ShowMore(QueueStagePaneProps props, QueueSection section)
    {
        int remaining = props.Lane.Remaining(section);
        var verbs = props.Verbs;
        return new BoxEl
        {
            Key = "stagemore:" + QueueSections.Tag(section),
            // scaled like every other stage slot: height + margins sum to ExtentOf's More extent (the virtualizer's offsets)
            Direction = 0, Height = Px(UR.MoreRowH), Gap = Px(Spacing.S), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            BlocksDragArm = true, Margin = new Edges4(0f, Px(Spacing.XS), 0f, Px(Spacing.XXS)), Corners = R.Shape.All(ShapeRole.Control),
            HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = () => verbs.ShowMore(section),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new TextEl(Icons.ChevronDown) { Size = Px(UR.MoreGlyph), FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
                new TextEl(FormatCache.Int(remaining)) { Size = Px(UR.MoreSize), LineHeight = Px(UR.MoreLine), Color = Ink.InkTertiary },
            ],
        };
    }
}
