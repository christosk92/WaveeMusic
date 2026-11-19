// ── Themes/Zune/Structure/Views/Playlist/ZunePlaylistPanels.cs — Zune's playlist access panel and Tune strip (themes plan 6.3; P5 Z6b1) ──────────
//
// Zune draws no board for either panel (owner Q18): both are built from Zune's parts, flat.
//
//   · the INVITE & ACCESS panel: the title in the label voice and the hint under it, the "copy invite link" call to action (the THEME accent, a flat
//     rectangle: it is chrome over the page, not the page) and, for an administrator who can edit the metadata, the two switches (the engine's switch,
//     which Zune's style draws square). `Playlist.AccessPanel` keeps the optimistic-switch rule and the verbs; the picture is these rows;
//   · the TUNE strip, inline under the list bar while Tune is open: the AI bar at its lead, its head, the directions as Zune's flat chips, and "reset tuning"
//     ONLY while a tuning is selected. `PlaylistTuneMenuModel` decides which choices show and whether Reset stands.
//
// Numbers: `Rules\ZunePlaylistRules.cs`.

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
using Z = Wavee.Themes.Zune.ZunePlaylistRules;

namespace Wavee.Themes.Zune;

internal static partial class ZunePlaylist
{
    // ══ 1. THE ACCESS PANEL ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistAccess"/>.</summary>
    public static Element AccessTemplate(PlaylistAccessProps p, in ViewContext ctx)
    {
        var lay = ZunePlaylistTiers.Of(ctx.Tier());
        var kids = new List<Element>(6)
        {
            R.Type.DenseTitle(Loc.Get(Strings.Detail.Edit.InviteCollaborators)) with
            {
                Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            R.Type.DenseMeta(Loc.Get(Strings.Detail.Edit.InviteHint)) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = Z.HintLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (p.Admin) kids.Add(InviteCta(p, lay));
        if (p.Admin && p.EditableMetadata)
        {
            kids.Add(Rule());
            kids.Add(SwitchRow("access:collab", Loc.Get(Strings.Detail.Edit.Collaborative), Loc.Get(Strings.Detail.Edit.CollaborativeHint),
                p.Collaborative, p.SavingCollab, p.OnCollaborative));
            kids.Add(SwitchRow("access:public", Loc.Get(Strings.Detail.Edit.PublicPlaylist),
                Loc.Get(p.IsPublic ? Strings.Detail.Access.PublicCaption : Strings.Detail.Access.PrivateCaption),
                p.Public, p.SavingPublic, p.OnPublic));
        }
        return new BoxEl
        {
            Key = Z.AccessKey, Direction = 1, Width = p.Width, Padding = Edges4.All(Z.PanelPad), Gap = Z.PanelGap, Children = kids.ToArray(),
        };
    }

    /// <summary>The "copy invite link" call to action: a flat accent rectangle, its word in the on-accent ink; a ring while the link is made, a tick once copied.</summary>
    static Element InviteCta(PlaylistAccessProps p, ZunePlaylistLayout lay)
    {
        int state = p.InviteState;
        ColorF fill = Tok.AccentDefault;
        ColorF ink = ColorContrast.PickContrast(fill);
        Element glyph = state == 1
            ? ProgressRing.Indeterminate(Z.CtaGlyph, foreground: ink)
            : Ui.Icon(state == 2 ? Icons.Accept : Icons.Link, Z.CtaGlyph, ink);
        return new BoxEl
        {
            Key = "access:cta", Direction = 0, Gap = Spacing.S, Height = MathF.Max(Z.CtaH, lay.Target), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = fill, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = state != 1, OnClick = p.CopyInvite,
            Children =
            [
                new BoxEl { Key = "cta-ico:" + state, Animate = MotionRecipes.IconSwap, Children = [glyph] },
                new BoxEl
                {
                    Key = "cta-txt:" + (state == 2 ? 1 : 0), Animate = MotionRecipes.TextSwap,
                    Children =
                    [
                        R.Type.DenseTitle(Loc.Get(state == 2 ? Strings.Auth.Copied : Strings.Detail.Edit.CopyInviteLink)) with
                        {
                            Color = ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                        },
                    ],
                },
            ],
        };
    }

    static Element Rule() => new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, Z.RuleGap, 0f, Z.RuleGap) };

