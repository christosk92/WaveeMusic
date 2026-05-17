using System.Collections.Generic;

namespace Wavee.UI.WinUI.ViewModels;

public sealed class CreditGroupVm
{
    public string? RoleName { get; init; }
    public List<ContributorVm>? Contributors { get; init; }
}
