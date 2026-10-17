// ── Themes/Zune/Structure/Views/Episode/ZuneEpisode.cs — Zune's episode page slots and the family's registration (themes plan 6.3; P5 Z6b2) ──
//
// Board Z-Episode (and -Compact, -Small, -Huge). The episode page is the detail frame's two columns: the identity column on the left (the 280
// cover, the show's name as a link, "#812 · 9 october · 48 min", the ring row — "resume" over "23 min left", save, share, the played mark and
// the "..." menu — then the short about), and on the right the reader (`ZuneEpisodeReader.cs`). The page's own words (about · chapters ·
// transcript · comments) are the band's row 2: the page publishes them through the `PageChromeSink` (`Entities\Episode.Page.cs`), the reader
// below draws no pivot of its own.
//
// What stays core: the page host and its demand effects, the saved / played / resume decisions (`Episode.Invoke`, `StartAt`, `MarkEpisode`,
// `Spotify.Podcasts.*`), the scroll spy (anchors, the viewport, the active section), the chapter lease, the transcript view, the discussion's
// signals, resources, composer verbs and reaction session, and the pure rules. A template draws and never names `Playback`, `Entities`, `Store`,
// `Shell` or `Spotify` (WVTH002): those arrive as props, signals and verbs.
//
// The rings' size is the tier's (36, 40 at Compact and 4K), read from the view context here (a slot template has one).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static Wavee.Themes.Zune.ZuneEpisodeRules;
using K = Wavee.Themes.Zune.ZuneDetailKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's episode family: the registration and the page slots.</summary>
internal static class ZuneEpisode
{
    /// <summary>Registers every key of the episode family and the show page's style (called by <see cref="ZuneStructure"/>'s registrar).</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.EpisodePrimary, Primary, "zune.episode.primary");
        views.Add(ViewKeys.EpisodeAttribution, Attribution, "zune.episode.attribution");
        views.Add(ViewKeys.EpisodeActions, Actions, "zune.episode.actions");
        views.Add(ViewKeys.EpisodeSave, Save, "zune.episode.save");
        views.Add(ViewKeys.EpisodeRailAbout, RailAbout, "zune.episode.rail-about");
        views.Add(ViewKeys.EpisodeRating, Rating, "zune.episode.rating");
        views.Add(ViewKeys.EpisodeReader, ZuneEpisodeReader.ReaderTemplate, "zune.episode.reader");
        views.Add(ViewKeys.EpisodeAbout, ZuneEpisodeReader.AboutTemplate, "zune.episode.about");
        views.Add(ViewKeys.EpisodeChapters, ZuneEpisodeReader.ChaptersTemplate, "zune.episode.chapters");
        views.Add(ViewKeys.EpisodeDiscussion, ZuneEpisodeDiscussion.ThreadTemplate, "zune.episode.discussion");
        views.Add(ViewKeys.EpisodeCommentCard, ZuneEpisodeDiscussion.CardTemplate, "zune.episode.comment-card");
        views.Add(ViewKeys.EpisodeCommentSeed, ZuneEpisodeDiscussion.SeedTemplate, "zune.episode.comment-seed");
        views.Add(ViewKeys.EpisodeLoadMore, ZuneEpisodeDiscussion.LoadMoreTemplate, "zune.episode.load-more");
        views.Add(ViewKeys.EpisodeReactionFlyout, ZuneEpisodeDiscussion.FlyoutTemplate, "zune.episode.reaction-flyout");
        views.Add(ViewKeys.EpisodeReactionRoster, ZuneEpisodeDiscussion.RosterTemplate, "zune.episode.reaction-roster");
        views.Add(ViewKeys.EpisodeReactionPeople, ZuneEpisodeDiscussion.PeopleTemplate, "zune.episode.reaction-people");
    }

    /// <summary>A quiet sentence the theme writes (a blocked episode's line, the empty and preview notes): the meta voice, wrapping, lowered as a label.</summary>
    internal static Element Quiet(string text) => R.Type.TrackMeta(text) with
    {
        Case = R.Case.For(LabelKind.Label), Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
    };

    /// <summary>A failed section: the quiet line over a "try again" button.</summary>
    internal static Element Failed(Action retry) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Start,
        Children = [Quiet(Loc.Get(Strings.Podcast.Reader.Unavailable)), Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), retry)],
    };

    /// <summary>The primary: the accent play ring with its words — "resume" over "23 min left" ("Resume · 23 min left" arrives as one label and is
    /// split at its dot) — or the quiet line of a blocked episode. The app accent, never the episode's tone.</summary>
    static Element Primary(EpisodePrimaryProps p, in ViewContext ctx)
    {
        if (p.Blocked is { } blocked) return Quiet(blocked);
        var lay = ZuneShow.RingLayoutOf(ctx.Tier());
        string label = p.Label, word = label, sub = "";
        int dot = label.IndexOf(LabelSplit, StringComparison.Ordinal);
        if (dot > 0)
        {
            word = label[..dot];
            sub = label[(dot + LabelSplit.Length)..];
        }
        return ZuneShow.RingEl("episode-play", p.Playing ? Icons.Pause : Icons.Play, label, word, sub.Length > 0 ? sub : null,
            filled: true, p.Play, lay);
    }

    /// <summary>The show's name under the cover (Z-Episode: a 14 white link): the shared inline link row, so the name is clickable on its own
    /// without owning the whole row's hit box.</summary>
    static Element Attribution(EpisodeAttributionProps p, in ViewContext ctx)
        => Controls.RichTextRow(p.Html, AttributionSize, Tok.TextPrimary, R.Ink.AccentAction, p.Open);

    /// <summary>The ring row's satellites: save, the played mark, share, "..." — in the tier's order and size (share is dropped at Small, as the
    /// album's rings drop it).</summary>
    static Element Actions(EpisodeActionsProps p, in ViewContext ctx)
    {
        var lay = ZuneShow.RingLayoutOf(ctx.Tier());
        var kids = new List<Element>(4) { p.Save };
        kids.Add(K.Ring("played", p.Completed ? Icons.Undo : Icons.Check,
            Loc.Get(p.Completed ? Strings.Podcast.Menu.MarkUnplayed : Strings.Podcast.Menu.MarkPlayed), false, null, p.ToggleCompleted, lay));
        if (lay.ShareRing) kids.Add(K.Ring("share", Icons.Share, Loc.Get(Strings.Menu.CopyLink), false, null, p.CopyLink, lay));
        if (lay.MoreRing) kids.Add(K.MoreRing(p.Menu, lay));
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = lay.ActionsGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray(),
        };
    }

    /// <summary>The save ring (its state is the host's: the glyph, the name and the click arrive in the props).</summary>
    static Element Save(EpisodeSaveProps p, in ViewContext ctx)
        => K.Ring("save", p.Glyph, p.Name, false, null, p.Click, ZuneShow.RingLayoutOf(ctx.Tier()));

    /// <summary>The rail's SHORT about block: the description clamped to four lines, and the inline overflow affordance the clamp itself decides on —
    /// <c>ExpandableRichTextFlex</c> reserves "… more" on the last line ONLY when the body actually overflows and swaps it for "less" once expanded.</summary>
    static Element RailAbout(EpisodeRailAboutProps p, in ViewContext ctx) => new BoxEl
    {
        Direction = 1, MinWidth = 0, Margin = new Edges4(0, Spacing.XS, 0, 0),
        Children = [Controls.ExpandableRichTextFlex(p.Prose, RailAboutSize, Tok.TextSecondary, R.Ink.AccentAction, RailAboutLines, p.Key, p.Open)],
    };

    /// <summary>The show rating popover: the title, the star row (disabled while busy or offline) and the error line.</summary>
    static Element Rating(ShowRatingProps p, in ViewContext ctx) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Width = RatingWidth, Padding = Edges4.All(Spacing.M),
        Children =
        [
            R.Type.SheetTitle(p.Title) with { Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            Controls.StarRow(p.Rating, p.Rate, RatingStar, p.Tone ?? s_accent),
            Quiet(p.Error),
        ],
    };

    static readonly Func<ColorF> s_accent = static () => R.Ink.AccentAction;
}