    static Element SwitchRow(string key, string label, string caption, Func<Signal<bool>> value, bool saving, Action<bool> onChange) => new BoxEl
    {
        Key = key, Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Z.SwitchLabelGap,
                Children =
                [
                    R.Type.DenseTitle(label) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    R.Type.MicroMeta(caption) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, MinWidth = 0f },
                ],
            },
            saving ? Status(saved: false) : new BoxEl(),
            ToggleSwitch.Create(value(), onChange, isEnabled: !saving),
        ],
    };

    // ══ 2. THE TUNE STRIP (inline since 2026-11-18) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistTune"/>: the strip under the list bar while Tune is open (owner, 2026-11-18: "not a dialog but
    /// a nicer experience"). Flat, in Zune's voice: the AI bar at its lead; its head ("tune this mix" in the label voice over the line that says what the mix
    /// follows, that it is rebuilding, or that the last pick failed; "reset tuning" as a command word while tuned; the close), then the directions as Zune's
    /// flat chips (the mix's own direction in the accent; while one applies that one is lit, the others wait and the head's ring turns).</summary>
    public static Element TuneTemplate(PlaylistTuneProps p, in ViewContext ctx)
    {
        var choices = PlaylistTuneMenuModel.VisibleChoices(p.Options);
        var reset = PlaylistTuneMenuModel.ResetOption(p.Options, p.Selected);
        bool busy = p.Applying is not null;
        var pick = p.Pick;
        float target = ZunePlaylistTiers.Of(ctx.Tier()).Target;

        var chips = new Element[choices.Count];
        for (int i = 0; i < choices.Count; i++)
        {
            string id = choices[i].Identifier;
            bool on = busy ? id == p.Applying : id == p.Selected;
            chips[i] = TuneChip("tune:" + id, choices[i].DisplayName ?? "", on, !busy, busy ? null : () => pick(id), target);
        }

        var head = new List<Element>(4) { TuneLines(p, choices, busy), new BoxEl { Grow = 1f, MinWidth = 0f } };
        if (reset is { } r && !busy)
        {
            string resetId = r.Identifier;
            head.Add(new BoxEl
            {
                Key = "tune:reset", Direction = 0, AlignItems = FlexAlign.Center, Height = target, Shrink = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => pick(resetId),
                Children = [R.Type.DenseTitle(Loc.Get(Strings.Detail.Tuning.Reset)) with { Color = R.Ink.AccentAction, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
            });
        }
        head.Add(ToolTip.Wrap(Controls.IconAction(Icons.ChromeClose, p.Close, size: Z.TuneClose) with { Key = "tune:close" }, Loc.Get(Strings.Detail.Tuning.Close)));

        return new BoxEl
        {
            Key = Z.TuneKey, Direction = 0, Gap = Z.TuneBarGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children =
            [
                new BoxEl { Key = "tune-strip:bar", Width = Z.TuneBarW, Shrink = 0f, Gradient = Z.TuneBar },
                new BoxEl
                {
                    Key = "tune-strip", Direction = 1, Gap = Z.TuneStripGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Key = "tune-strip:head", Direction = 0, Gap = Z.TuneHeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = head.ToArray() },
                        new BoxEl { Key = "tune-strip:chips", Direction = 0, Wrap = true, Gap = Z.TuneChipGap, MinWidth = 0f, Children = chips },
                    ],
                },
            ],
        };
    }

    /// <summary>"tune this mix" over its line: the failure, the rebuild (with the ring), the direction the mix follows, or the invitation.</summary>
    static Element TuneLines(PlaylistTuneProps p, List<TuneOption> choices, bool busy)
    {
        string? name = null;
        if (p.Selected is { } selected) foreach (var c in choices) if (c.Identifier == selected) { name = c.DisplayName; break; }
        string line = p.Failed ? Loc.Get(Strings.Detail.Tuning.ApplyFailed)
            : busy ? Loc.Get(Strings.Detail.Tuning.Applying)
            : name is not null ? Strings.Detail.Tuning.Tuned(direction: name)
            : Loc.Get(Strings.Detail.Tuning.Subtitle);
        var text = R.Type.DenseMeta(line) with
        {
            Color = p.Failed ? Tok.SystemFillCritical : Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        Element row = busy
            ? new BoxEl { Direction = 0, Gap = Z.TuneRingGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [ProgressRing.Create(size: Z.TuneRing), text] }
            : text;
        return new BoxEl
        {
            Direction = 1, Gap = Z.TuneLineGap, Shrink = 1f, MinWidth = 0f,
            Children =
            [
                R.Type.DenseTitle(Loc.Get(Strings.Detail.Tuning.Title)) with
                {
                    Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                row,
            ],
        };
    }

    /// <summary>One direction: Zune's flat chip (a wash at rest, the accent with the on-accent ink when on), <paramref name="target"/> tall; a waiting chip is shown
    /// and disabled.</summary>
    static Element TuneChip(string key, string label, bool on, bool available, Action? click, float target) => new BoxEl
    {
        Key = key, Direction = 0, Height = target, MinWidth = target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Z.ChipPadX, 0f, Z.ChipPadX, 0f),
        Fill = on ? Tok.AccentDefault : R.Layer.RowHover,
        HoverFill = on ? Tok.AccentSecondary : R.Layer.ChromeHover, PressedFill = on ? Tok.AccentTertiary : R.Layer.RowPressed,
        Opacity = available || on ? 1f : ZuneBandRules.DisabledOpacity,
        Role = AutomationRole.ToggleButton, Focusable = available, AllowFocusOnInteraction = false, IsEnabled = available,
        Cursor = available ? CursorId.Hand : (CursorId?)null, OnClick = click, BrushTransitionMs = Design.Motion.Faster,
        Children =
        [
            R.Type.DenseTitle(label) with { Color = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f },
        ],
    };
}
