using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>One row of a step list — a near-copy of <c>LoginView.LoginStepRow</c> generalized off the shared
/// <see cref="SetupStepState"/> vocabulary rather than the login takeover's own <c>LoginStep</c>/<c>LoginSnapshot</c>,
/// so the SAME row shape drives the Done page's four-row checklist. State arrives as a RE-PUSHED prop
/// (<see cref="Props"/> via <c>Embed.Comp</c> + <c>UseProps</c>) — not a signal this row owns, and not a frozen
/// constructor field either: <see cref="SetupStepList.Column"/> rebuilds the props tuple on every caller render (the
/// checklist's row states change as library sync/runtime provisioning settle), and a reused <c>ComponentEl</c> never
/// re-runs its factory, so a ctor field would freeze the row's very first state forever.
///
/// <para>Marks: <see cref="SetupStepState.Pending"/> = a dim bullet; <see cref="SetupStepState.Current"/> =
/// <c>ProgressRing.Indeterminate</c>; <see cref="SetupStepState.Done"/> = a checkmark with the same ~320ms
/// <c>ScaleX</c>/<c>ScaleY</c> pop keyframes <c>LoginStepRow</c> fires, keyed on the row's OWN transition into
/// <c>Done</c> (not a global animation flag); <see cref="SetupStepState.Attention"/> = a caution glyph;
/// <see cref="SetupStepState.Failed"/> = a critical X.</para></summary>
sealed class SetupStepRow : Component
{
    internal sealed record Props(string Label, SetupStepState State);

    public override Element Render()
    {
        var p = UseProps<Props>();
        bool current = p.State == SetupStepState.Current;
        bool done = p.State == SetupStepState.Done;

        var iconRef = UseRef<NodeHandle>(default);
        UseEffect(() =>
        {
            if (!done || Motion.ReducedMotion) return;
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || iconRef.Value.IsNull || !scene.IsLive(iconRef.Value)) return;
            var pop = new Keyframe[] { new(0f, 0.3f, Easing.EaseOut), new(0.55f, 1.18f, Easing.EaseOut), new(1f, 1f, Easing.EaseInOut) };
            anim.Keyframes(iconRef.Value, AnimChannel.ScaleX, pop, 320f, loop: false);
            anim.Keyframes(iconRef.Value, AnimChannel.ScaleY, pop, 320f, loop: false);
        }, done);

        Element mark = p.State switch
        {
            SetupStepState.Current => ProgressRing.Indeterminate(16f),
            SetupStepState.Failed => new TextEl(Icons.Cancel) { Size = 15f, FontFamily = Theme.IconFont, Color = Tok.SystemFillCritical },
            SetupStepState.Attention => new TextEl(Icons.StatusWarning) { Size = 15f, FontFamily = Theme.IconFont, Color = Tok.SystemFillCaution },
            SetupStepState.Done => new TextEl(Icons.Accept) { Size = 15f, FontFamily = Theme.IconFont, Color = Tok.AccentDefault },
            _ => new TextEl(Icons.RadioBullet) { Size = 11f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary },
        };

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = 26f,
            Enter = new EnterExit(Dx: -6f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
            Children =
            [
                new BoxEl
                {
                    Width = 18f, Height = 18f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    OnRealized = h => iconRef.Value = h, Children = [mark],
                },
                new TextEl(p.Label)
                {
                    Size = 12f, LineHeight = 16f,
                    Weight = current ? (ushort)600 : (ushort)400,
                    Color = current ? Tok.TextPrimary : done ? Tok.TextSecondary : Tok.TextTertiary,
                },
            ],
        };
    }
}

/// <summary>The <c>Stagger = 55f</c> column of <see cref="SetupStepRow"/>s — the Done page's checklist, both in the
/// stage column (Wide) and appended under the chips (Compact/Narrow).</summary>
static class SetupStepList
{
    public static Element Column(IReadOnlyList<(string Label, SetupStepState State)> steps)
    {
        var kids = new List<Element>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            kids.Add(Embed.Comp(new SetupStepRow.Props(step.Label, step.State), () => new SetupStepRow()) with { Key = "step:" + i });
        }
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, AlignSelf = FlexAlign.Stretch,
            Stagger = 55f,
            Children = kids.ToArray(),
        };
    }
}
