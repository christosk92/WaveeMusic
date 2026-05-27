using System;

namespace BetterLyrics.Core.Models.SettingsSchema
{
    public partial class ActionSettingDef : SettingDef
    {
        public string ButtonText { get; set; }
        public Action<string> Action { get; set; }
    }
}