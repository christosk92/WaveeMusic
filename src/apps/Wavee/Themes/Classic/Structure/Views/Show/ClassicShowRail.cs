// ── Themes/Classic/Structure/Views/Show/ClassicShowRail.cs — Classic's show primary and topics (themes plan 6.1, T9c) ───────────────
//
// From `Entities\Show.Page.cs` "4. THE RAIL'S SLOTS" and `Show.Podcasts.cs`: the library pane's primary (`ShowReaderStyle.Primary`) and the topic
// words (`ShowReaderStyle.Topics`).
//
// WHAT THE CORE KEEPS: each slot is a core component (`PrimarySlot`, `TopicsSlot`) that owns its facts; it hands the decided values and verbs to a
// template here. The primary's decision (`ShowReaderRules.PrimaryOf`: Follow, Resume, episode 1 or latest), the follow write (`Controls.Library`)
// and the topics search are the core's.

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
}
