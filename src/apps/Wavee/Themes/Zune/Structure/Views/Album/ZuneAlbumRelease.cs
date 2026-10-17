// ── Themes/Zune/Structure/Views/Album/ZuneAlbumRelease.cs — Zune's album attribution and release views (themes plan 6.3; P5 Z6a1) ───────────────
//
// Z-Album's identity column: the billed artists as ONE link run in the artist voice (18/24; the faces are the page's photos elsewhere, the
// line keeps the names), opening the every-artist flyout where more than one is billed; the release's facts as the column's quiet foot (the
// released date and the label in the meta voice, the ℗ / © courtesy lines in the 12/18 tertiary voice, Z-Album) with the other editions as
// link words; a release not out yet counts down in BIG NUMERALS (the numeral roles: the style's "big numerals" option moves them to the body rung)
// under the accent "coming soon"; an unresolvable prerelease is one centred line. The hosts (`Album.FacePileHost`, `Album.CountdownHost`,
// `Album.ReleasePanelHost`) keep every decision; these draw what they were handed.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Z = Wavee.Themes.Zune.ZuneDetailRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's album attribution and release views.</summary>
internal static class ZuneAlbumRelease
{
    // ── the artist line ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.AlbumArtistLine"/>: the names as one trigger (the flyout's chevron where it opens a
    /// list); an album with no resolvable artist draws an empty box. The host's width mode is honoured as the line's measure (the names survive).</summary>
    public static Element LineTemplate(AlbumArtistLineProps p, in ViewContext ctx)
    {
        if (p.Line is not { } s) return new BoxEl();
        var lay = ZuneDetailTiers.Of(ctx.Tier());
        bool live = s.Click is not null;
        var kids = new List<Element>(2) { K.ArtistText(s.Names, s.Width, lay) with { HoverColor = live ? Tok.AccentTextPrimary : default } };
        if (s.Menu) kids.Add(Icon(Icons.ChevronDownSmall, Z.StripChevron, Tok.TextTertiary));
        Element trigger = new BoxEl
        {
            Key = Z.ArtistLineKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.HeroIdentityGap, MinWidth = 0f, MaxWidth = s.Width,
            Height = Z.HitHeight(ctx.Tier().Window, lay.ArtistLine), Shrink = 1f,
            Role = !live ? AutomationRole.None : s.Menu ? AutomationRole.Button : AutomationRole.Hyperlink,
            Focusable = live, Cursor = live ? CursorId.Hand : (CursorId?)null,
            OnClick = s.Click, OnKeyDown = s.OnKey, OnRealized = s.OnRealized,
            Children = kids.ToArray(),
        };
        return s.Menu ? ToolTip.Wrap(trigger, s.Tip) : trigger;
    }

