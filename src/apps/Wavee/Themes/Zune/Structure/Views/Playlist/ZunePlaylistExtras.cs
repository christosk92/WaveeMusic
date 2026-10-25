// ── Themes/Zune/Structure/Views/Playlist/ZunePlaylistExtras.cs — Zune's daylist strip and recommended songs (themes plan 6.3; P5 Z6b1) ────────────
//
// Boards Z-State-Empty-Playlist (+ -Compact), Z-Playlist-Owned-Followed ("recommended songs: a section under the table, with 'add' words per row").
//
//   · the DAYLIST STRIP: HH:MM:SS in the numeral voices (the rail's compact digits, the hero's large ones), every digit in a fixed cell and the colons in
//     theirs, so a tick never moves the strip; "next update at {time}" beside it in the tertiary ink; or either one alone (Settings > Appearance > Daylist
//     clock). Past the window the stock ring stands in for the digits ("updating your daylist..."), or, once the rollover ladder has given up, a full ring
//     ("your next daylist is running late"). The strip reserves its row height in every phase, so the ring that replaces the digits moves nothing.
//     `Playlist.DaylistHost` keeps the clock and the clock-mode decisions;
//   · the RECOMMENDED SONGS section: the heading (26/32 light, lowercased) with a "refresh" word (a spinner while asked; the "no suggestions" note after an
//     empty reply) over rows with a 40 cover and an outlined "add" pill. `Playlist.RecsSection` keeps the edge's demand (its MOUNT is the first fetch,
//     and the batch refills itself), the adding state and the verbs. The row CLICK plays the track; the pill is the trailing gesture owner.
//   · the EMPTY playlist's LEAD (Z-State-Empty-Playlist): "let's find something for this playlist" (the 40/48 display step; 26/32 at Compact), the line
//     under it and the field that finds songs to add (its submit is the core's: Search for the query), the section's heading under them. The box RESERVES
//     its height and eases a change of it, so the rows under it never jump; it enters with the late rows' fade.
//
// Numbers: `Rules\ZunePlaylistRules.cs` and `ZunePlaylistTiers`.

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
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Z = Wavee.Themes.Zune.ZunePlaylistRules;

namespace Wavee.Themes.Zune;

internal static partial class ZunePlaylist
{
    // ══ 1. THE DAYLIST STRIP ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistDaylist"/>.</summary>
    public static Element DaylistTemplate(PlaylistDaylistProps p, in ViewContext ctx)
    {
        if (p.Phase == DaylistCountdown.Phase.Idle) return new BoxEl();
        float rowH = p.Compact ? Z.DaylistCompactH : Z.DaylistHeroH;
        if (p.Phase == DaylistCountdown.Phase.Rolling) return Rolled(p.Late, rowH);

        string? caption = p.Caption;
        if (!p.Cells) return Strip(null, caption, rowH);

        ColorF ink = Design.Palette.TextInk(p.Accent());
        bool compact = p.Compact;
        int hours = p.Hours, minutes = p.Minutes, seconds = p.Seconds;
        Element[] cells =
        [
            Digit("h1", hours / 10, compact, rowH, ink), Digit("h0", hours % 10, compact, rowH, ink), Colon("c1", compact, rowH, ink),
            Digit("m1", minutes / 10, compact, rowH, ink), Digit("m0", minutes % 10, compact, rowH, ink), Colon("c2", compact, rowH, ink),
            Digit("s1", seconds / 10, compact, rowH, ink), Digit("s0", seconds % 10, compact, rowH, ink),
        ];
        return Strip(new BoxEl { Key = "daylist:cells", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = cells }, caption, rowH);
    }

    static TextEl Numeral(string text, bool compact) => compact ? R.Type.Numeral(text) : R.Type.NumeralLarge(text);

