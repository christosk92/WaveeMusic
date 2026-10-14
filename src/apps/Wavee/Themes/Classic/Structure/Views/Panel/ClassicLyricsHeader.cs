// ── Themes/Classic/Structure/Views/Panel/ClassicLyricsHeader.cs — Classic's `Panel.LyricsHeader` (themes plan 6.1, P3) ─────────────
//
// From `Shell/Rail.UI.cs` (`Rail.LyricsHeader`), `AiLyrics/AiLyrics.UI.Lyrics.cs` (`AiHeaderButton`) and `Screens/Diagnostics.UI.cs`
// (`LyricsInspectorButton`): the lyrics mode's header row. Title · (secondary-line toggle) · (AI sparkle) · expand · (inspector) · close,
// drawn with the panel header's own row and glyph buttons (`ClassicPanel.HeaderBox`, `HeaderButton`, `CloseButton`).
//
// The template runs on the slot's hook context and reads the header's facts there, so the capability and the prefs epoch subscribe the
// HEADER, not the whole rail (as the core component did). The toggle is composed only when the document on screen carries a second layer;
// the expand button shows in both arms (a track with no lyrics still opens a full stage). The sparkle and the inspector are components of
// their own, so only the sparkle subscribes the AI status and only the inspector the developer-mode setting: a progressive publish
// re-renders one 32-DIP glyph, a developer-mode flip composes the inspector in or out, and neither re-renders the row.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's lyrics header.</summary>
internal static class ClassicLyricsHeader
{
    /// <summary>The registered template of <see cref="ViewKeys.PanelLyricsHeader"/>.</summary>
    public static Element Template(PanelLyricsHeaderProps props, in ViewContext ctx)
    {
        var begin = ctx.Hooks.UseContext(SharedTransition.Begin);   // the stage's art flight is captured before the mount (null headless)
        var facts = props.Read();
        var kids = new List<Element>(6) { ClassicPanel.TitleText(facts.Title) };
        if (facts.HasSecondLine)
            kids.Add(ClassicPanel.HeaderButton(Icons.Globe, facts.SecondLineTip, props.ToggleSecondLine, active: facts.SecondLineActive));
        // The on-device AI sparkle: its own component (only it subscribes the AI status), hidden while the feature is off.
        kids.Add(Embed.Comp(props, static () => new ClassicLyricsSparkle()) with { Key = "ai-lyrics-header" });
        kids.Add(ClassicPanel.HeaderButton(Icons.FullScreen, facts.ExpandTip, () => props.Expand(begin)));
        kids.Add(Embed.Comp(props, static () => new ClassicLyricsInspectorButton()));
        kids.Add(ClassicPanel.CloseButton(props.Close));
        return ClassicPanel.HeaderBox(kids.ToArray());
    }
}

/// <summary>The sparkle. The root BoxEl is the component's host node: the pulse loop runs on ITS opacity channel, so the header's shared
/// glyph button stays untouched. The root's static Opacity is never declared, so a re-render never stomps the channel the loop (or the
/// settle spring) owns. While the current track is being timed the glyph breathes 1.0 → 0.55 → 1.0 on a looping opacity keyframe track
/// (an idle-cadence engine loop sampled on frame time — no spinner, no re-render).</summary>
internal sealed class ClassicLyricsSparkle : Component
{
    readonly Action _click;
    readonly Action _drivePulse;
    PanelLyricsHeaderProps? _props;
    LyricsSparkleFacts _facts;
    bool _pulse;    // this render's verdict (the layout effect reads it)
    bool _pulsed;   // a loop was seeded and has not been settled yet

    public ClassicLyricsSparkle()
    {
        // One stable handler that acts on this render's verdict (the core decides what a click does with it).
        _click = () => _props?.SparkleClick(_facts);
        _drivePulse = DrivePulse;
    }

    public override Element Render()
    {
        var props = _props = UseProps<PanelLyricsHeaderProps>();
        var facts = _facts = props.Sparkle();
        // Reduced motion is a VALUE folded into the verdict: the glyph simply stays at rest.
        _pulse = facts.Visible && facts.Working && !Design.Reduced;
        // Keyed on the verdict only: a progressive publish (LinesReady moves) re-renders the tooltip, never re-seeds
        // the loop mid-breath.
        UseLayoutEffect(_drivePulse, DepKey.From(_pulse));

        if (!facts.Visible) return new BoxEl { Visible = false };
        return new BoxEl
        {
            Shrink = 0f,
            Children = [ClassicPanel.HeaderButton(Icons.RefineSparkle, facts.Tip, _click, active: facts.Active)],
        };
    }

    void DrivePulse()
    {
        var ctx = Context;
        if (ctx.Anim is not { } anim || ctx.HostNode.IsNull) return;
        if (_pulse)
        {
            anim.Keyframes(ctx.HostNode, AnimChannel.Opacity, ClassicPanelLayout.SparklePulse, ClassicPanelLayout.SparklePulseMs, loop: true);
            _pulsed = true;
        }
        else if (_pulsed)
        {
            anim.Spring(ctx.HostNode, AnimChannel.Opacity, 1f, ClassicPanelLayout.SparkleSettle);
            _pulsed = false;
        }
    }
}

/// <summary>The developer-mode <c>&lt;/&gt;</c> glyph. Its own component so ONLY it subscribes the settings epoch: a developer-mode flip
/// mounts or removes it without re-rendering the header. The track is read at click time by the core's open verb.</summary>
internal sealed class ClassicLyricsInspectorButton : Component
{
    public override Element Render()
    {
        var props = UseProps<PanelLyricsHeaderProps>();
        var overlay = UseContext(Overlay.Service);
        // subscribes to the setting and to presentation mode → a flip of either composes the glyph in/out live
        if (!props.InspectorShown()) return new BoxEl { Visible = false };
        return ClassicPanel.HeaderButton(Icons.Code, Loc.Get(Strings.Player.InspectLyrics), () => props.OpenInspector(overlay));
    }
}
