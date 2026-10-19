// ── Themes/WinUi/Structure/Views/Detail/WinUiAlbumRelease.cs — WinUI's album release views (T9b) ───────────────────────────────────────────
//
// From `Entities\Album.UI.cs` §2 (the countdown card) and `Entities\Album.Page.cs` §2 (About this release) and `NothingHereYet`:
//
//   · the prerelease COUNTDOWN (W13): ring + "Coming soon" in the page accent + four unit tiles, or "Out now" once the instant passes (the ring goes inactive,
//     nothing refetches). `Album.CountdownHost` keeps the clock (ONE wall-clock sample, then the frame clock's own delta: the stuck-timer fix);
//   · ABOUT THIS RELEASE (W11, W15): ONE shape from its first paint, an "About this release" title (14/20 semibold), a label/value facts list (Released, Label:
//     caption labels, body values that wrap), the ℗ / © lines as caption notes, and the Other versions picker. `Album.ReleasePanelHost` keeps the gate (the
//     stamp over the album row, the tracklist and the versions edges) and reads the facts;
//   · W18's unresolvable prerelease: 14 px tertiary, centred, 16/24 padding.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's album release views.</summary>
internal static class WinUiAlbumRelease
{
    // ══ 1. THE COUNTDOWN ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.AlbumCountdown"/>. The accent is NOT a tracked read of this render (W2-A2): the eyebrow
    /// binds the thunk into its Color channel (a mount-time effect that subscribes the cover's per-image palette signal and writes one scene column when
    /// it grades), and the ring, whose foreground is a scalar, is handed the value the host peeked untracked this second.</summary>
    public static Element CountdownTemplate(AlbumCountdownProps p, in ViewContext ctx)
    {
        Element body = p.Released
            ? new TextEl(Loc.Get(Strings.Detail.PreReleaseOut))
            {
                Size = WinUiAlbumLayout.OutNowSize, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1,
                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            }
            : Controls.PreReleaseCountdown(p.Left);
        if (p.Bare) return body;

        var accent = p.Accent;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
            Padding = Edges4.All(Spacing.M), Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                // isActive: !released — the ring's own Inactive state fades it; it is never removed.
                ProgressRing.Indeterminate(size: WinUiAlbumLayout.CountdownRing, isActive: !p.Released, foreground: p.RingAccent),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Grow = 1f,
                    Children =
                    [
                        R.Type.Eyebrow(Loc.Get(Strings.Detail.PreReleaseEyebrow)) with { Color = Prop.Of(accent), MaxLines = 1 },
                        body,
                    ],
                },
            ],
        };
    }

    // ══ 2. ABOUT THIS RELEASE ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.AlbumReleasePanel"/> (W-Album-Huge "About this album"): a Subtitle title over a card (r8, the card
    /// fill and stroke) of 44-tall rows, each a 12 key in a 120 lane and a 14 value: Released, Label and the courtesy lines as "Copyright". Song count and length
    /// are not painted: the hero's meta states both. The Other versions picker follows the card.</summary>
    public static Element ReleasePanelTemplate(AlbumReleasePanelProps p, in ViewContext ctx)
    {
        int noteCount = p.Notes.Count;
        if (p.Released is null && p.Label is null && noteCount == 0 && p.Versions.Count == 0) return new BoxEl();

        var rows = new List<Element>(4);
        if (p.Released is not null) rows.Add(Fact("fact:released", p.ReleasedCaption, p.Released, first: rows.Count == 0));
        if (p.Label is { } label) rows.Add(Fact("fact:label", Loc.Get(Strings.Detail.FactLabel), label, first: rows.Count == 0));
        for (int i = 0; i < noteCount; i++)
            rows.Add(Fact("fact:note:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                i == 0 ? Loc.Get(Strings.Theme.Winui.Detail.Copyright) : "", p.Notes[i], first: rows.Count == 0));

        var children = new List<Element>(3);
        if (rows.Count > 0)
            children.Add(new BoxEl
            {
                Key = "release-about", Direction = 1, Gap = WinUiDetailRules.AsideTitleGap, MinWidth = 0f,
                Children =
                [
                    R.Type.RailHeader(Loc.Get(Strings.Theme.Winui.Detail.AboutAlbum)) with { Key = "release-title", MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl
                    {
                        Key = "release-facts", Direction = 1, MinWidth = 0f, Corners = CornerRadius4.All(WinUiDetailRules.CardRadius), ClipToBounds = true,
                        Fill = Prop.Of(static () => Tok.FillCardDefault), BorderWidth = 1f, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
                        Children = rows.ToArray(),
                    },
                ],
            });
        if (p.Versions.Count > 0) children.Add(OtherVersions(p.Versions));

        return new BoxEl
        {
            Key = "release-panel", Direction = 1, Gap = Spacing.M, Padding = p.OuterPadding ? SectionPad : Edges4.All(0f), Children = children.ToArray(),
        };
    }

    static Edges4 SectionPad => new(0f, WinUiDetailRules.BandSectionGap, 0f, 0f);

    /// <summary>One fact row: the key (12, Text 3) in its 120 lane, the value (14) wrapping, 44 tall at least, a hairline over every row but the first.</summary>
    static Element Fact(string key, string label, string value, bool first)
    {
        Element row = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiDetailRules.FactGap, MinWidth = 0f, MinHeight = WinUiDetailRules.FactRowMin,
            Padding = new Edges4(WinUiDetailRules.FactPadX, 0f, WinUiDetailRules.FactPadX, 0f),
            Children =
            [
                new BoxEl { Width = WinUiDetailRules.FactKeyWidth, Shrink = 0f, Children = [new TextEl(label) { Size = WinUiDetailRules.FactKeySize, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }] },
                new TextEl(value)
                {
                    Size = WinUiDetailRules.FactValueSize, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
        return new BoxEl
        {
            Key = key, Direction = 1, MinWidth = 0f,
            Children = first ? [row] : [new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) }, row],
        };
    }

    /// <summary>W15: "Name · Year · KIND" per edition; choosing one opens that album.</summary>
    static Element OtherVersions(IReadOnlyList<AlbumVersionChoice> versions)
    {
        var items = new MenuFlyoutItem[versions.Count];
        for (int i = 0; i < items.Length; i++)
            items[i] = new MenuFlyoutItem(versions[i].Label, Icons.MusicNote, true, versions[i].Open);
        return new BoxEl
        {
            Key = "release-versions",
            Direction = 0, Padding = new Edges4(0f, WinUiAlbumLayout.VersionsPadY, 0f, WinUiAlbumLayout.VersionsPadY), AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f,
                    Children = [DropDownButton.Create(Loc.Get(Strings.Detail.OtherVersions), items, Icons.MusicNote)],
                },
            ],
        };
    }

    // ══ 3. NOTHING HERE YET ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.AlbumUnresolved"/>: W18's unresolvable prerelease, a centred "nothing here yet" line.</summary>
    public static Element UnresolvedTemplate(AlbumUnresolvedProps p, in ViewContext ctx) => new BoxEl
    {
        Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WinUiAlbumLayout.UnresolvedPadX, WinUiAlbumLayout.UnresolvedPadY, WinUiAlbumLayout.UnresolvedPadX,
            WinUiAlbumLayout.UnresolvedPadY),
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.Empty.NoTracks))
            {
                Size = WinUiAlbumLayout.UnresolvedSize, LineHeight = WinUiAlbumLayout.UnresolvedLine, Color = Tok.TextTertiary, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };
}
