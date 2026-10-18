// ── Themes/Zune/Structure/Views/Episode/ZuneEpisodeDiscussion.cs — Zune's discussion views (themes plan 6.3; P5 Z6b2) ─────────────────────
//
// From `Entities\Episode.Discussion.cs` (`PodcastReaderUI`): the PICTURES of the episode's comments — the thread (heading, comment cards, the
// quiet lines, "Load more", the composer), one comment card (the avatar / content grid, the reaction pill, Reply, the replies disclosure and the
// reply thread's rail), the loading seed, the reaction flyout (the palette, the roster's filter chips and the people). The discussion's state
// stays core (`Discussion`, `CommentPage`, `CommentCard`, `ReactionFlyout` and their signals, resources, composer and reaction verbs, the
// reaction session, the overlay), and so do the pure decisions (`DiscussionLayout`, `PodcastReactionRules.Pill`).
//
// ZUNE'S COMMENT: flat on the page's ground (no plates, no radii), the body at 15/22, the name in 600 as written, the reaction pill and the
// replies disclosure square washes that take the accent when the viewer has reacted. The words the theme writes (the heading, the pinned and
// pending marks, the quiet lines) are lowered by the case roles; a name and a comment's text are never re-cased.
//
// ONE LEFT EDGE (podcast-episode-peek §3). `Frame` IS the comment's grid: an avatar column and one content column, and every row the card
// builds — the name / timestamp line, the body, the reaction / Reply row, the replies disclosure — is a child of that content column.
// Nothing under the name gets an inset of its own: only the avatar sits outside the edge the rest of the comment shares. The live card and the
// loading seed are the SAME frame, so a hydrating thread never re-measures.

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
using K = Wavee.Themes.Zune.ZuneDetailKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's discussion views.</summary>
internal static class ZuneEpisodeDiscussion
{
    static readonly TemplateParts ConsentParts = ConsentTemplate();

    static TemplateParts ConsentTemplate()
    {
        var parts = new TemplateParts
        {
            [CheckBox.PartRoot] = root => root with { MinWidth = 0, AlignItems = FlexAlign.Start },
            [CheckBox.PartBox] = box => box with { Shrink = 0 },
        };
        parts.Set<TextEl>(CheckBox.PartLabel, label => label with
        {
            Grow = 1, Basis = 0, Shrink = 1, MinWidth = 0, Wrap = TextWrap.Wrap, MaxLines = ZuneEpisodeRules.ConsentLines,
            Size = ZuneEpisodeRules.ConsentSize, LineHeight = ZuneEpisodeRules.ConsentLine, Color = Tok.TextSecondary,
        });
        return parts;
    }

    /// <summary>The loading seed's comments: placeholder text in the live card's frame.</summary>
    static DiscussionComment[] Seed { get; } =
    [
        new("seed:1", "A thoughtful conversation about this episode.", "Listener", "", "", false, false, false, 0, [], false),
        new("seed:2", "Thank you for sharing these ideas.", "Listener", "", "", false, false, false, 0, [], false),
    ];

