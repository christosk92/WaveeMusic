using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The page's masthead: what this release IS (pills + name + tagline), when it landed, and the two links out
/// (GitHub, copy). Everything here is document data — the hero never asks the update service anything, so it renders
/// identically for the running build, an older release the rail selected, and a beta the user is only reading about.</summary>
static class ReleaseNotesHero
{
    public static Element Create(ReleaseNotesDocument doc, bool isLatest, Action<string> openUrl, Action<string> copy)
    {
        string version = doc.Version;
        string tag = "wavee-v" + version;
        string release = doc.Links?.Release is { Length: > 0 } r ? r : ReleaseNotesText.ReleaseTagUrl(version);

        var pills = new List<Element>(3);
        if (isLatest) pills.Add(Pill(Loc.Get(Strings.WhatsNew.Latest), accent: true));
        pills.Add(Pill(Loc.Get(string.Equals(doc.Channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? Strings.WhatsNew.Beta : Strings.WhatsNew.Stable)));
        if (doc.PackageVersion is { Length: > 0 } quad) pills.Add(Pill(quad, mono: true));

        var meta = new List<Element>(3);
        if (ReleaseNotesText.Date(doc.Date) is { Length: > 0 } released)
            meta.Add(MetaItem(Loc.Get(Strings.WhatsNew.Released), released));
        if (doc.MinOs is { Length: > 0 } minOs)
            meta.Add(MetaItem(Loc.Get(Strings.WhatsNew.Requires), minOs));
        meta.Add(MetaItem(Loc.Get(Strings.WhatsNew.Tag), tag));

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = new Edges4(24f, 22f, 24f, 20f), Corners = CornerRadius4.All(12f),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = pills.ToArray() },
                        new TextEl(Headline(doc))
                            { Size = 30f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new TextEl(doc.Tagline)
                            { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 640f },
                        new BoxEl { Direction = 0, Gap = 14f, Wrap = true, Margin = new Edges4(0f, 4f, 0f, 0f), Children = meta.ToArray() },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.End,
                    Children =
                    [
                        Button.Create(Loc.Get(Strings.WhatsNew.OpenOnGitHub), () => openUrl(release),
                            ButtonAppearance.Standard, ControlSize.Small),
                        Button.Create(Loc.Get(Strings.WhatsNew.CopyLink), () => copy(release),
                            ButtonAppearance.Subtle, ControlSize.Small),
                    ],
                },
            ],
        };
    }

    /// <summary>"Wavee 0.3.0 “Crest”" — the codename is the thing people remember, so it is never dropped when
    /// present. A document with no codename uses the BARE key rather than printing empty quotes.
    /// <para>Both shapes are loc keys (<c>whatsNew.headline</c> / <c>whatsNew.headlineBare</c>) rather than string
    /// concatenation, because the quotation marks are typography: a locale that uses «» or „“ has to be able to say
    /// so, and a headline assembled in C# gives it nowhere to.</para></summary>
    static string Headline(ReleaseNotesDocument doc)
        => doc.Name is { Length: > 0 } name
            ? Strings.WhatsNew.Headline(doc.Version, name)
            : Strings.WhatsNew.HeadlineBare(doc.Version);

    internal static Element Pill(string text, bool accent = false, bool mono = false) => new BoxEl
    {
        Shrink = 0f, MinHeight = 22f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(9f, 2f, 9f, 2f), Corners = CornerRadius4.All(Radii.Pill),
        Fill = accent ? Tok.AccentDefault : Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(text)
            {
                Size = 11.5f, Weight = 600, MaxLines = 1,
                Color = accent ? Tok.TextOnAccentPrimary : Tok.TextSecondary,
                FontFamily = mono ? "Cascadia Code" : null,
            },
        ],
    };

    static Element MetaItem(string label, string value) => new BoxEl
    {
        Direction = 0, Gap = 5f, Shrink = 0f, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
            new TextEl(value) { Size = 12f, Color = Tok.TextTertiary },
        ],
    };
}
