// ── Entities/Show.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the episode column's static pieces: the status/order toolbar, the "Listen next" banner, the section header, the empty
// arm (its own empty-SHOW copy), the load-more pill and the figure-space cold seed the list's skeleton derives from
//
// Role: UI
// Owner: M
// Wave: 5
// Budget: 320 lines
// Spec: ch 09 W1, W4, W6, W8, W9, §3 "Show page — right column" / "Listen-next banner", §6.1, §9.1 (the Shrink 0 pill),
//       §9.4 (the load-more margin, the empty-show key, minutes via loc) · 0.2.9 `Features/Detail/EpisodeList.cs:74-186`
//
// Every function here is a VALUE re-created by its caller's render (ch 09 §1.4): the list's head component re-renders
// only when what it shows changes (the vertical arm, the resume pick and its row version, the empty arm), and the foot
// only when the paging gate or the in-flight flag flips — never on a scroll frame.
//
// The column's rhythm (W1): body padding (16, 12, 16, 96) · gap 12 between toolbar, banner, header, cards and pill ·
// the toolbar's own 4 below it · the 96 bottom reserve = the player dock's 72 + 24.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary>The episode column's bottom reserve: the last card clears the player dock by 24 (ch 09 item 11).</summary>
    public const float BottomReserve = Design.Dock.Reserve + Spacing.XXL;

    const float BannerArt = 72f;
    const int SeedRows = 8;
    static readonly Action s_noop = static () => { };

    /// <summary>The status selector flush left, Newest/Oldest flush right (W1). ABSENT in the vertical arm — the caller
    /// decides (W4: "the ONE control set that simply disappears in the narrow arm").</summary>
    public static Element EpisodeToolbar(Signal<int> status, Signal<int> order) => new BoxEl
    {
        Key = "eps:toolbar", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
        Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
        Children = [SelectorBar.Create(StatusLabels(), status), new BoxEl { Grow = 1f }, SelectorBar.Create(OrderLabels(), order)],
    };

    // The SelectorBar re-pushes its items live; in `Episode.Rules.Status` order (0 All · 1 Unplayed · 2 In progress · 3 Played).
    static string[] StatusLabels() =>
    [
        Loc.Get(Strings.Podcast.Filter.All), Loc.Get(Strings.Podcast.Filter.Unplayed),
        Loc.Get(Strings.Podcast.Filter.InProgress), Loc.Get(Strings.Podcast.Filter.Played),
    ];

    static string[] OrderLabels() => [Loc.Get(Strings.Podcast.Sort.Newest), Loc.Get(Strings.Podcast.Sort.Oldest)];

    /// <summary>W8: "Listen next" over the resume card — 72 art · the CONTINUE LISTENING eyebrow · a 15/700 one-line title
    /// · "MMM d · N min" · the progress rule · the Resume capsule, which NEVER shrinks (the copy column gives, §9.1).</summary>
    public static Element ResumeBanner(Episode e, Action resume)
    {
        float pct = e.IsValid ? Episode.Rules.PctOf(e) : 0f;
        string date = e.IsValid && e.Knows(EpisodeFields.Published) ? Episode.DateLabel(e.PublishedAt) : "";
        string minutes = e.IsValid && e.Knows(EpisodeFields.Duration) ? Episode.MinutesLabel(e.DurationMs) : "";
        string caption = date.Length > 0 && minutes.Length > 0 ? date + " · " + minutes : date + minutes;
        string title = e.IsValid && e.Knows(EpisodeFields.Title) ? e.Title : "";
        string? art = e.IsValid && e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null;
        return new BoxEl
        {
            Key = "eps:resume", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                Design.Type.RailHeader(Loc.Get(Strings.Podcast.ListenNext)),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Padding = new Edges4(Spacing.M, Spacing.M, Spacing.L, Spacing.M),
                    Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = BannerArt, Height = BannerArt, Shrink = 0f, Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                            Children = [Controls.Artwork(art, BannerArt, BannerArt, Radii.Card)],
                        },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                            Children =
                            [
                                Design.Type.Eyebrow(Loc.Get(Strings.Podcast.ContinueListening)) with
                                {
                                    Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                },
                                // 15/700 is 0.2.9's raw banner cut (§9.4: port the pixels; the EpisodeBannerTitle alias is L's).
                                new TextEl(title)
                                {
                                    Size = 15f, LineHeight = 20f, Weight = 700, Color = Tok.TextPrimary,
                                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                                new TextEl(caption)
                                {
                                    Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary,
                                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                                Episode.ProgressRule(pct),
                            ],
                        },
                        Controls.Accent(Loc.Get(Strings.Podcast.Resume), Tok.AccentDefault, resume) with { Shrink = 0f },
                    ],
                },
            ],
        };
    }

    /// <summary>The "Episodes" section header (W1).</summary>
    public static Element EpisodesHeader()
        => Design.Type.RailHeader(Loc.Get(Strings.Podcast.Episodes)) with { Key = "eps:header" };

    /// <summary>W9's empty box, with the §9.4 fix: a show whose unfiltered list is genuinely empty says so in its own words
    /// (<c>podcast.empty</c>); any other empty view is "No episodes match this filter" (<c>Episode.Rules.IsEmptyShow</c>).</summary>
    public static Element EmptyArm(bool emptyShow) => new BoxEl
    {
        Key = "eps:empty", Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.XL), MinWidth = 0f,
        Children =
        [
            new TextEl(emptyShow ? Loc.Get("podcast.empty") : Loc.Get(Strings.Podcast.NoEpisodes))
            {
                Size = 14f, LineHeight = 20f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MinWidth = 0f,
            },
        ],
    };

    /// <summary>The standard (never accent) pill at the end of the list — not an infinite-scroll sentinel. While a page is
    /// out the label reads "Loading…" and a tap is a no-op (not a disabled button, W9). The margin is TOP 8: 0.2.9's
    /// <c>Left 8</c> was an Edges4 mis-order that pushed the centred pill 4 DIP off centre (§9.4).</summary>
    public static Element LoadMorePill(bool paging, Action page) => new BoxEl
    {
        Key = "eps:more", Direction = 0, Justify = FlexJustify.Center, Margin = new Edges4(0f, Spacing.S, 0f, 0f),
        Children =
        [
            Controls.Pill(Loc.Get(paging ? Strings.Podcast.LoadingMore : Strings.Podcast.LoadMore),
                          paging ? s_noop : page, ButtonAppearance.Standard),
        ],
    };

    /// <summary>The cold skeleton's SOURCE (W6 + the §9.4 fix): the real toolbar labels (two-column only), the real
    /// "Episodes" header and eight figure-space seed cards — the list's `SkelRegionEl` derives its shimmer from this one
    /// tree, so the bars are the real rows' shapes and nothing is hand-drawn. No banner, no rule, no pill: the seed
    /// invents no progress and no paging.</summary>
    internal static Element SeedList(bool vertical)
    {
        var kids = new Element[SeedRows + (vertical ? 1 : 2)];
        int at = 0;
        if (!vertical)
            kids[at++] = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
                Children = [SelectorBar.Create(StatusLabels()), new BoxEl { Grow = 1f }, SelectorBar.Create(OrderLabels())],
            };
        kids[at++] = EpisodesHeader();
        for (int i = 0; i < SeedRows; i++) kids[at++] = Episode.SeedRow();
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f, Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, BottomReserve),
            Children = kids,
        };
    }
}
