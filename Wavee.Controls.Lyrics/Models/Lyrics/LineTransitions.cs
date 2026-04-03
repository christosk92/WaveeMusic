using Wavee.Controls.Lyrics.Enums;
using Wavee.Controls.Lyrics.Helper;
using Microsoft.UI;
using System;
using Windows.UI;

namespace Wavee.Controls.Lyrics.Models.Lyrics
{
    public sealed class LineTransitions
    {
        public ValueTransition<double> Angle { get; }
        public ValueTransition<double> Blur { get; }
        public ValueTransition<double> PhoneticOpacity { get; }
        public ValueTransition<double> PlayedOpacity { get; }
        public ValueTransition<double> UnplayedOpacity { get; }
        public ValueTransition<double> TranslatedOpacity { get; }
        public ValueTransition<double> Scale { get; }
        public ValueTransition<double> YOffset { get; }
        public ValueTransition<Color> PlayedFill { get; }
        public ValueTransition<Color> UnplayedFill { get; }
        public ValueTransition<Color> PlayedStroke { get; }
        public ValueTransition<Color> UnplayedStroke { get; }

        public LineTransitions(double animationDuration)
        {
            Angle = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), defaultTotalDuration: animationDuration);
            Blur = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), defaultTotalDuration: animationDuration);
            PhoneticOpacity = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), defaultTotalDuration: animationDuration);
            PlayedOpacity = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), defaultTotalDuration: animationDuration);
            UnplayedOpacity = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), defaultTotalDuration: animationDuration);
            TranslatedOpacity = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), defaultTotalDuration: animationDuration);
            Scale = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), defaultTotalDuration: animationDuration);
            YOffset = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), defaultTotalDuration: animationDuration);
            PlayedFill = new(Colors.Transparent, defaultTotalDuration: 0.3f, interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to));
            UnplayedFill = new(Colors.Transparent, defaultTotalDuration: 0.3f, interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to));
            PlayedStroke = new(Colors.Transparent, defaultTotalDuration: 0.3f, interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to));
            UnplayedStroke = new(Colors.Transparent, defaultTotalDuration: 0.3f, interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to));
        }

        public void Update(TimeSpan elapsed)
        {
            Angle.Update(elapsed);
            Scale.Update(elapsed);
            Blur.Update(elapsed);
            PhoneticOpacity.Update(elapsed);
            PlayedOpacity.Update(elapsed);
            UnplayedOpacity.Update(elapsed);
            TranslatedOpacity.Update(elapsed);
            YOffset.Update(elapsed);
            PlayedFill.Update(elapsed);
            UnplayedFill.Update(elapsed);
            PlayedStroke.Update(elapsed);
            UnplayedStroke.Update(elapsed);
        }
    }
}