    // ══ 1. THE THREAD ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeDiscussion"/>.</summary>
    public static Element ThreadTemplate(DiscussionThreadProps p, in ViewContext ctx)
    {
        var rows = new List<Element>();
        if (p.Heading is { } heading) rows.Add(K.Heading(heading));
        rows.AddRange(p.Comments);
        if (p.Empty is { } empty) rows.Add(ZuneEpisode.Quiet(empty));
        if (p.More is { } more) rows.Add(more);
        if (p.Composer is { } composer)
        {
            var kids = new List<Element>
            {
                TextBox.Create(composer.Text, options: new TextBox.TextBoxOptions
                {
                    Placeholder = composer.Placeholder, Width = float.NaN, AcceptsReturn = true, Height = ZuneEpisodeRules.ComposerHeight,
                }),
            };
            if (composer.ShowConsent)
                kids.Add(CheckBox.Create(Loc.Get(Strings.Podcast.Reader.Consent), composer.Consent,
                    onChange: composer.OnConsent, parts: ConsentParts));
            kids.Add(Button.Standard(composer.SubmitLabel, composer.Submit, isEnabled: composer.CanSubmit) with { AlignSelf = FlexAlign.Start });
            rows.Add(new BoxEl
            {
                Direction = 1, MinWidth = 0, Gap = Spacing.M, Margin = new Edges4(0, Spacing.M, 0, 0),
                Children = kids.ToArray(),
            });
        }
        else if (p.Closed is { } closed) rows.Add(ZuneEpisode.Quiet(closed));
        if (p.Error is { } error) rows.Add(ZuneEpisode.Quiet(error));
        return p.Continuation
            ? new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, Children = rows.ToArray() }
            : new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, MaxWidth = ZuneEpisodeRules.ThreadMeasure, Children = rows.ToArray() };
    }

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeLoadMore"/>.</summary>
    public static Element LoadMoreTemplate(LoadMoreProps p, in ViewContext ctx)
    {
        var button = Button.Subtle(Loc.Get(Strings.Podcast.Reader.LoadMore), p.Click);
        return p.AlignStart ? button with { AlignSelf = FlexAlign.Start } : button;
    }

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeCommentSeed"/>: the placeholder comments, in the live frame.</summary>
    public static Element SeedTemplate(CommentSeedProps p, in ViewContext ctx)
    {
        var frames = new Element[Seed.Length];
        for (int i = 0; i < frames.Length; i++) frames[i] = Frame(Seed[i], p.Reply, [CommentText(Seed[i].Text)]);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0, MaxWidth = ZuneEpisodeRules.ThreadMeasure, Children = frames,
        };
    }

    // ══ 2. A COMMENT ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element CommentText(string text) => new TextEl(text)
    {
        Size = ZuneEpisodeRules.CommentSize, LineHeight = ZuneEpisodeRules.CommentLine, Wrap = TextWrap.Wrap, MinWidth = 0,
        Color = Tok.TextPrimary,
    };

    /// <summary>A small mark after the name (pinned by the show, awaiting review): the micro voice, lowered as a label, the accent where it earns it.</summary>
    static Element Mark(string text, bool accent) => R.Type.MicroMeta(text) with
    {
        Color = accent ? Prop.Of(static () => R.Ink.AccentDecor) : (Prop<ColorF>)Tok.TextTertiary, Case = R.Case.For(LabelKind.Label),
        MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0,
    };

    static Element Frame(DiscussionComment c, bool reply, Element[] body)
    {
        float avatar = reply ? ZuneEpisodeRules.ReplyAvatar : ZuneEpisodeRules.ThreadAvatar;
        var heading = new List<Element>
        {
            new TextEl(c.Author.Length == 0 ? Loc.Get(Strings.Podcast.Reader.Anonymous) : c.Author)
            {
                Size = ZuneEpisodeRules.AuthorSize, LineHeight = ZuneEpisodeRules.AuthorLine, Weight = 600, Color = Tok.TextPrimary,
                Wrap = TextWrap.Wrap, MinWidth = 0,
            },
        };
        if (DateTimeOffset.TryParse(c.Created, out var date))
            heading.Add(new TextEl(date.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture))
                { Size = ZuneEpisodeRules.DateSize, LineHeight = ZuneEpisodeRules.AuthorLine, Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap });
        if (c.Pinned) heading.Add(Mark(Loc.Get(Strings.Podcast.Reader.Pinned), accent: true));
        if (c.Pending) heading.Add(Mark(Loc.Get(Strings.Podcast.Reader.Pending), accent: false));
        var content = new List<Element>
        {
            new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0, Children = heading.ToArray() },
        };
        content.AddRange(body);
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, MinWidth = 0, AlignItems = FlexAlign.Start,
            Padding = new Edges4(0, Spacing.S, 0, Spacing.S), Children =
            [
                PersonPicture.Create("", avatar, displayName: c.Author, imageSourcePath: c.Avatar) with { Shrink = 0 },
                new BoxEl { Direction = 1, Grow = 1, Basis = 0, MinWidth = 0, Gap = Spacing.S, Children = content.ToArray() },
            ],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeCommentCard"/>.</summary>
    public static Element CardTemplate(CommentCardProps p, in ViewContext ctx)
    {
        var c = p.Comment;
        var rows = new List<Element>();
        if (c.Sensitive && !p.Revealed)
            rows.Add(Button.Subtle(Loc.Get(Strings.Podcast.Reader.Reveal), p.Reveal) with { AlignSelf = FlexAlign.Start });
        else rows.Add(CommentText(c.Text));
        var actions = new List<Element>
        {
            ToolTip.Wrap(ReactionPill(p.Pill, p.OpenReactions) with { OnRealized = p.PillRealized }, Loc.Get(Strings.Podcast.Reader.Reactions)),
        };
        // Reply is the VERB, with no count in it — the count belongs to the disclosure below. It opens the thread
        // (which is where the reply composer lives); the disclosure toggles the same thread for reading.
        if (!p.Reply)
            actions.Add(Button.Subtle(Loc.Get(Strings.Podcast.Reader.Reply), p.OpenReplies, isEnabled: !c.ReplyLimit));
        rows.Add(new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, Wrap = true, MinWidth = 0,
            AlignItems = FlexAlign.Center, Children = actions.ToArray(),
        });
        if (DiscussionLayout.ShowReplies(p.Reply, c.Replies)) rows.Add(RepliesDisclosure(c, p.RepliesOpen, p.ToggleReplies));
        if (p.Thread is { } thread) rows.Add(ReplyThread(thread));
        return Frame(c, p.Reply, rows.ToArray());
    }

    static Element ReplyThread(Element content) => new BoxEl
    {
        Key = "replies", Animate = Design.Reveal.Drawer,
        Direction = 0, MinWidth = 0, Gap = Spacing.M, Margin = new Edges4(0, Spacing.S, 0, 0), Children =
        [
            new BoxEl { Width = 1, Shrink = 0, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
            new BoxEl { Direction = 1, Grow = 1, Basis = 0, MinWidth = 0, Children = [content] },
        ],
    };

    /// <summary>ONE reaction pill (podcast-episode-peek §3), replacing the bare outline heart and its number: a
    /// rounded filled STANDARD-radius plate carrying the emoji people ACTUALLY used — up to three, most-used first,
    /// overlapping — and the total after them, tinted with the accent when you are one of them, reading "React" with
    /// a single neutral face when there are none, and opening the picker either way.
    /// <para>Which emoji, in what order, what the label says and whether the pill reads as pressed is
    /// <see cref="PodcastReactionRules.Pill"/>'s decision, not this builder's.</para>
    /// Zune's is the control shape (square) on the 5 % wash; its accent-pressed fill is a page-specific state this custom shape owns (not a stock
    /// <c>ToggleButton</c>: the emoji cluster and the overlap margins have no slot on one).</para></summary>
    static BoxEl ReactionPill(ReactionPillModel pill, Action open)
    {
        var glyphs = new Element[pill.Emoji.Length];
        for (int i = 0; i < glyphs.Length; i++)
            glyphs[i] = new TextEl(pill.Emoji[i])
            {
                Size = ZuneEpisodeRules.PillEmojiSize, LineHeight = ZuneEpisodeRules.PillEmojiLine, Shrink = 0,
                Margin = new Edges4(0, 0, i == glyphs.Length - 1 ? 0 : ZuneEpisodeRules.PillEmojiOverlap, 0),
            };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS), Corners = R.Shape.All(ShapeRole.Control),
            Fill = pill.Pressed ? R.Ink.AccentAction with { A = ZuneEpisodeRules.PillPressedAlpha } : Tok.FillSubtleSecondary,
            HoverFill = pill.Pressed ? R.Ink.AccentAction with { A = ZuneEpisodeRules.PillPressedHoverAlpha } : Tok.FillSubtleTertiary,
            PressedFill = pill.Pressed ? R.Ink.AccentAction with { A = ZuneEpisodeRules.PillPressedDownAlpha } : Tok.FillSubtleSecondary,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = open,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0, Children = glyphs },
                new TextEl(pill.CallToAction ? Loc.Get(Strings.Podcast.React) : FormatCache.Int(pill.Total))
                {
                    Size = ZuneEpisodeRules.PillSize, LineHeight = ZuneEpisodeRules.PillLine, Weight = 600, Shrink = 0,
                    Color = pill.Pressed ? R.Ink.AccentDecor : Tok.TextSecondary,
                },
            ],
        };
    }

    /// <summary>The replies DISCLOSURE (podcast-episode-peek §3) under the pill row, on the comment's one left edge:
    /// up to three repliers' avatars stacked with overlap, "N replies", and a chevron that expands the thread in
    /// place. The count lives here and nowhere else — "Reply" beside the pill is a plain verb with no number in it.
    /// The stack is skipped entirely when the answer carried no replier avatars, rather than inventing faces.</summary>
    static Element RepliesDisclosure(DiscussionComment c, Func<bool> expanded, Action toggle)
    {
        int faces = DiscussionLayout.ReplyAvatarCount(c.ReplyAvatars.Length, c.Replies);
        var row = new List<Element>(3);
        if (faces > 0)
        {
            var stack = new Element[faces];
            for (int i = 0; i < faces; i++)
                stack[i] = PersonPicture.Create("", ZuneEpisodeRules.ReplyStackAvatar, imageSourcePath: c.ReplyAvatars[i]) with
                { Shrink = 0, Margin = new Edges4(0, 0, i == faces - 1 ? 0 : ZuneEpisodeRules.ReplyStackOverlap, 0) };
            row.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0, Children = stack });
        }
        row.Add(new TextEl(Strings.Podcast.RepliesCount(c.Replies))
            { Size = ZuneEpisodeRules.DisclosureSize, LineHeight = ZuneEpisodeRules.DisclosureLine, Weight = 600, Shrink = 0, Color = Tok.TextSecondary });
        row.Add(Sidebar.Chevron.Section(expanded, size: ZuneEpisodeRules.DisclosureChevron));   // rotates with the thread's reveal
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Start, MinWidth = 0,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.M, Spacing.XS), Corners = R.Shape.All(ShapeRole.Control),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
            OnClick = toggle,
            Children = row.ToArray(),
        };
    }

    // ══ 3. THE REACTION FLYOUT ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeReactionFlyout"/>.</summary>
    public static Element FlyoutTemplate(ReactionFlyoutProps p, in ViewContext ctx)
    {
        int columns = ZuneEpisodeRules.PaletteColumns;
        int rowCount = (p.Palette.Length + columns - 1) / columns;
        var palette = new Element[rowCount];
        for (int row = 0; row < rowCount; row++)
        {
            var buttons = new List<Element>();
            for (int column = 0; column < columns && row * columns + column < p.Palette.Length; column++)
            {
                var option = p.Palette[row * columns + column];
                string emoji = option.Emoji;
                buttons.Add(ToolTip.Wrap(Button.Subtle(emoji, () => p.React(emoji), isEnabled: option.Enabled) with
                {
                    Width = ZuneEpisodeRules.PaletteButtonWidth, Height = ZuneEpisodeRules.PaletteButtonHeight, Grow = 1, Basis = 0, MinWidth = 0,
                    Fill = option.Chosen ? Tok.FillSubtleSecondary : ColorF.Transparent,
                    BorderWidth = option.Chosen ? 1 : 0, BorderColor = R.Ink.AccentAction,
                }, option.Name, grow: 1));
            }
            palette[row] = new BoxEl { Direction = 0, Gap = Spacing.XS, MinWidth = 0, Children = buttons.ToArray() };
        }
        var body = new List<Element>
        {
            new TextEl(Loc.Get(Strings.Podcast.Reader.Reactions)) { Size = ZuneEpisodeRules.FlyoutTitleSize, Weight = 600, Color = Tok.TextPrimary },
            new BoxEl { Direction = 1, Gap = Spacing.XS, Shrink = 0, MinWidth = 0, Children = palette },
        };
        if (p.Error.Length > 0)
            body.Add(new TextEl(p.Error) { Size = ZuneEpisodeRules.FlyoutErrorSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0 });
        body.Add(p.Roster);
        return new BoxEl
        {
            Direction = 1,
            Width = Math.Min(ZuneEpisodeRules.FlyoutWidth, Math.Max(1, p.ViewportWidth - ZuneEpisodeRules.FlyoutMarginX)),
            Height = Math.Min(ZuneEpisodeRules.FlyoutHeight, Math.Max(1, p.ViewportHeight - ZuneEpisodeRules.FlyoutMarginY)),
            MinHeight = 0, MinWidth = 0,
            Padding = Edges4.All(Spacing.M), Gap = Spacing.S, ClipToBounds = true, Children = body.ToArray(),
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeReactionRoster"/>: the filter chips over the scrolling people.</summary>
    public static Element RosterTemplate(ReactionRosterProps p, in ViewContext ctx)
    {
        var filters = new Element[p.Filters.Length];
        for (int i = 0; i < filters.Length; i++)
        {
            var f = p.Filters[i];
            string emoji = f.Emoji;
            filters[i] = Button.Subtle(f.Label, () => p.Select(emoji)) with
            {
                Fill = f.Selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
                BorderWidth = f.Selected ? 1 : 0, BorderColor = Tok.StrokeControlDefault,
            };
        }
        return new BoxEl
        {
            Direction = 1, Grow = 1, Basis = 0, MinHeight = 0, MinWidth = 0, Gap = Spacing.S, Children =
            [
                new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.XS, Shrink = 0, MinWidth = 0, Children = filters },
                Ui.ScrollView(p.People) with
                {
                    Grow = 1, Basis = 0, MinHeight = 0, AutoEdgeFade = true,
                    ScrollKey = p.ScrollKey, Key = p.PeopleKey,
                },
            ],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeReactionPeople"/>.</summary>
    public static Element PeopleTemplate(ReactionPeopleProps p, in ViewContext ctx)
    {
        var rows = new List<Element>();
        foreach (var item in p.People)
            rows.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0,
                Padding = new Edges4(0, Spacing.S, 0, Spacing.S), Children =
                [
                    PersonPicture.Create("", ZuneEpisodeRules.PersonAvatar, displayName: item.Author, imageSourcePath: item.Avatar) with { Shrink = 0 },
                    new TextEl(item.Author.Length > 0 ? item.Author : Loc.Get(Strings.Podcast.Reader.Anonymous))
                        { Size = ZuneEpisodeRules.PersonSize, Grow = 1, Basis = 0, MinWidth = 0, Wrap = TextWrap.Wrap, Color = Tok.TextPrimary },
                    new TextEl(item.Emoji) { Size = ZuneEpisodeRules.PersonEmojiSize },
                ],
            });
        if (rows.Count == 0) rows.Add(ZuneEpisode.Quiet(Loc.Get(Strings.Podcast.Reader.Empty)));
        if (p.More is { } more) rows.Add(more);
        return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0, Children = rows.ToArray() };
    }
}
