using System.Collections.Generic;

namespace Wavee.UI.WinUI.ViewModels;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ContributorVm
{
    public string? Name { get; init; }
    public string? Uri { get; init; }
    public string? ImageUrl { get; init; }
    public List<string>? Roles { get; init; }
    public string RolesText => Roles != null ? string.Join(", ", Roles) : "";
}