// ── Themes/Zune/Structure/Views/Playlist/ZunePlaylistPanels.cs — Zune's playlist access panel and Tune panel (themes plan 6.3; P5 Z6b1) ──────────
//
// Zune draws no board for either panel (owner Q18): both are built from Zune's parts, flat.
//
//   · the INVITE & ACCESS panel: the title in the label voice and the hint under it, the "copy invite link" call to action (the THEME accent, a flat
//     rectangle: it is chrome over the page, not the page) and, for an administrator who can edit the metadata, the two switches (the engine's switch,
//     which Zune's style draws square). `Playlist.AccessPanel` keeps the optimistic-switch rule and the verbs; the picture is these rows;
//   · the TUNE panel: the header line, the choices (the current one marked with a 2-dip accent bar and "current" trailing, not disabled-looking) and
//     "reset tuning" behind a rule ONLY while a tuning is selected. `PlaylistTuneMenuModel` decides which choices show and whether the reset row stands.
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

    // ══ 2. THE TUNE PANEL ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistTune"/>. <c>TableProfile.Tune</c> hands no anchor, so the panel opens as a centred dialog.</summary>
    public static Element TuneTemplate(PlaylistTuneProps p, in ViewContext ctx)
    {
        var options = p.Options;
        var kids = new List<Element>(options.Count + 4)
        {
            new BoxEl
            {
                Key = "tune:head", Direction = 1, Gap = Z.SwitchLabelGap, Padding = Edges4.All(Z.TuneHeaderPad),
                Children =
                [
                    R.Type.DenseTitle(Loc.Get(Strings.Detail.Tuning.FlyoutTitle)) with
                    {
                        Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    R.Type.DenseMeta(Loc.Get(Strings.Detail.Tuning.FlyoutSubtitle)) with
                    {
                        Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = Z.HintLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
            Rule(),
        };
        foreach (var option in PlaylistTuneMenuModel.VisibleChoices(options))
            kids.Add(TuneRow("tune:" + option.Identifier, option.DisplayName ?? "", string.Equals(option.Identifier, p.Selected, StringComparison.Ordinal),
                option.Identifier, p.Pick, reset: false));
        if (PlaylistTuneMenuModel.ResetOption(options, p.Selected) is { } resetOption)
        {
            kids.Add(Rule());
            kids.Add(TuneRow("tune:reset", Loc.Get(Strings.Detail.Tuning.Reset), false, resetOption.Identifier, p.Pick, reset: true));
        }
        return new BoxEl { Key = Z.TuneKey, Direction = 1, Width = p.Width, Children = kids.ToArray() };
    }

    /// <summary>One choice: a flat row, the current one marked with the accent bar at its lead and the word "current" at its end (nothing to click), a
    /// reset row as a command word.</summary>
    static Element TuneRow(string key, string label, bool current, string identifier, Action<string> pick, bool reset) => new BoxEl
    {
        Key = key, Direction = 0, Gap = Spacing.M, Height = Z.TuneRowH, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, 0f, Z.TuneRowPad, 0f),
        HoverFill = current ? ColorF.Transparent : R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
        Role = reset ? AutomationRole.MenuItem : AutomationRole.RadioButton, Focusable = !current, AllowFocusOnInteraction = false,
        Cursor = current ? CursorId.Arrow : CursorId.Hand, IsEnabled = !current, OnClick = current ? null : () => pick(identifier),
        Children =
        [
            new BoxEl { Width = Z.TuneMark, Height = Z.TuneRowH, Shrink = 0f, Fill = current ? Tok.AccentDefault : ColorF.Transparent },
            R.Type.TrackTitle(label) with
            {
                Color = current ? Tok.AccentTextPrimary : Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Case = reset ? R.Case.For(LabelKind.Command) : TextCase.AsWritten,
            },
            current
                ? R.Type.MicroMeta(Loc.Get(Strings.Detail.Tuning.Current)) with
                {
                    Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1,
                }
                : new BoxEl(),
        ],
    };
}
