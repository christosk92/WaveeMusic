using Wavee.Controls.Lyrics.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Wavee.Controls.Lyrics.Models.Settings
{
    public partial class AlbumArtAreaEffectSettings : ObservableRecipient, ICloneable
    {
        [ObservableProperty][NotifyPropertyChangedRecipients] public partial ImageSwitchType ImageSwitchType { get; set; } = ImageSwitchType.Crossfade;
        [ObservableProperty][NotifyPropertyChangedRecipients] public partial bool SongInfoAutoScroll { get; set; } = true;

        public AlbumArtAreaEffectSettings() { }

        public object Clone()
        {
            return new AlbumArtAreaEffectSettings
            {
                ImageSwitchType = this.ImageSwitchType,
                SongInfoAutoScroll = this.SongInfoAutoScroll,
            };
        }
    }
}
