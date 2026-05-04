using System;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Update(Action<AppSettings> modifier);
    Task SaveAsync();
    Task LoadAsync();
}
