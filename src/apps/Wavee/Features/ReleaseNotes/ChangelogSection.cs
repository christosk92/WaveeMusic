using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One <c>### Added</c> / <c>### Fixed</c> group: a coloured kind badge, the title, the count, and the rows.
///
/// <para>A Component (not a static builder) for exactly one reason: "Show all 14" is LOCAL state. Fourteen fix lines
/// would otherwise push the highlights and the next section off the first screen of every release, which is the one
/// thing a release-notes page must not do. Eight rows is the fold; the rest are one click away and never collapse
/// again while the page is open.</para>
///
/// <para>Props are RE-PUSHED (<c>Embed.Comp(props, …)</c>), never frozen ctor args: the issue states arrive after the
/// document does (a budgeted GitHub fetch), so a frozen section would show the release tool's snapshot forever.</para></summary>
sealed class ChangelogSection : Component
{
    /// <summary>Live props. <paramref name="States"/> is replaced wholesale when the issue refresh lands, so record
    /// equality sees the change and the chips re-render.</summary>
    public sealed record Props(ReleaseSection Section, IssueStateCache? States, Action<string> OpenUrl);

    const int Fold = 8;

    readonly Signal<bool> _all = new(false);

    /// <summary>Build one section. <paramref name="key"/> must be unique across the whole page (release + kind), or two
    /// stacked releases would share one collapse state.</summary>
    public static Element Create(ReleaseSection section, IssueStateCache? states, Action<string> openUrl, string key)
        => Embed.Comp(new Props(section, states, openUrl), () => new ChangelogSection()) with { Key = key };

    public override Element Render()
    {
        var p = UseProps<Props>();
        ReleaseItem[] items = p.Section.Items;
        bool all = _all.Value;                       // subscribe → "Show all" expands in place
        int shown = all || items.Length <= Fold ? items.Length : Fold;

        var rows = new List<Element>(shown * 2);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, AlignSelf = FlexAlign.Stretch });
            rows.Add(ChangelogItem.Create(items[i], p.States, p.OpenUrl));
        }

        var (glyph, ink, wash) = Badge(p.Section.Kind);
        var header = new List<Element>(4)
        {
            new BoxEl
            {
                Width = 22f, Height = 22f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(6f), Fill = wash,
                Children = [ new TextEl(glyph) { Size = 12f, Weight = 700, Color = ink } ],
            },
            new TextEl(Title(p.Section.Kind)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
            new TextEl(items.Length.ToString(CultureInfo.InvariantCulture)) { Size = 12f, Color = Tok.TextTertiary, Grow = 1f },
        };
        if (!all && items.Length > Fold)
            header.Add(Button.Create(Strings.WhatsNew.ShowAll(items.Length), () => _all.Value = true,
                ButtonAppearance.Subtle, ControlSize.Small) with { Shrink = 0f });

        return new BoxEl
        {
            Direction = 1, Gap = 6f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinHeight = 28f, Children = header.ToArray() },
                new BoxEl
                {
                    Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                    Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                    Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Children = rows.ToArray(),
                },
            ],
        };
    }

    /// <summary>The kind's glyph + ink + wash. Plain characters, not the icon font: "+ ~ ✓ − !" is what a changelog
    /// reads as, and the icon font has no arithmetic glyphs at this weight.</summary>
    static (string Glyph, ColorF Ink, ColorF Wash) Badge(string? kind) => kind switch
    {
        "added" => ("+", Tok.SystemFillSuccess, Tok.SystemFillSuccessBackground),
        "changed" => ("~", Tok.AccentTextPrimary, Tok.AccentSubtle),
        "fixed" => ("✓", Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
        "removed" => ("−", Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
        "deprecated" => ("!", Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
        "security" => ("!", Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
        _ => ("!", Tok.TextTertiary, Tok.FillSubtleSecondary),        // known limitations
    };

    public static string Title(string? kind) => Loc.Get(kind switch
    {
        "added" => Strings.WhatsNew.Section.Added,
        "changed" => Strings.WhatsNew.Section.Changed,
        "fixed" => Strings.WhatsNew.Section.Fixed,
        "removed" => Strings.WhatsNew.Section.Removed,
        "deprecated" => Strings.WhatsNew.Section.Deprecated,
        "security" => Strings.WhatsNew.Section.Security,
        _ => Strings.WhatsNew.Section.Known,
    });
}
