// ── Themes/Classic/Structure/Views/Episode/ClassicEpisode.cs — Classic's episode page slots and the family's registration (themes plan 6.1, T9c) ──
//
// From `Entities\Episode.Page.cs`: the pictures of the episode page's frame slots (the primary, the show attribution, the action satellites and
// the Save heart, the rail's short About block) and, from `Entities\Episode.Reader.cs`, the show rating popover the show page's rating slot opens.
// The page host, the saved / played / resume decisions (`Episode.Invoke`, `StartAt`, `MarkEpisode`, `Spotify.Podcasts.*`) and the
// slot components that read them stay core; a slot component builds its props and mounts one of these keys.
//
// The reader and the discussion are their own files (`ClassicEpisodeReader.cs`, `ClassicDiscussion.cs`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's episode family: the registration and the page slots.</summary>
internal static class ClassicEpisode
{
    /// <summary>Registers every key of the episode family (called by <see cref="ClassicStructure.Register"/>).</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.EpisodePrimary, Primary, "classic.episode.primary");
        views.Add(ViewKeys.EpisodeAttribution, Attribution, "classic.episode.attribution");
        views.Add(ViewKeys.EpisodeActions, Actions, "classic.episode.actions");
        views.Add(ViewKeys.EpisodeSave, Save, "classic.episode.save");
        views.Add(ViewKeys.EpisodeRailAbout, RailAbout, "classic.episode.rail-about");
        views.Add(ViewKeys.EpisodeRating, Rating, "classic.episode.rating");
        views.Add(ViewKeys.EpisodeReader, ClassicEpisodeReader.ReaderTemplate, "classic.episode.reader");
        views.Add(ViewKeys.EpisodeAbout, ClassicEpisodeReader.AboutTemplate, "classic.episode.about");
        views.Add(ViewKeys.EpisodeChapters, ClassicEpisodeReader.ChaptersTemplate, "classic.episode.chapters");
        views.Add(ViewKeys.EpisodeDiscussion, ClassicDiscussion.ThreadTemplate, "classic.episode.discussion");
        views.Add(ViewKeys.EpisodeCommentCard, ClassicDiscussion.CardTemplate, "classic.episode.comment-card");
        views.Add(ViewKeys.EpisodeCommentSeed, ClassicDiscussion.SeedTemplate, "classic.episode.comment-seed");
        views.Add(ViewKeys.EpisodeLoadMore, ClassicDiscussion.LoadMoreTemplate, "classic.episode.load-more");
        views.Add(ViewKeys.EpisodeReactionFlyout, ClassicDiscussion.FlyoutTemplate, "classic.episode.reaction-flyout");
        views.Add(ViewKeys.EpisodeReactionRoster, ClassicDiscussion.RosterTemplate, "classic.episode.reaction-roster");
        views.Add(ViewKeys.EpisodeReactionPeople, ClassicDiscussion.PeopleTemplate, "classic.episode.reaction-people");
    }

    /// <summary>The primary: the Play / Resume / Pause button, or the quiet line of a blocked episode.
    /// W11d: the app accent, never the show / episode tone — `Design.AccentCtx` carries the entity's own art-derived tone (the leak the video
    /// frame caught as a pink Resume pill on this exact pill). A media transport verb should not repaint with the cover.</summary>
    static Element Primary(EpisodePrimaryProps p, in ViewContext ctx)
        => p.Blocked is { } blocked
            ? PodcastReaderUI.Quiet(blocked)
            : Controls.PlayButton(static () => Tok.AccentDefault, p.Play, p.Label, p.Playing ? Icons.Pause : Icons.Play);

    /// <summary>The shared inline link row (`Controls.RichTextRow`), not a stretched full-bleed Hyperlink box: one ellipsised line under the
    /// title, the same idiom an album's artist line uses. The row parses the anchor, so the show name is clickable on its own without owning
    /// the whole row's hit box.</summary>
    static Element Attribution(EpisodeAttributionProps p, in ViewContext ctx)
        => Controls.RichTextRow(p.Html, 14, Tok.TextSecondary, Tok.AccentTextPrimary, p.Open);

    static Element Actions(EpisodeActionsProps p, in ViewContext ctx)
    {
        float box = SatelliteBox(in ctx);
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, Wrap = true, Children =
            [
                p.Save,
                ClassicDetailKit.RailSatellite(p.Completed ? Icons.Undo : Icons.Check,
                    Loc.Get(p.Completed ? Strings.Podcast.Menu.MarkUnplayed : Strings.Podcast.Menu.MarkPlayed), p.ToggleCompleted, box),
                ClassicDetailKit.RailSatellite(Icons.Share, Loc.Get(Strings.Menu.CopyLink), p.CopyLink, box),
                ClassicDetailKit.RailSatelliteMore(p.Menu, box),
            ],
        };
    }

    static Element Save(EpisodeSaveProps p, in ViewContext ctx) => ClassicDetailKit.RailSatellite(p.Glyph, p.Name, p.Click, SatelliteBox(in ctx));

    /// <summary>A satellite's box: the rail's 32, or the tier's target where it is larger (Compact's 40: 32 boxes 4 apart left each one a 36-wide hit).</summary>
    static float SatelliteBox(in ViewContext ctx) => MathF.Max(Detail.RailLayout.SatelliteSize, ClassicDetailTiers.Of(ctx.Tier()).Target);

    /// <summary>The rail's SHORT about block (podcast-episode-peek §1): a small label, the description clamped to four
    /// lines, and the inline overflow affordance the clamp itself decides on — `ExpandableRichTextFlex` reserves "… More" on the last line
    /// ONLY when the body actually overflows and swaps it for "Less" once expanded, so a one-paragraph episode gets no dead button.</summary>
    static Element RailAbout(EpisodeRailAboutProps p, in ViewContext ctx) => new BoxEl
    {
        Direction = 1, MinWidth = 0, Gap = Spacing.S, Margin = new Edges4(0, Spacing.XS, 0, 0), Children =
        [
            new BoxEl { Height = 1, Shrink = 0, MinWidth = 0, Fill = Tok.StrokeDividerDefault },
            R.Type.Eyebrow(Loc.Get(Strings.Podcast.RailAbout)) with { Color = Tok.TextTertiary },
            Controls.ExpandableRichTextFlex(p.Prose, 13, Tok.TextSecondary, Tok.AccentTextPrimary, ClassicEpisodeRules.RailAboutLines,
                p.Key, p.Open),
        ],
    };

    /// <summary>The show rating popover: the title, the star row (disabled while busy or offline) and the error line.</summary>
    static Element Rating(ShowRatingProps p, in ViewContext ctx) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Width = ClassicEpisodeRules.RatingWidth, Padding = Edges4.All(Spacing.M), Children =
        [
            PodcastReaderUI.Heading(p.Title),
            Controls.StarRow(p.Rating, p.Rate, ClassicEpisodeRules.RatingStar, p.Tone),
            PodcastReaderUI.Quiet(p.Error),
        ],
    };
}