    /// <summary>The registered template of <see cref="ViewKeys.AlbumArtistFlyout"/>: 44 rows (the 32 portrait and the name) in a scrolling list.</summary>
    public static Element FlyoutTemplate(PersonFlyoutProps p, in ViewContext ctx)
    {
        var rows = new Element[p.Rows.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = p.Rows[i];
            bool live = row.Go is not null;
            rows[i] = new BoxEl
            {
                Key = "flyout:" + i.ToString(CultureInfo.InvariantCulture), Direction = 0, Height = Z.FlyoutRowH, AlignItems = FlexAlign.Center,
                Gap = Z.FlyoutRowGap, Padding = new Edges4(Z.FlyoutPad, 0f, Z.FlyoutPad, 0f), MinWidth = 0f,
                HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
                Role = AutomationRole.MenuItem, Focusable = true, Cursor = live ? CursorId.Hand : (CursorId?)null, OnClick = row.Go,
                Children =
                [
                    PersonPicture.Create("", Z.FlyoutPortrait, displayName: row.Name, imageSourcePath: row.ImageUrl),
                    R.Type.TrackTitle(row.Name) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            };
        }
        return new BoxEl
        {
            Direction = 1, Width = Z.FlyoutW, MaxHeight = Z.FlyoutMaxH, Padding = Edges4.All(Z.FlyoutPad),
            Children =
            [
                ScrollView(new BoxEl { Direction = 1, MinWidth = 0f, Children = rows }) with
                {
                    MaxHeight = Z.FlyoutMaxH - 2f * Z.FlyoutPad, ContentSized = true, AutoEdgeFade = true, Grow = 0f,
                },
            ],
        };
    }

    // ── the countdown ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.AlbumCountdown"/>: the accent "coming soon" over the four units in the large numeral
    /// (days, hours, minutes, seconds), or "out now" once the instant passed. The accent is the host's thunk, bound into the colour channel (a late
    /// grading re-tints it without a render). Bare: the units alone (an artist page's panel carries its own eyebrow).</summary>
    public static Element CountdownTemplate(AlbumCountdownProps p, in ViewContext ctx)
    {
        Element body;
        if (p.Released)
            body = K.Heading(Loc.Get(Strings.Detail.PreReleaseOut)) with { Color = Tok.TextPrimary };
        else
        {
            var left = p.Left < TimeSpan.Zero ? TimeSpan.Zero : p.Left;
            body = new BoxEl
            {
                Direction = 0, Gap = Z.CountdownGap, MinWidth = 0f, Wrap = true,
                Children =
                [
                    Unit("days", left.Days, Strings.Detail.PreReleaseUnitDays),
                    Unit("hours", left.Hours, Strings.Detail.PreReleaseUnitHours),
                    Unit("minutes", left.Minutes, Strings.Detail.PreReleaseUnitMinutes),
                    Unit("seconds", left.Seconds, Strings.Detail.PreReleaseUnitSeconds),
                ],
            };
        }
        if (p.Bare) return body;
        var accent = p.Accent;
        return new BoxEl
        {
            Key = Z.CountdownKey, Direction = 1, Gap = Z.CountdownUnitGap, MinWidth = 0f,
            Children =
            [
                R.Type.Eyebrow(Loc.Get(Strings.Detail.PreReleaseEyebrow)) with
                {
                    Color = Prop.Of(accent), Case = R.Case.For(LabelKind.Label), MaxLines = 1,
                },
                body,
            ],
        };
    }

    static Element Unit(string key, int value, string unitKey) => new BoxEl
    {
        Key = "unit:" + key, Direction = 1, Gap = Z.CountdownUnitGap, Shrink = 0f,
        Children =
        [
            R.Type.NumeralLarge(value.ToString("00", CultureInfo.CurrentCulture)) with { Color = Tok.TextPrimary, MaxLines = 1 },
            R.Type.MicroMeta(Loc.Get(unitKey)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 },
        ],
    };

    // ── about this release ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.AlbumReleasePanel"/>: the facts, whole (the panel appears once, complete): the released
    /// date and the label in the meta voice, the courtesy lines in the tertiary notes voice, the other editions as link words. Nothing when there is
    /// nothing to say. With outer padding (the vertical arm's body under the rows) it stands on the frame's gutter.</summary>
    public static Element ReleasePanelTemplate(AlbumReleasePanelProps p, in ViewContext ctx)
    {
        int noteCount = (p.Label is not null ? 1 : 0) + p.Notes.Count;
        if (p.Released is null && noteCount == 0 && p.Versions.Count == 0) return new BoxEl();
        var facts = ctx.Tier();
        var lay = ZuneDetailTiers.Of(facts);
        var kids = new List<Element>(4);
        if (p.Released is { } released) kids.Add(Fact("fact:released", p.ReleasedCaption, released, lay));
        if (p.Label is { } label) kids.Add(Fact("fact:label", Loc.Get(Strings.Detail.FactLabel), label, lay));
        for (int i = 0; i < p.Notes.Count; i++)
            kids.Add(new BoxEl
            {
                Key = "note:" + i.ToString(CultureInfo.InvariantCulture), Direction = 1, MinWidth = 0f,
                Children =
                [
                    R.Type.MicroMeta(p.Notes[i]) with
                    {
                        Size = lay.NotesSize, LineHeight = lay.NotesLine, Color = Tok.TextTertiary, MinWidth = 0f,
                        Wrap = TextWrap.WrapWholeWords, MaxLines = Z.DescriptionLines + 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            });
        if (p.Versions.Count > 0)
        {
            var links = new Element[p.Versions.Count + 1];
            links[0] = R.Type.DenseMeta(Loc.Get(Strings.Detail.OtherVersions)) with
            {
                Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1,
            };
            for (int i = 0; i < p.Versions.Count; i++)
                links[i + 1] = VersionLink("version:" + i.ToString(CultureInfo.InvariantCulture), p.Versions[i]);
            kids.Add(new BoxEl { Key = "release:versions", Direction = 1, Gap = Z.CountdownUnitGap, MinWidth = 0f, Children = links });
        }
        float gutter = ZuneTiers.FrameOf(facts).Gutter;
        return new BoxEl
        {
            Key = Z.ReleaseKey, Direction = 1, Gap = Z.HeroIdentityGap, MinWidth = 0f,
            Padding = p.OuterPadding ? new Edges4(gutter, Z.SectionTop, gutter, 0f) : Edges4.All(0f),
            Children = kids.ToArray(),
        };
    }

    static Element Fact(string key, string caption, string value, ZuneDetailLayout lay) => new BoxEl
    {
        Key = key, Direction = 0, Wrap = true, Gap = Z.HeroIdentityGap, MinWidth = 0f,
        Children =
        [
            R.Type.DenseMeta(caption) with { Size = lay.NotesSize, LineHeight = lay.NotesLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f },
            R.Type.DenseMeta(value) with
            {
                Size = lay.NotesSize, LineHeight = lay.NotesLine, Color = Tok.TextSecondary, MinWidth = 0f, Shrink = 1f,
                Wrap = TextWrap.WrapWholeWords, MaxLines = Z.RailTitleLines, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    /// <summary>One other edition as a link word ("Name · Year · KIND": a name, never re-cased).</summary>
    static Element VersionLink(string key, AlbumVersionChoice v) => new BoxEl
    {
        Key = key, Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = v.Open,
        Children =
        [
            R.Type.DenseMeta(v.Label) with
            {
                Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        ],
    };

    // ── nothing here yet ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.AlbumUnresolved"/>: a centred "nothing here yet" in the heading voice, lowercased.</summary>
    public static Element UnresolvedTemplate(AlbumUnresolvedProps p, in ViewContext ctx) => new BoxEl
    {
        Key = Z.UnresolvedKey, Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Z.UnresolvedPadX, Z.UnresolvedPadY, Z.UnresolvedPadX, Z.UnresolvedPadY),
        Children = [K.Heading(Loc.Get(Strings.Detail.Empty.NoTracks)) with { Color = Tok.TextTertiary }],
    };
}
