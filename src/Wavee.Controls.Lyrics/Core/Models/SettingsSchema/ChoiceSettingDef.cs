using System.Collections.Generic;

namespace BetterLyrics.Core.Models.SettingsSchema
{
    public partial class ChoiceSettingDef : SettingDef
    {
        public List<string> Options { get; set; }
    }
}