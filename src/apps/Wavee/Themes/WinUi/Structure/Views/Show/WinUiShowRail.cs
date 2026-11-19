// ── Themes/WinUi/Structure/Views/Show/WinUiShowRail.cs — WinUI's show primary, follow toggle and topics (W6b, winui menus/podcasts) ──────────────────
//
// What the library's show pane and the show aside draw in WinUI's voice (W-Show): "▶ Resume · 31 min left" as the AccentButton
// (`ShowReaderStyle.Primary`), "✓ Following" as the neutral toggle (`FollowToggle`, the pane's), the topics as standard small buttons
// (`ShowReaderStyle.Topics`, the aside's). The decisions (which primary, the follow write) stay the core's.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.WinUi.WinUiShowRules;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiShow
{
    static readonly Func<ColorF> s_accent = static () => Tok.AccentDefault;

    /// <summary>The primary by visit: Follow (and a standard "▶ Latest" / "▶ Episode 1" beside it), Resume · N min left, ▶ Episode 1 or ▶ Latest, on the
    /// AccentButton with the play glyph (W-Show). A show with no episodes keeps its play verbs in place, disabled.</summary>
    static Element Primary(ShowPrimaryArgs a)
    {
        bool playable = !a.Empty;
        Element pill = a.Primary switch
        {
            ShowReaderRules.Primary.Follow => Button.Create(Loc.Get(Strings.Artist.Follow), a.Follow, ButtonAppearance.Accent, a.Size, glyph: Icons.Add),
            ShowReaderRules.Primary.Resume => Button.Create(
                Loc.Get(Strings.Podcast.Resume) + " · " + Strings.Podcast.Left(Episode.DurationWords(a.ResumeLeft)), a.Resume, ButtonAppearance.Accent, a.Size,
                glyph: Icons.Play, isEnabled: playable),
            ShowReaderRules.Primary.PlayFirst => Button.Create(Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)), a.First, ButtonAppearance.Accent, a.Size,
                glyph: Icons.Play, isEnabled: playable),
            _ => Button.Create(Loc.Get(Strings.Podcast.Latest), a.Latest, ButtonAppearance.Accent, a.Size, glyph: Icons.Play, isEnabled: playable),
        };
        if (a.Primary != ShowReaderRules.Primary.Follow) return pill;
        Element ghost = a.Ghost == ShowReaderRules.Primary.PlayFirst
            ? Button.Create(Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)), a.First, ButtonAppearance.Standard, a.Size, glyph: Icons.Play, isEnabled: playable)
            : Button.Create(Loc.Get(Strings.Podcast.Latest), a.Latest, ButtonAppearance.Standard, a.Size, glyph: Icons.Play, isEnabled: playable);
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = WinUiDetailRules.ActionGap, AlignItems = FlexAlign.Center, Children = [pill, ghost],
        };
    }

    /// <summary>The topic words: one standard small button per topic (W-Show's About chips), wrapping, at the rail's measure.</summary>
    static Element Topics(ShowTopicsArgs a)
    {
        var children = new Element[a.Topics.Length];
        for (int i = 0; i < children.Length; i++)
        {
            string topic = a.Topics[i];
            children[i] = Button.Create(topic, () => a.Open(topic), ButtonAppearance.Standard, ControlSize.Small);
        }
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.S, MinWidth = 0,
            MaxWidth = float.IsFinite(a.Width) ? a.Width : float.NaN, Children = children,
        };
    }

    static readonly TemplateParts s_followNoShrink = new() { [ToggleButton.PartRoot] = static e => e with { Shrink = 0f, MinWidth = 0f } };

    /// <summary>The show's follow toggle ("✓ Following", the 40 square at Compact), for the library's show pane.</summary>
    internal static Element FollowToggle(string uri, string name) => Embed.Comp(new ShowFollowProps(uri, name), static () => new ShowFollow());

    sealed record ShowFollowProps(string Uri, string Name);

    /// <summary>Follow beside the primary: "✓ Following" / "+ Follow" (the neutral toggle, W-Show), the square 40 check toggle at Compact (W-Show-Compact).
    /// The state is the library's, visible at rest.</summary>
    sealed class ShowFollow : Component
    {
        public override Element Render()
        {
            var p = UseProps<ShowFollowProps>();
            var lib = Controls.Library;
            var facts = Wavee.Theming.TierContext.DrawnIn(UseContext(Wavee.Theming.ThemeFactsContext.Signal)).Value.Tiers;
            bool compact = WinUiDetailTiers.Of(in facts).Tier == WidthTier.Compact;
            bool on = lib is not null && p.Uri.Length > 0 && lib.IsSaved(p.Uri);
            string uri = p.Uri, name = p.Name;
            Action<bool> toggle = _ => lib?.ToggleSaved(uri, name);
            string label = Loc.Get(on ? Strings.Artist.Following : Strings.Artist.Follow);
            if (compact)
                return Controls.Named(Controls.IconAction(on ? Icons.Check : Icons.Add, () => lib?.ToggleSaved(uri, name), size: WinUiDetailRules.CompactTarget) with
                {
                    Key = "follow:square", Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                    BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                }, label);
            return ToggleButton.Controlled(Loc.Get(Strings.Artist.Follow), on, toggle, glyph: Icons.Add, checkedGlyph: Icons.Check,
                checkedLabel: Loc.Get(Strings.Artist.Following), style: WinUiDetailKit.NeutralToggle(ControlSize.Medium), parts: s_followNoShrink) with { Key = "follow:word" };
        }
    }
}
