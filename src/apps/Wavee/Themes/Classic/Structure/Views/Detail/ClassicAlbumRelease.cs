// ── Themes/Classic/Structure/Views/Detail/ClassicAlbumRelease.cs — Classic's album release views (T9b) ───────────────────────────────────────────
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

namespace Wavee.Themes.Classic;

/// <summary>Classic's album release views.</summary>
internal static class ClassicAlbumRelease
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
                Size = ClassicAlbumLayout.OutNowSize, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1,
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
                ProgressRing.Indeterminate(size: ClassicAlbumLayout.CountdownRing, isActive: !p.Released, foreground: p.RingAccent),
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

    static readonly string[] s_noteKeys = ["note:0", "note:1", "note:2", "note:3"];

    /// <summary>The registered template of <see cref="ViewKeys.AlbumReleasePanel"/>: the facts, whole. Song count and length are not painted — the hero
    /// meta line already states both. The surrounding detail frame owns the reveal: the panel must settle in place when publishing data lands (a second
    /// fade / stagger here made the album page flash and visibly jump after navigation).</summary>
    public static Element ReleasePanelTemplate(AlbumReleasePanelProps p, in ViewContext ctx)
    {
        // Songs and length never open the panel: the hero already prints both. A date, a label, the courtesy lines, or another edition do.
        int noteCount = (p.Label is not null ? 1 : 0) + p.Notes.Count;
        if (p.Released is null && noteCount == 0 && p.Versions.Count == 0) return new BoxEl();

        var children = new List<Element>(2);
        if (p.Released is not null || noteCount > 0)
        {
            // A section title, then the facts as a label/value list (Released, Label), then the ℗/© courtesy lines — one type ramp (14/20 values, 12/16
            // labels and notes), nothing crammed onto the title's line.
            var body = new List<Element>(4)
            {
                Ui.BodyStrong(Loc.Get(Strings.Detail.AboutRelease)) with { Key = "release-title", MinWidth = 0f },
            };
            var factRows = new List<Element>(2);
            if (p.Released is not null) factRows.Add(Fact("fact:released", p.ReleasedCaption, p.Released));
            if (p.Label is { } label) factRows.Add(Fact("fact:label", Loc.Get(Strings.Detail.FactLabel), label));
            if (factRows.Count > 0)
                body.Add(new BoxEl { Key = "release-facts", Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Children = factRows.ToArray() });
            if (p.Notes.Count > 0)
            {
                var notes = new Element[p.Notes.Count];
                for (int i = 0; i < notes.Length; i++)
                    notes[i] = Note(i < s_noteKeys.Length ? s_noteKeys[i] : "note:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Notes[i]);
                body.Add(new BoxEl { Key = "release-notes", Direction = 1, Gap = ClassicAlbumLayout.NotesGap, MinWidth = 0f, Children = notes });
            }
            children.Add(new BoxEl
            {
                Key = "release-about", Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = body.ToArray(),
            });
        }
        if (p.Versions.Count > 0) children.Add(OtherVersions(p.Versions));

        return new BoxEl
        {
            Key = "release-panel",
            Direction = 1, Gap = Spacing.M,
            Padding = p.OuterPadding
                ? new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.L)
                : Edges4.All(0f),
            Children = children.ToArray(),
        };
    }

    /// <summary>One fact: a tertiary caption label and its value in the body rung, the value wrapping (a long label name or date never ellipsises in the
    /// narrow rail). The label column is wide enough for "Released" / "Label" in the caption rung, so the values start on one line; a longer localized
    /// label simply pushes its own value (Shrink 0), never truncates.</summary>
    static Element Fact(string key, string label, string value) => new BoxEl
    {
        Key = key, Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start, MinWidth = 0f,
        Children =
        [
            Ui.Caption(label).Tertiary() with
            {
                MinWidth = ClassicAlbumLayout.FactLabelW, Shrink = 0f, Margin = new Edges4(0f, ClassicAlbumLayout.FactLabelNudge, 0f, 0f),
            },
            Ui.Body(value).Secondary() with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    static Element Note(string key, string text) => new BoxEl
    {
        Key = key, Direction = 1, MinWidth = 0f,
        Children =
        [
            Ui.Caption(text).Tertiary() with
            {
                MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 4, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    /// <summary>W15: "Name · Year · KIND" per edition; choosing one opens that album.</summary>
    static Element OtherVersions(IReadOnlyList<AlbumVersionChoice> versions)
    {
        var items = new MenuFlyoutItem[versions.Count];
        for (int i = 0; i < items.Length; i++)
            items[i] = new MenuFlyoutItem(versions[i].Label, Icons.MusicNote, true, versions[i].Open);
        return new BoxEl
        {
            Key = "release-versions",
            Direction = 0, Padding = new Edges4(0f, ClassicAlbumLayout.VersionsPadY, 0f, ClassicAlbumLayout.VersionsPadY),
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
        Padding = new Edges4(ClassicAlbumLayout.UnresolvedPadX, ClassicAlbumLayout.UnresolvedPadY, ClassicAlbumLayout.UnresolvedPadX,
            ClassicAlbumLayout.UnresolvedPadY),
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.Empty.NoTracks))
            {
                Size = ClassicAlbumLayout.UnresolvedSize, LineHeight = ClassicAlbumLayout.UnresolvedLine, Color = Tok.TextTertiary, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };
}
