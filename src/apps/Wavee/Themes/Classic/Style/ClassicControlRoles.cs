// -- Themes/Classic/Style/ClassicControlRoles.cs -- the control roles of Wavee Classic (themes plan EP15, lane WR) ------------------
//
// Every value is what Classic draws today, so publishing the table moves no pixel: the primary action is the Accent button at the
// medium size (`Controls.PrimaryButton`), the secondary the Standard button (the artist hero's Shuffle), the transport's play an
// accent disc, the follow toggle the medium `ButtonRules.FollowStyle`, the list row plate a 6-radius plate on the track rows' 8 inset
// with the 3 x 16 selection pill (`ClassicTrackRowLayout`, `RowMetrics.RowInset`), and a command is its glyph and its label.

using FluentGpu.Controls;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Classic;

/// <summary>The control roles of Wavee Classic.</summary>
public static class ClassicControlRoles
{
    /// <summary>Classic's control role table (one immutable instance, carried by <see cref="ClassicRoles.Table"/>).</summary>
    public static ControlRoleTable Table { get; } = new(
        PrimaryAction: new ButtonRoleSpec(ButtonAppearance.Accent, ControlSize.Medium),
        SecondaryAction: new ButtonRoleSpec(ButtonAppearance.Standard, ControlSize.Medium),
        Transport: new ButtonRoleSpec(ButtonAppearance.Accent, ControlSize.Large, Round: true),
        Follow: new FollowRoleSpec(ControlSize.Medium),
        ListBackplate: new ListBackplateSpec(Radius: 6f, Inset: 8f, Pill: new SelectionPill(Width: 3f, Height: 16f, Radius: 1.5f, Lead: 2f)),
        Command: CommandButtonStyle.Uniform(LabelMode.IconLabel));

    /// <summary>The least initials size of an imageless avatar: Classic's type floor, the micro rung (a 24 owner portrait's 42 % is 10.08;
    /// audit B6b).</summary>
    public const float InitialsFloor = 11f;

    static readonly PersonPicture.Style s_avatar = new() { InitialsFloor = InitialsFloor };

    /// <summary>The PersonPicture: the stock avatar, its initials never under <see cref="InitialsFloor"/> (where that fits in half the circle).</summary>
    public static PersonPicture.Style Avatar() => s_avatar;
}
