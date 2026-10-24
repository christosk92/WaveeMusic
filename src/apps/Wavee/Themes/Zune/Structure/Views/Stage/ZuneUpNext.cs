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
using Wavee.HomeUi;
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
    float _hs = 1f;     // the "played before this" section's tier factor (1.5 at Huge 4K)
    float _target;
    ZuneStageQueueRow _q = ZuneStageUpNextRules.Of(WidthTier.Standard);

    float ExtentOf(QueueSlot slot) => slot.Kind switch
    {
        QueueSlotKind.More => Px(UR.MoreRowH) + Px(Spacing.XS) + Px(Spacing.XXS),   // = ShowMore's rendered height + margins (each term rounded as rendered)
        QueueSlotKind.Header => Px(_q.HeaderH),
        _ => Px(_q.RowH),
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
        var window = tiers.Policy is null ? WidthTier.Standard : tiers.Window;
        _q = props.Aside ? UR.Docked(UR.Of(window)) : UR.Of(window);   // the docked column (audit B3, Vr): the board's three columns, not the page's five
        _hs = UR.HistoryScale(window);
        var set = ctx.Accent.Value;
        var layout = ctx.Layout.Value;
        // the Huge 4K stage's Up next column beside the lyrics stands in its own frame: the board's 1x rows across that frame's width, not the
        // pane region's scale and list cap
        _s = props.Aside ? 1f : layout.QueueScale;
        lane.SetMetrics(Px(_q.RowH), _extentOf ??= ExtentOf);

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

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                ModesRow(props, set),
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = props.Aside ? "stagequeue:aside" : "stagequeue",
                    Content = rows,
                },
            ],
        };
        // Huge: the list and, right of it, the autoplay panel
        if (_q.SidePanelW <= 0f || props.Aside) return column;
        return new BoxEl
        {
            Direction = 0, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Gap = Px(UR.SideGap),
            Children = [column, SidePanel(props, set)],
        };
    }

    // ── the toggle row ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's three mode words (Z-Queue-Page): "shuffle", "repeat" and "autoplay", the accent while on, the secondary ink while off; a press flips it.</summary>
    Element ModesRow(QueueStagePaneProps props, AccentSet set)
    {
        var words = new List<Element>(3);
        if (props.Player is { } player)
        {
            words.Add(ModeWord(Strings.Player.Shuffle, _queue.Shuffle, player.ToggleShuffle, "stage:upnext:shuffle", set));
            words.Add(ModeWord(Strings.Player.Repeat, (int)_queue.Repeat != 0, player.CycleRepeat, "stage:upnext:repeat", set));
        }
        words.Add(ModeWord(Strings.Player.Autoplay, _queue.Autoplay.Enabled, props.Verbs.ToggleAutoplay, "stage:upnext:autoplay", set));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(UR.ModeGap), Height = MathF.Max(Px(UR.ToggleRowH), _target), Shrink = 0f,
            Children = words.ToArray(),
        };
    }

    /// <summary>One mode word; on a Compact window (the board's chips) a flat plate, the accent filled while on.</summary>
    Element ModeWord(string key, bool on, Action press, string nodeKey, AccentSet set)
    {
        bool chip = _q.ModeChips;
        return new BoxEl
        {
            Key = nodeKey, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinHeight = _target, Shrink = 0f,
            Padding = chip ? new Edges4(Px(UR.ChipPadX), 0f, Px(UR.ChipPadX), 0f) : default,
            Fill = chip ? (on ? set.Text : Ink.Plate) : ColorF.Transparent, Corners = chip ? R.Shape.All(ShapeRole.Control) : default,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = press,
            Children =
            [
                new TextEl(Loc.Get(key))
                {
                    Size = Px(UR.ToggleSize), LineHeight = Px(UR.ToggleLine), Color = chip ? (on ? Tok.TextOnAccentPrimary : Ink.Ink) : on ? set.Text : Ink.InkSecondary, HoverColor = chip && on ? Tok.TextOnAccentPrimary : Ink.Ink,
                    Case = R.Case.For(LabelKind.Command), MaxLines = 1,
                },
            ],
        };
    }

    /// <summary>Huge's side panel (Z-Queue-Page-Huge): "played before this" (the tracks played earlier, newest first, each with when), then "autoplay when the queue
    /// ends", what it does in a sentence, and the switch as a word.</summary>
    Element SidePanel(QueueStagePaneProps props, AccentSet set)
    {
        bool on = _queue.Autoplay.Enabled;
        string name = _queue.PlayingFrom is { } pf && pf.Name.Length > 0 ? pf.Name : "";
        var kids = new List<Element>(3)
        {
            new TextEl(Loc.Get(Strings.Theme.Zune.Stage.AutoplayWhenEnds))
            {
                Size = Px(UR.SideHeadingSize), LineHeight = Px(UR.SideHeadingLine), Weight = UR.HeadingWeight, FontFamily = R.Type.DisplayFace, Color = Ink.Ink,
                Case = R.Case.For(LabelKind.Heading), Wrap = TextWrap.Wrap, Margin = new Edges4(0f, Px(UR.HistorySectionGap * _hs), 0f, 0f),
            },
        };
        if (name.Length > 0)
            kids.Add(new TextEl(Strings.Theme.Zune.Stage.AutoplayBlurb(name))
            {
                Size = Px(UR.SideBodySize), LineHeight = Px(UR.SideBodyLine), Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, Margin = new Edges4(0f, Px(Spacing.S), 0f, 0f),
            });
        kids.Add(new BoxEl
        {
            Key = "stage:upnext:autoplay-switch", Direction = 0, AlignItems = FlexAlign.Center, MinHeight = _target, Margin = new Edges4(0f, Px(Spacing.M), 0f, 0f),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = props.Verbs.ToggleAutoplay,
            Children = [new TextEl(Loc.Get(on ? Strings.Theme.Zune.Stage.AutoplayIsOn : Strings.Theme.Zune.Stage.AutoplayIsOff))
                { Size = Px(UR.SideSwitchSize), LineHeight = Px(UR.SideSwitchLine), Color = on ? set.Text : Ink.InkSecondary, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Command) }],
        });
        var autoplay = new BoxEl { Key = "stage:upnext:autoplay-block", Direction = 1, Layout = LayoutTransition.Slide, Children = kids.ToArray() };
        return new BoxEl
        {
            Direction = 1, Width = Px(_q.SidePanelW), Shrink = 0f, MinHeight = 0f,
            Children = [HistorySection(props), autoplay],
        };
    }

    // ── "played before this" ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The heading on its 56 slot (the main column's heading height, so the two headings share a baseline) and the played-before rows. With nothing played yet
    /// the section is the heading and one quiet sentence; the autoplay block under it slides when rows arrive instead of jumping.</summary>
    Element HistorySection(QueueStagePaneProps props)
    {
        var rows = _queue.History;
        int n = Math.Min(rows.Length, UR.HistoryShown);
        float h = _hs;
        var kids = new List<Element>(n + 2)
        {
            new BoxEl
            {
                Key = "stage:upnext:history-head", Direction = 0, AlignItems = FlexAlign.End, Height = Px(UR.HistoryHeadH * h), Shrink = 0f,
                Padding = new Edges4(0f, 0f, 0f, Px(8f * h)),
                Children =
                [
                    new TextEl(Loc.Get(Strings.Theme.Zune.Stage.PlayedBeforeThis))
                    {
                        Size = Px(UR.SideHeadingSize), LineHeight = Px(UR.SideHeadingLine), Weight = UR.HeadingWeight, FontFamily = R.Type.DisplayFace, Color = Ink.Ink,
                        Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            },
        };
        if (n == 0)
            kids.Add(new TextEl(Loc.Get(Strings.Theme.Zune.Stage.PlayedBeforeEmpty))
                { Size = Px(UR.EmptySize), LineHeight = Px(UR.EmptyLine), Color = Ink.InkTertiary, Wrap = TextWrap.Wrap });
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < n; i++) kids.Add(HistoryRow(props, rows[i], now, h));
        return new BoxEl { Key = "stage:upnext:history", Direction = 1, Layout = LayoutTransition.Slide, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>One played-before row (Z-Queue-Page-Huge): the cover in a 64 column, the title over the artists, the "4 min ago" word at the right; dim at rest. A press plays it.</summary>
    Element HistoryRow(QueueStagePaneProps props, QueueHistoryRow row, long nowMs, float h)
    {
        QueueRowText text = props.Lane.TextOf(row.Ref);
        var verbs = props.Verbs;
        float art = Px(UR.HistoryArt * h);
        var title = new TextEl(text.Thin ? "" : text.Title)
        {
            Size = Px(UR.HistoryTitleSize * h), LineHeight = Px(UR.HistoryTitleLine * h), Color = Ink.Ink,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var artists = new TextEl(text.Thin ? "" : text.Artists)
        {
            Size = Px(UR.HistoryMetaSize * h), LineHeight = Px(UR.HistoryMetaLine * h), Color = Ink.InkSecondary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Key = "stage:upnext:history:" + row.Uri,
            Direction = 0, AlignItems = FlexAlign.Center, Height = Px(UR.HistoryRowH * h), Shrink = 0f, MinWidth = 0f,
            Corners = R.Shape.All(ShapeRole.Control), HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
            PressScale = R.Motion.ScaleSubtle.Press, Opacity = UR.HistoryDim,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => verbs.PlayHistory(row),
            Enter = new EnterExit(Dy: UR.RowEnterDy, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: UR.RowExitDy, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new BoxEl { Width = Px(UR.HistoryArtCol * h), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, HitTestVisible = false, Children = [ArtTile(text.Art, art, (int)Px(UR.ArtDecode))] },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, HitTestVisible = false, Children = [title, artists] },
                new BoxEl
                {
                    Width = Px(UR.HistoryWhenW * h), Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center, HitTestVisible = false,
                    Children = [new TextEl(When(row.PlayedAtMs, nowMs))
                        { Size = Px(UR.HistoryMetaSize * h), LineHeight = Px(UR.HistoryMetaLine * h), Color = Ink.InkTertiary, Wrap = TextWrap.Wrap }],
                },
            ],
        };
    }

    /// <summary>"4 min ago" / "2 h ago" / "yesterday" / a weekday / "23 Sep" for a play that started at <paramref name="playedMs"/> (the Home's own ladder, so the two say it alike).</summary>
    static string When(long playedMs, long nowMs)
        => WhenCaption.Of(playedMs, nowMs, playingNow: false, TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);

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
        var kids = new List<Element>(7);
        if (_q.NumberW > 0f)
            kids.Add(new BoxEl
            {
                Width = Px(_q.NumberW), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(Px(_q.NumberPadL), 0f, 0f, 0f), HitTestVisible = false,
                Children =
                [
                    new TextEl(FormatCache.Int(index + 1))
                    {
                        Size = Px(_q.NumberSize), LineHeight = Px(_q.NumberLine), Weight = UR.NumberWeight, FontFamily = R.Type.DisplayFace,
                        Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap,
                    },
                ],
            });
        kids.Add(ArtTile(text.Art, Px(_q.ArtEdge), (int)Px(UR.ArtDecode)));
        kids.Add(TitleBlock(text, _q.Album ? 3f : 1f));
        // Huge: the album column (the board's 2fr beside the 3fr title)
        if (_q.Album)
            kids.Add(new BoxEl
            {
                Grow = 2f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, HitTestVisible = false,
                Children = [new TextEl(text.Album) { Size = Px(_q.ArtistSize), LineHeight = Px(_q.ArtistLine), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
            });
        // Tabular by geometry: a fixed end-aligned slot, so every colon lands on the same x (Compact has none: the board's row is art, titles, remove)
        if (_q.TimeW > 0f)
            kids.Add(new BoxEl
            {
                Width = Px(_q.TimeW), Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                Children = [new TextEl(text.DurationMs > 0 ? lane.Clock(text.DurationMs) : "") { Size = Px(_q.TimeSize), LineHeight = Px(_q.TimeLine), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap }],
            });
        kids.Add(RemoveSlot(verbs, row));

        var el = new BoxEl
        {
            Key = lane.RowKey("s", itemId, index) + ":s=" + Px(100f),
            Draggable = lane.OwnDrag(itemId, index, r),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(_q.ColGap), MinHeight = Px(_q.RowH), Height = Px(_q.RowH),
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
    Element TitleBlock(QueueRowText text, float grow = 1f)
    {
        if (text.Thin) return new BoxEl { Grow = grow, Basis = 0f, MinWidth = 0f };
        var title = new TextEl(text.Title)
        {
            Size = Px(_q.TitleSize), LineHeight = Px(_q.TitleLine), Weight = UR.TitleWeight, Color = Ink.Ink,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element artists = new TextEl(text.Artists) { Size = Px(_q.ArtistSize), LineHeight = Px(_q.ArtistLine), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        Element sub = text.Explicit
            ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                          Children = [Controls.ExplicitBadge(Px(UR.ExplicitBadge), Ink.InkTertiary), artists] }
            : artists;
        return new BoxEl { Direction = 1, Grow = grow, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Children = [title, sub] };
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
        float w = MathF.Max(Px(_q.RemoveW), _target);
        return row.Removable
            ? new BoxEl
            {
                Width = w, Height = MathF.Max(Px(_q.RowH), _target), Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, BlocksDragArm = true,
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
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(UR.HeadingGap), Height = Px(_q.HeaderH),
            Layout = LayoutTransition.Slide,
            BlocksDragArm = true,                                              // a slot, never a handle
            Children =
            [
                new TextEl(title)
                {
                    Size = Px(_q.HeadingSize), LineHeight = Px(_q.HeadingLine), Weight = UR.HeadingWeight, FontFamily = R.Type.DisplayFace, Color = Ink.Ink,
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
