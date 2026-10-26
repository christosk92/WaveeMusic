// -- Themes/Zune/Style/ZuneControlRoles.cs -- the control roles of the Zune style (themes plan EP15, lane WR) ---------------------
//
// Every value is what Zune draws today. The ROLES name the same appearances as Classic's (an Accent primary, a Standard secondary, an
// accent transport disc, the medium follow toggle); what makes them Zune's is the control table (`ZuneControls`: the accent disc, the
// words), which the role lookup resolves through. The list row plate is square, flush with the list and marked by its fill alone (no
// pill: Z-State-Rows), and a command is words.

using FluentGpu.Controls;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>The control roles of the Zune style.</summary>
public static class ZuneControlRoles
{
    /// <summary>Zune's control role table (one immutable instance, carried by every <see cref="ZuneRoles"/> table).</summary>
    public static ControlRoleTable Table { get; } = new(
        PrimaryAction: new ButtonRoleSpec(ButtonAppearance.Accent, ControlSize.Medium),
        SecondaryAction: new ButtonRoleSpec(ButtonAppearance.Standard, ControlSize.Medium),
        Transport: new ButtonRoleSpec(ButtonAppearance.Accent, ControlSize.Large, Round: true),
        Follow: new FollowRoleSpec(ControlSize.Medium),
        ListBackplate: new ListBackplateSpec(Radius: 0f, Inset: 0f, Pill: null),
        Command: CommandButtonStyle.Uniform(LabelMode.Words));
}
