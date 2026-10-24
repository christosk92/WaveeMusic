// ── Themes/Classic/Structure/Views/Show/ClassicShowRail.cs — Classic's show rail slots and library pane (themes plan 6.1, T9c) ─────────
//
// From `Entities\Show.Page.cs` "4. THE RAIL'S SLOTS", `Show.Podcasts.cs`, `Show.Pane.cs` and `Show.UI.cs` (`PublisherLine`): what the shared detail
// frame's podcast slots (`FrameSlots` Primary, Ledger, Rating, Badges, Topics, Attribution, Satellites) draw, and the library pane's column.
//
// WHAT THE CORE KEEPS: each slot is a core component (`PrimarySlot`, `LedgerSlot`, `RatingSlot`, `BadgesSlot`, `TopicsSlot`) that owns its tone memo,
// its overlay handle and its facts; it hands the decided values and verbs to a template here. The primary's decision (`ShowReaderRules.PrimaryOf`:
// Follow, Resume, episode 1 or latest), the follow write (`Controls.Library`), the trailer, the rate flyout's open / close and the topics search are
// the core's. The page-facing satellite builders are the shared `ClassicDetailKit`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.Classic.ClassicShowRules;

namespace Wavee.Themes.Classic;

internal static partial class ClassicShow
{
    /// <summary>The CTA's primary by visit (§4): New &amp; not following → Follow + a ghost ▶ (episode 1 for a serial,
    /// else the latest); Returning → Resume · N min left on listen-next's resume; otherwise ▶ the latest.</summary>
    static Element Primary(ShowPrimaryArgs a)
    {
        // Reports 11c/11d: the APP accent (never the show tone) and a label that WRAPS — Workstream B's
        // Controls.PrimaryButton(wrap: true), not a bespoke pill (was Show.PrimaryPill).
        // The size is the host's ask (the library pane's 40-DIP Compact targets, contract 1.22); the stock Medium everywhere else.
        // A show with no episodes keeps its play verbs in place, disabled (sh8: "Latest" offered nothing to play).
        bool playable = !a.Empty;
        Element pill = a.Primary switch
        {
            ShowReaderRules.Primary.Follow => Controls.PrimaryButton(Loc.Get(Strings.Artist.Follow), a.Follow, Tok.AccentDefault, Icons.Add, wrap: true, size: a.Size),
            ShowReaderRules.Primary.Resume => Controls.PrimaryButton(
                Loc.Get(Strings.Podcast.Resume) + " · " + Strings.Podcast.Left(Episode.DurationWords(a.ResumeLeft)), a.Resume, Tok.AccentDefault, wrap: true, size: a.Size,
                isEnabled: playable),
            ShowReaderRules.Primary.PlayFirst => Controls.PrimaryButton(Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)), a.First, Tok.AccentDefault, wrap: true,
                size: a.Size, isEnabled: playable),
            _ => Controls.PrimaryButton(Loc.Get(Strings.Podcast.Latest), a.Latest, Tok.AccentDefault, wrap: true, size: a.Size, isEnabled: playable),
        };
        if (a.Primary != ShowReaderRules.Primary.Follow) return pill;
        Element ghost = a.Ghost == ShowReaderRules.Primary.PlayFirst
            ? Button.Create(Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)), a.First, ButtonAppearance.Standard, a.Size, glyph: Icons.Play, isEnabled: playable)
            : Button.Create(Loc.Get(Strings.Podcast.Latest), a.Latest, ButtonAppearance.Standard, a.Size, glyph: Icons.Play, isEnabled: playable);
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Detail.RailLayout.SatelliteGap, AlignItems = FlexAlign.Center, Children = [pill, ghost],
        };
    }

    /// <summary>The played ledger (W1): the three-part bar and "84 played · 3 in progress · 41 to go · 5 new" — or, when
    /// the hydrate failed, "progress unavailable" (§6.1).</summary>
    static Element Ledger(ShowLedgerArgs a)
    {
        if (a.Failed)
            return new BoxEl
            {
                Direction = 1, Width = a.Width, MinWidth = 0f,
                Children = [Caption(Loc.Get(Strings.Podcast.ProgressUnavailable)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
            };
        int sum = a.Played + a.InProgress + a.ToGo;
        float played = sum > 0 ? a.Played / (float)sum : 0f, progress = sum > 0 ? a.InProgress / (float)sum : 0f;
        TextSpan[] line = a.Fresh > 0
            ? [new TextSpan(Strings.Podcast.Ledger(a.Played, a.InProgress, a.ToGo) + " · "),
               new TextSpan(Strings.Podcast.LedgerNew(a.Fresh), Weight: 600, Color: a.Tone())]
            : [new TextSpan(Strings.Podcast.Ledger(a.Played, a.InProgress, a.ToGo))];
        return new BoxEl
        {
            Direction = 1, Gap = Detail.RailLayout.LedgerGap, Width = a.Width, MinWidth = 0f,
            Children =
            [
                Controls.LedgerBar(played, progress, a.Tone),
                new SpanTextEl(line) { Size = Caption("").Size, LineHeight = Detail.RailLayout.LedgerLineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
            ],
        };
    }

    /// <summary>The rating row: ★ 4.8 · "12,431 ratings" (· "yours 5★") — a button that opens the rate flyout, whose
    /// stars are DISABLED with their reason until the write lands (wave P10, D-10).</summary>
    static Element Rating(ShowRatingArgs a)
    {
        var button = new BoxEl
        {
            Direction = 0, Gap = RatingGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
            Padding = new Edges4(4f, 2f, 4f, 2f), Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = a.Toggle, OnRealized = a.Realized,
            Children =
            [
                Icon(Icons.FavoriteStarFill, RatingStar) with { Color = Prop.Of(a.Tone) },
                R.Type.DenseTitle(a.Average) with { Color = Tok.TextPrimary, MaxLines = 1 },
                R.Type.DenseMeta(a.Count) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            ],
        };
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, MaxWidth = float.IsFinite(a.Width) ? a.Width : float.NaN,
            Children = [Controls.Named(button, a.Name)],
        };
    }

    /// <summary>The badge chips on the eyebrow's line: exclusive (the tone) · E · video.</summary>
    static Element Badges(ShowBadgesArgs a)
    {
        var flags = a.Flags;
        var chips = new List<Element>(3);
        if ((flags & ShowFlags.Exclusive) != 0) chips.Add(Controls.Chip(Loc.Get(Strings.Podcast.Badge.Exclusive), tone: true, toneOf: a.Tone));
        if ((flags & ShowFlags.Explicit) != 0) chips.Add(Controls.Chip("E"));
        if ((flags & ShowFlags.Video) != 0) chips.Add(Controls.Chip(Loc.Get(Strings.Podcast.Badge.Video), Icons.Movie));
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Detail.RailLayout.BadgeGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = chips.ToArray(),
        };
    }

    /// <summary>The topic words, under the CTA: one subtle button per topic, wrapping, at the rail's measure.</summary>
    static Element Topics(ShowTopicsArgs a)
    {
        var children = new Element[a.Topics.Length];
        for (int i = 0; i < children.Length; i++)
        {
            string topic = a.Topics[i];
            children[i] = Button.Subtle(topic, () => a.Open(topic));
        }
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0,
            MaxWidth = float.IsFinite(a.Width) ? a.Width : float.NaN, Children = children,
        };
    }

    /// <summary>The rail's publisher line (the Attribution slot — the publisher left the meta line, P2-M): 13/18
    /// secondary, one line, at the rail's measure.</summary>
    static Element Publisher(string publisher, float width) => new BoxEl
    {
        Direction = 0, MinWidth = 0f, MaxWidth = float.IsFinite(width) ? width : AboutMeasure,
        Children = [R.Type.DenseMeta(publisher) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
    };

    /// <summary>The CTA's satellites (W1): ♥ (hidden while Follow is the primary) · trailer (when the show names one)
    /// · the bell, DISABLED with its reason (D-10) · share · ⋯. A FIXED array per slot set; what comes and goes binds its presence.</summary>
    static Element[] Satellites(ShowSatellitesArgs a) =>
    [
        new BoxEl { Key = "sat:heart", Visible = a.HeartShown, Children = [ClassicDetailKit.RailSatelliteSave(a.Uri, a.Name)] },
        new BoxEl
        {
            Key = "sat:trailer", Visible = a.TrailerShown,
            Children = [ClassicDetailKit.RailSatellite(Icons.TvMonitor, Loc.Get(Strings.Podcast.Trailer), a.PlayTrailer)],
        },
        ClassicDetailKit.RailSatellite(Icons.Bell, Loc.Get(Strings.Podcast.Notify.Unavailable), null) with { Key = "sat:bell" },
        ClassicDetailKit.RailSatellite(Icons.Share, Loc.Get(Strings.Menu.Share), a.Share) with { Key = "sat:share" },
        ClassicDetailKit.RailSatelliteMore(a.More),
    ];
}