    static Element Digit(string key, int value, bool compact, float rowH, ColorF ink) => new BoxEl
    {
        Key = key, Width = compact ? Z.DigitCompactW : Z.DigitHeroW, Height = rowH, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Numeral(value.ToString(System.Globalization.CultureInfo.InvariantCulture), compact) with { Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };

    static Element Colon(string key, bool compact, float rowH, ColorF ink) => new BoxEl
    {
        Key = key, Width = compact ? Z.ColonCompactW : Z.ColonHeroW, Height = rowH, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Numeral(":", compact) with { Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };

    /// <summary>The window has ended, so there are no digits to flip. While the next edition is on its way the stock indeterminate ring stands in for them;
    /// once the rollover ladder has given up (or never ran: offline) it is a full, static ring. Every clock mode shows it: it is a status, not a clock.</summary>
    static Element Rolled(bool late, float rowH)
    {
        Element ring = late
            ? ProgressRing.Determinate(1f, rowH, foreground: Tok.TextTertiary, track: Tok.StrokeControlStrongDefault)
            : ProgressRing.Indeterminate(rowH);
        string caption = Loc.Get(late ? Strings.Home.Daylist.Late : Strings.Home.DaylistUpdating);
        return Strip(new BoxEl { Key = "daylist:ring", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [ring] }, caption, rowH);
    }

    /// <summary>The strip: the leading cluster (the digits, or the ring) and its tertiary caption; a clock mode that shows only one passes null for the other.
    /// At least the cells' row height, so the caption-only mode holds the strip's height and the ring that replaces the digits never jumps the rail.</summary>
    static Element Strip(BoxEl? lead, string? caption, float rowH)
    {
        Element? text = caption is null
            ? null
            : R.Type.DenseMeta(caption) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };
        return new BoxEl
        {
            Direction = 0, Gap = Z.DaylistGap, AlignItems = FlexAlign.Center, Wrap = true, MinHeight = rowH,
            Children = lead is null ? [text!.Value] : text is null ? [lead.Value] : [lead.Value, text.Value],
        };
    }

    // ══ 2. RECOMMENDED SONGS ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistRecs"/>: the lead of an empty playlist, the heading and the rows, one slot each (keyed
    /// <c>zune.playlist.rec:&lt;track slot&gt;</c>).</summary>
    public static Element RecsTemplate(PlaylistRecsProps p, in ViewContext ctx)
    {
        var lay = ZunePlaylistTiers.Of(ctx.Tier());
        var kids = new List<Element>(p.Rows.Count + 2);
        if (p.Lead is { } lead) kids.Add(EmptyLead(lead, lay));
        kids.Add(RecsHeader(p, lay));
        for (int i = 0; i < p.Rows.Count; i++)
            kids.Add(ctx.View(ViewKeys.PlaylistRecRow, p.Rows[i]) with { Key = Z.RecRowPrefix + p.Rows[i].Slot });
        return new BoxEl { Key = Z.RecsKey, Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
    }

    static readonly string[] s_noSuggestions = [];

    /// <summary>The empty playlist's lead (see the file header). The title and the line wrap rather than clip; the field is the full width with the pill's corner at
    /// Compact, the board's measure with a square corner elsewhere.</summary>
    static Element EmptyLead(PlaylistRecsLead lead, ZunePlaylistLayout lay)
    {
        TextEl title = lay.LeadLarge ? R.Type.HeroTitle(Loc.Get(Strings.Theme.Zune.Detail.EmptyTitle)) : R.Type.Heading(Loc.Get(Strings.Theme.Zune.Detail.EmptyTitle));
        string placeholder = Loc.Get(Strings.Detail.Empty.SearchSongs);
        Element field = lay.LeadFieldFull
            ? AutoSuggestBox.Create(s_noSuggestions, placeholder, onQuerySubmitted: lead.Find, queryIcon: Icons.Search,
                minHeight: lay.LeadFieldH, cornerRadius: lay.LeadFieldRadius, grow: 1f)
            : AutoSuggestBox.Create(s_noSuggestions, placeholder, width: Z.LeadFieldW, onQuerySubmitted: lead.Find, queryIcon: Icons.Search,
                minHeight: lay.LeadFieldH, cornerRadius: lay.LeadFieldRadius);
        return new BoxEl
        {
            Key = Z.LeadKey, Direction = 1, Gap = Z.LeadLineGap, MinHeight = lay.LeadMinHeight, MinWidth = 0f, Animate = Design.Reveal.Resize,
            Padding = new Edges4(Z.RecsInset, lay.LeadTop, Z.RecsInset, Z.LeadBottom),
            Enter = K.FadeUp,
            Children =
            [
                title with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading), MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                R.Type.DenseMeta(Loc.Get(Strings.Theme.Zune.Detail.EmptyLine)) with
                {
                    Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                new BoxEl
                {
                    Key = "lead:find", Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = new Edges4(0f, Z.LeadFieldGap, 0f, 0f),
                    Children = [field],
                },
            ],
        };
    }

    /// <summary>The heading row: "recommended songs", the "no suggestions" note after an empty reply, then the spinner (asked) or the "refresh" word.</summary>
    static Element RecsHeader(PlaylistRecsProps p, ZunePlaylistLayout lay)
    {
        float target = lay.Target;
        return new BoxEl
        {
            Key = Z.RecsHeadKey, Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
            MinHeight = MathF.Max(Z.RecsHeadH, target), Padding = new Edges4(Z.RecsInset, Z.RecsTop, Z.RecsInset, 0f),
            Children =
            [
                K.Heading(Loc.Get(Strings.Detail.Recommended)) with { Color = Tok.TextPrimary, Grow = 1f, Basis = 0f },
                p.Empty
                    ? R.Type.DenseMeta(Loc.Get(Strings.Detail.NoSuggestions)) with
                    {
                        Size = Z.NoteSize, Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                    }
                    : new BoxEl(),
                p.Loading
                    ? new BoxEl { Width = target, Height = target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Track.Spinner()] }
                    : Word("recs:refresh", Loc.Get(Strings.Theme.Zune.Detail.Refresh), accent: false, p.Refresh, target),
            ],
        };
    }

    /// <summary>The surface's own height at a 40 cover (the 8-DIP padding around it): the default floor is for rows that carry more, and a recommendation is a
    /// title over its artists.</summary>
    static readonly SurfaceShape s_recShape = Shape.Row(Z.RecArt) with { MinHeight = Z.RecArt + 2f * SurfaceGeometry.RowPad };

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistRecRow"/>: one recommendation on the shared media surface. A slot of its own because
    /// <c>Track.RowData</c> reads the track table: a title landing re-renders this row, not the section.</summary>
    public static Element RecRowTemplate(PlaylistRecRowProps p, in ViewContext ctx)
    {
        var lay = ZunePlaylistTiers.Of(ctx.Tier());
        var t = new Track(p.Slot);
        int slot = p.Slot;
        var add = p.Add;
        var play = p.Play;

        Element addCell = p.Busy
            ? new BoxEl { Key = "rec:busy", Width = lay.Target, Height = lay.Target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Track.Spinner()] }
            : AddPill(() => add(slot), lay);
        var trailing = new BoxEl
        {
            Direction = 0, Gap = Z.RecTrailGap, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children = lay.RecDuration
                ? [addCell, R.Type.TrackMeta(Track.Format.TrackTime(t.DurationMs)) with { Width = Z.DurationW, MaxLines = 1 }]
                : [addCell],
        };
        var data = Track.RowData(t, new Track.RowDataOptions(OnClick: () => play(slot), Trailing: trailing));
        return Controls.Surface(data, s_recShape);
    }

    /// <summary>The "add" pill: the board's 28-high outlined pill with its lowercase word, inside the tier's hit edge (40 at Compact), the row's own gesture owner.</summary>
    static Element AddPill(Action onAdd, ZunePlaylistLayout lay) => new BoxEl
    {
        Key = "rec:add", Direction = 0, Height = lay.Target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, BlocksDragArm = true, OnClick = onAdd,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Height = Z.PillH, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = new Edges4(Z.PillPadX, 0f, Z.PillPadX, 0f),
                Corners = CornerRadius4.All(Z.PillH / 2f), BorderWidth = Z.PillStroke, BorderColor = Tok.TextPrimary,
                HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed, HitTestVisible = false,
                Children =
                [
                    R.Type.DenseTitle(Loc.Get(Strings.Theme.Zune.Detail.Add)) with
                    {
                        Size = Z.PillWord, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                ],
            },
        ],
    };
}
