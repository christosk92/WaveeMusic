// ── Themes/Classic/Structure/Views/Detail/ClassicPlaylistExtras.cs — Classic's playlist daylist strip and recommendations (themes plan 6.1, T9b) ────────
//
// From `Entities\Playlist.UI.cs` §6 (W26) and §7 (W10):
//
//   · the DAYLIST STRIP: HH:MM:SS flip cells (10 × 20 compact / 13 × 28 hero) + "Next update at {time}", or either one alone (Settings ▸ Appearance ▸ Daylist
//     clock, #185). Past the window the strip is Rolling: an indeterminate ring replaces the digits ("Updating your daylist…"), or — once the rollover ladder has
//     given up — a full, static ring ("Your next daylist is running late"). `Playlist.DaylistHost` keeps the clock (a wall-clock sample anchored against the
//     frame clock and re-anchored on `DaylistCountdown`'s cadence, the one-second interval, the expiry timer) and the clock-mode decisions
//     (`DaylistClockShows`, `DaylistClockFace`); the strip is these cells;
//   · the RECOMMENDED SONGS section's header and rows (`TableProfile.Recommendations`): `Playlist.RecsSection` keeps the edge's demand (its MOUNT is the first
//     fetch, and the batch refills itself once the user has added all of it), the adding state and the verbs; one row is the shared media surface
//     (`Shape.Row` over `Track.RowData`: the row plate, the hand, the play FAB over the art, the now-playing pill, a track drag and the single-track menu with its
//     "…"). The row CLICK plays the track; the "+" and the duration are the trailing cluster — the "+" its own gesture owner, a spinner while ITS add is in flight.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicPlaylist
{
    // ══ 1. THE DAYLIST STRIP ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistDaylist"/>.</summary>
    public static Element DaylistTemplate(PlaylistDaylistProps p, in ViewContext ctx)
    {
        if (p.Phase == DaylistCountdown.Phase.Idle) return new BoxEl();
        float rowH = p.Compact ? Controls.FlipCompactRowHeight : Controls.FlipHeroRowHeight;
        if (p.Phase == DaylistCountdown.Phase.Rolling) return Rolled(p.Late, rowH);

        string? caption = p.Caption;
        if (!p.Cells) return Strip(null, caption, rowH);

        ColorF ink = Design.Palette.TextInk(p.Accent());
        int hours = p.Hours, minutes = p.Minutes, seconds = p.Seconds;
        Element[] cells =
        [
            Controls.FlipDigit(hours / 10, rowH, ink) with { Key = "h1" },
            Controls.FlipDigit(hours % 10, rowH, ink) with { Key = "h0" },
            Colon(rowH, ink, p.Compact, "c1"),
            Controls.FlipDigit(minutes / 10, rowH, ink) with { Key = "m1" },
            Controls.FlipDigit(minutes % 10, rowH, ink) with { Key = "m0" },
            Colon(rowH, ink, p.Compact, "c2"),
            Controls.FlipDigit(seconds / 10, rowH, ink) with { Key = "s1" },
            Controls.FlipDigit(seconds % 10, rowH, ink) with { Key = "s0" },
        ];
        return Strip(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = cells }, caption, rowH);
    }

    /// <summary>The window has ended, so there are no digits to flip. While the next edition is on its way the stock indeterminate ring (the row's height)
    /// stands in for them beside "Updating your daylist…"; once the rollover ladder has given up (or never ran — offline) it is a full, static ring beside "Your
    /// next daylist is running late". The Home clock's "Check again" is not repeated here. Every clock mode shows it: it is a status, not a clock.</summary>
    static Element Rolled(bool late, float rowH)
    {
        Element ring = late
            ? ProgressRing.Determinate(1f, rowH, foreground: Tok.TextTertiary, track: Tok.StrokeControlStrongDefault)
            : ProgressRing.Indeterminate(rowH);
        string caption = Loc.Get(late ? Strings.Home.Daylist.Late : Strings.Home.DaylistUpdating);
        return Strip(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [ring] }, caption, rowH);
    }

    /// <summary>The strip: the leading cluster (the flip cells, or the ring) and its tertiary caption; a clock mode that shows only one passes null for the
    /// other. At least the cells' row height, so the update-time-only caption holds the strip's height and the Rolling ring that replaces it never jumps the
    /// hero.</summary>
    static Element Strip(BoxEl? lead, string? caption, float rowH)
    {
        Element? text = caption is null
            ? null
            : Caption(caption) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true, MinHeight = rowH,
            Children = lead is null ? [text!] : text is null ? [lead] : [lead, text],
        };
    }

    static Element Colon(float rowH, ColorF ink, bool compact, string key) => new BoxEl
    {
        Key = key, Width = compact ? ClassicPlaylistLayout.ColonCompactW : ClassicPlaylistLayout.ColonHeroW, Height = rowH,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            new TextEl(":")
            {
                Size = compact ? ClassicPlaylistLayout.ColonCompactSize : ClassicPlaylistLayout.ColonHeroSize, Weight = ClassicPlaylistLayout.ColonWeight,
                Color = ink, FontFamily = ClassicPlaylistLayout.ColonFont,
            },
        ],
    };

    // ══ 2. RECOMMENDED SONGS ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistRecs"/>: the header over the rows, one slot each (keyed <c>rec:&lt;track slot&gt;</c>).
    /// Where the tier record leads an EMPTY playlist's section (Compact, C-Empty-Compact) the lead stands over the header: "Nothing here yet", its line
    /// and the field that finds songs to add; the section's commands are the tier's targets (40 at Compact).</summary>
    public static Element RecsTemplate(PlaylistRecsProps p, in ViewContext ctx)
    {
        var tier = ClassicDetailTiers.Of(ctx.Tier());
        float edge = MathF.Max(ClassicPlaylistLayout.RecsButton, tier.Target);
        var kids = new List<Element>(p.Rows.Count + 2);
        if (tier.EmptyLead && p.Lead is { } lead) kids.Add(EmptyLead(lead));
        kids.Add(RecsHeader(p, edge));
        for (int i = 0; i < p.Rows.Count; i++)
            kids.Add(ctx.View(ViewKeys.PlaylistRecRow, p.Rows[i]) with { Key = "rec:" + p.Rows[i].Slot });
        return new BoxEl { Direction = 1, Children = kids.ToArray() };
    }

    static readonly string[] s_noSuggestions = [];

    /// <summary>The empty playlist's lead (C-Empty-Compact): the centred 20/28 "Nothing here yet" over its line, 16 over and 4 under, then the 40 field that
    /// looks for songs to add (its submit is the core's: Search for the query), 12 apart, at the rows' 16 gutter. It enters with the late rows' fade.</summary>
    static Element EmptyLead(PlaylistRecsLead lead) => new BoxEl
    {
        Key = "rec:lead", Direction = 1, Gap = ClassicPlaylistLayout.LeadGap,
        Padding = new Edges4(Spacing.L, 0f, Spacing.L, ClassicPlaylistLayout.LeadGap),
        Enter = ClassicDetailKit.FadeUp,
        Children =
        [
            new BoxEl
            {
                Key = "rec:lead:message", Direction = 1, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Padding = new Edges4(0f, ClassicPlaylistLayout.LeadMessagePadTop, 0f, ClassicPlaylistLayout.LeadMessagePadBottom),
                Children =
                [
                    Subtitle(Loc.Get(Strings.Detail.Empty.NoTracks)) with { MaxLines = 2, Wrap = TextWrap.WrapWholeWords },
                    Caption(Loc.Get(Strings.Detail.Empty.FindSongs)) with { MaxLines = 2, Wrap = TextWrap.WrapWholeWords },
                ],
            },
            new BoxEl
            {
                Key = "rec:lead:find", Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    AutoSuggestBox.Create(s_noSuggestions, Loc.Get(Strings.Detail.Empty.SearchSongs), onQuerySubmitted: lead.Find, queryIcon: Icons.Search,
                        minHeight: ClassicDetailTiers.CompactTarget, cornerRadius: Radii.Control, grow: 1f),
                ],
            },
        ],
    };

    /// <summary>The header: the title, the "No suggestions right now" note after an empty reply, and the spinner (asked) or the refresh, each
    /// <paramref name="edge"/> square (the tuned 32, the Compact 40).</summary>
    static Element RecsHeader(PlaylistRecsProps p, float edge) => new BoxEl
    {
        Key = "rec:header", Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
        MinHeight = MathF.Max(Track.RowMetrics.RowHeight, edge + ClassicPlaylistLayout.RecsHeaderPadY), Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f),
        Children =
        [
            BodyStrong(Loc.Get(Strings.Detail.Recommended)) with { Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1 },
            p.Empty
                ? new TextEl(Loc.Get(Strings.Detail.NoSuggestions)) { Size = ClassicPlaylistLayout.RecsNoteSize, Color = Tok.TextTertiary, MaxLines = 1 }
                : new BoxEl(),
            p.Loading
                ? new BoxEl
                {
                    Width = edge, Height = edge, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Track.Spinner()],
                }
                : ToolTip.Wrap(new BoxEl
                {
                    Width = edge, Height = edge,
                    Corners = CornerRadius4.All(edge / 2f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
                    HoverScale = R.Motion.ScaleEmphatic.Hover, PressScale = R.Motion.ScaleEmphatic.Press,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = p.Refresh,
                    Children = [Icon(Icons.Refresh, ClassicPlaylistLayout.RecsRefreshGlyph, Tok.TextSecondary)],
                }, Loc.Get(Strings.Detail.Recommended)),
        ],
    };

    /// <summary>The surface's own height at a 40 art (the 8-DIP padding around it): the default 64 floor is for rows that carry more, and a recommendation is a
    /// title over its artists.</summary>
    static readonly SurfaceShape s_recShape = Shape.Row(ClassicPlaylistLayout.RecArt) with { MinHeight = ClassicPlaylistLayout.RecArt + 2f * SurfaceGeometry.RowPad };

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistRecRow"/>: one recommendation on the shared media surface. A slot of its own because
    /// <c>Track.RowData</c> reads the track table: a title landing re-renders this row, not the section.</summary>
    public static Element RecRowTemplate(PlaylistRecRowProps p, in ViewContext ctx)
    {
        var t = new Track(p.Slot);
        int slot = p.Slot;
        var add = p.Add;
        var play = p.Play;
        // At Compact (C-Playlist-Compact, C-Empty-Compact) the "+" is a 40 target around its 28 ring and the row has no duration lane.
        bool compact = ClassicDetailTiers.Of(ctx.Tier()).EmptyLead;
        float box = compact ? ClassicDetailTiers.CompactTarget : ClassicPlaylistLayout.RecAddBox;

        Element addCell = p.Busy
            ? new BoxEl
            {
                Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center, Children = [Track.Spinner()],
            }
            : ToolTip.Wrap(compact ? CompactAdd(() => add(slot)) : Track.AddButton(() => add(slot)), Loc.Get(Strings.Detail.AddToPlaylist));
        var trailing = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children = compact
                ? [addCell]
                : [addCell, R.Type.TrackMeta(Track.Format.TrackTime(t.DurationMs)) with { Width = ClassicPlaylistLayout.RecDurationW, MaxLines = 1 }],
        };
        var data = Track.RowData(t, new Track.RowDataOptions(OnClick: () => play(slot), Trailing: trailing));
        return Controls.Surface(data, s_recShape);
    }

    /// <summary>The Compact "+": a 40 target (the brief's Compact rule) around the row's 28 ring.</summary>
    static Element CompactAdd(Action onAdd) => new BoxEl
    {
        Width = ClassicDetailTiers.CompactTarget, Height = ClassicDetailTiers.CompactTarget, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(ClassicDetailTiers.CompactTarget / 2f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
        PressScale = R.Motion.ScaleEmphatic.Press,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, BlocksDragArm = true, OnClick = onAdd,
        Children =
        [
            new BoxEl
            {
                Width = ClassicPlaylistLayout.RecAddBox, Height = ClassicPlaylistLayout.RecAddBox, HitTestVisible = false,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(ClassicPlaylistLayout.RecAddBox / 2f), BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                Children = [Icon(Icons.Add, ClassicPlaylistLayout.RecAddGlyph, Tok.TextPrimary)],
            },
        ],
    };
}
