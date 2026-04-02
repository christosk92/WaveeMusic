// Ported from BetterLyrics by Zhe Fang

using System;
using System.Collections.Generic;
using Wavee.UI.WinUI.Controls.Lyrics.Models;

namespace Wavee.UI.WinUI.Controls.Lyrics.Helpers;

public class LyricsAnimator
{
    private readonly double _defaultScale = 0.75;
    private readonly double _highlightedScale = 1.0;

    public void UpdateLines(
        IList<RenderLyricsLine>? lines,
        int startIndex,
        int endIndex,
        int primaryPlayingLineIndex,
        double canvasHeight,
        double targetYScrollOffset,
        double playingLineTopOffsetFactor,
        LyricsStyleSettings lyricsStyle,
        LyricsEffectSettings lyricsEffect,
        ValueTransition<double> canvasYScrollTransition,
        NowPlayingPalette albumArtThemeColors,
        TimeSpan elapsedTime,
        bool isMouseScrolling,
        bool isLayoutChanged,
        bool isPrimaryPlayingLineChanged,
        bool isMouseScrollingChanged,
        bool isArtThemeColorsChanged,
        double currentPositionMs)
    {
        if (lines == null || lines.Count == 0) return;
        if (primaryPlayingLineIndex < 0 || primaryPlayingLineIndex >= lines.Count) return;

        var primaryPlayingLine = lines[primaryPlayingLineIndex];

        var phoneticOpacity = lyricsStyle.PhoneticLyricsOpacity / 100.0;
        var originalOpacity = lyricsStyle.UnplayedOriginalLyricsOpacity / 100.0;
        var translatedOpacity = lyricsStyle.TranslatedLyricsOpacity / 100.0;

        double topHeightFactor = canvasHeight * playingLineTopOffsetFactor;
        double bottomHeightFactor = canvasHeight * (1 - playingLineTopOffsetFactor);

        double scrollTopDurationSec = lyricsEffect.LyricsScrollTopDuration / 1000.0;
        double scrollTopDelaySec = lyricsEffect.LyricsScrollTopDelay / 1000.0;
        double scrollBottomDurationSec = lyricsEffect.LyricsScrollBottomDuration / 1000.0;
        double scrollBottomDelaySec = lyricsEffect.LyricsScrollBottomDelay / 1000.0;
        double canvasTransDuration = canvasYScrollTransition.DurationSeconds;

        bool isBlurEnabled = lyricsEffect.IsLyricsBlurEffectEnabled;
        bool isOutOfSightEnabled = lyricsEffect.IsLyricsOutOfSightEffectEnabled;
        bool isFanEnabled = lyricsEffect.IsFanLyricsEnabled;
        double fanAngleRad = Math.PI * (lyricsEffect.FanLyricsAngle / 180.0);
        bool isGlowEnabled = lyricsEffect.IsLyricsGlowEffectEnabled;
        bool isFloatEnabled = lyricsEffect.IsLyricsFloatAnimationEnabled;
        bool isScaleEnabled = lyricsEffect.IsLyricsScaleEffectEnabled;

        int safeStart = Math.Max(0, startIndex);
        int safeEnd = Math.Min(lines.Count - 1, endIndex + 1);

        for (int i = safeStart; i <= safeEnd; i++)
        {
            var line = lines[i];
            var lineHeight = line.PrimaryLineHeight;
            if (lineHeight == null || lineHeight <= 0) continue;

            bool isWordAnimationEnabled = lyricsEffect.WordByWordEffectMode switch
            {
                WordByWordEffectMode.Auto => line.IsPrimaryHasRealSyllableInfo,
                WordByWordEffectMode.Always => true,
                WordByWordEffectMode.Never => false,
                _ => line.IsPrimaryHasRealSyllableInfo
            };

            double targetCharFloat = lyricsEffect.IsLyricsFloatAnimationAmountAutoAdjust
                ? lineHeight.Value * 0.1
                : lyricsEffect.LyricsFloatAnimationAmount;
            double targetCharGlow = lyricsEffect.IsLyricsGlowEffectAmountAutoAdjust
                ? lineHeight.Value * 0.2
                : lyricsEffect.LyricsGlowEffectAmount;
            double targetCharScale = lyricsEffect.IsLyricsScaleEffectAmountAutoAdjust
                ? 1.15
                : lyricsEffect.LyricsScaleEffectAmount / 100.0;

            var maxAnimationDurationMs = Math.Max(line.EndMs ?? 0 - currentPositionMs, 0);

            bool isSecondaryLinePlaying = line.GetIsPlaying(currentPositionMs);
            bool isSecondaryLinePlayingChanged = line.IsPlayingLastFrame != isSecondaryLinePlaying;
            line.IsPlayingLastFrame = isSecondaryLinePlaying;

            if (isLayoutChanged || isPrimaryPlayingLineChanged || isMouseScrollingChanged || isSecondaryLinePlayingChanged || isArtThemeColorsChanged)
            {
                int lineCountDelta = i - primaryPlayingLineIndex;
                double distanceFromPlayingLine = Math.Abs(line.TopLeftPosition.Y - primaryPlayingLine.TopLeftPosition.Y);

                double distanceFactor;
                if (lineCountDelta < 0)
                    distanceFactor = Math.Clamp(distanceFromPlayingLine / topHeightFactor, 0, 1);
                else
                    distanceFactor = Math.Clamp(distanceFromPlayingLine / bottomHeightFactor, 0, 1);

                double yScrollDuration;
                double yScrollDelay;

                if (lineCountDelta < 0)
                {
                    yScrollDuration = canvasTransDuration + distanceFactor * (scrollTopDurationSec - canvasTransDuration);
                    yScrollDelay = distanceFactor * scrollTopDelaySec;
                }
                else if (lineCountDelta == 0)
                {
                    yScrollDuration = canvasTransDuration;
                    yScrollDelay = 0;
                }
                else
                {
                    yScrollDuration = canvasTransDuration + distanceFactor * (scrollBottomDurationSec - canvasTransDuration);
                    yScrollDelay = distanceFactor * scrollBottomDelaySec;
                }

                line.BlurAmountTransition.SetDuration(yScrollDuration);
                line.BlurAmountTransition.SetDelay(yScrollDelay);
                line.BlurAmountTransition.Start(
                    (isMouseScrolling || isSecondaryLinePlaying) ? 0 :
                    (isBlurEnabled ? (5 * distanceFactor) : 0));

                line.ScaleTransition.SetDuration(yScrollDuration);
                line.ScaleTransition.SetDelay(yScrollDelay);
                line.ScaleTransition.Start(
                    isSecondaryLinePlaying ? _highlightedScale :
                    (isOutOfSightEnabled ?
                    (_highlightedScale - distanceFactor * (_highlightedScale - _defaultScale)) :
                    _highlightedScale));

                line.PhoneticOpacityTransition.SetDuration(yScrollDuration);
                line.PhoneticOpacityTransition.SetDelay(yScrollDelay);
                line.PhoneticOpacityTransition.Start(
                    isSecondaryLinePlaying ? phoneticOpacity :
                    CalculateTargetOpacity(phoneticOpacity, phoneticOpacity, distanceFactor, isMouseScrolling, lyricsEffect));

                line.PlayedPrimaryOpacityTransition.SetDuration(yScrollDuration);
                line.PlayedPrimaryOpacityTransition.SetDelay(yScrollDelay);
                line.PlayedPrimaryOpacityTransition.Start(
                    isSecondaryLinePlaying ? 1.0 :
                    CalculateTargetOpacity(originalOpacity, 1.0, distanceFactor, isMouseScrolling, lyricsEffect));

                line.UnplayedPrimaryOpacityTransition.SetDuration(yScrollDuration);
                line.UnplayedPrimaryOpacityTransition.SetDelay(yScrollDelay);
                line.UnplayedPrimaryOpacityTransition.Start(
                    isSecondaryLinePlaying ? originalOpacity :
                    CalculateTargetOpacity(originalOpacity, originalOpacity, distanceFactor, isMouseScrolling, lyricsEffect));

                line.TranslatedOpacityTransition.SetDuration(yScrollDuration);
                line.TranslatedOpacityTransition.SetDelay(yScrollDelay);
                line.TranslatedOpacityTransition.Start(
                    isSecondaryLinePlaying ? translatedOpacity :
                    CalculateTargetOpacity(translatedOpacity, translatedOpacity, distanceFactor, isMouseScrolling, lyricsEffect));

                line.PlayedFillColorTransition.SetDuration(yScrollDuration);
                line.PlayedFillColorTransition.SetDelay(yScrollDelay);
                line.PlayedFillColorTransition.Start(isSecondaryLinePlaying ? albumArtThemeColors.PlayedCurrentLineFillColor : albumArtThemeColors.NonCurrentLineFillColor);

                line.UnplayedFillColorTransition.SetDuration(yScrollDuration);
                line.UnplayedFillColorTransition.SetDelay(yScrollDelay);
                line.UnplayedFillColorTransition.Start(isSecondaryLinePlaying ? albumArtThemeColors.UnplayedCurrentLineFillColor : albumArtThemeColors.NonCurrentLineFillColor);

                line.PlayedStrokeColorTransition.SetDuration(yScrollDuration);
                line.PlayedStrokeColorTransition.SetDelay(yScrollDelay);
                line.PlayedStrokeColorTransition.Start(isSecondaryLinePlaying ? albumArtThemeColors.PlayedTextStrokeColor : albumArtThemeColors.UnplayedTextStrokeColor);

                line.UnplayedStrokeColorTransition.SetDuration(yScrollDuration);
                line.UnplayedStrokeColorTransition.SetDelay(yScrollDelay);
                line.UnplayedStrokeColorTransition.Start(isSecondaryLinePlaying ? albumArtThemeColors.UnplayedTextStrokeColor : albumArtThemeColors.UnplayedTextStrokeColor);

                line.AngleTransition.SetInterpolator(canvasYScrollTransition.Interpolator);
                line.AngleTransition.SetDuration(yScrollDuration);
                line.AngleTransition.SetDelay(yScrollDelay);
                line.AngleTransition.Start(
                    (isFanEnabled && !isMouseScrolling) ?
                    fanAngleRad * distanceFactor * (i > primaryPlayingLineIndex ? 1 : -1) :
                    0);

                if (isLayoutChanged || isPrimaryPlayingLineChanged || isMouseScrollingChanged)
                {
                    line.YOffsetTransition.SetInterpolator(canvasYScrollTransition.Interpolator);
                    line.YOffsetTransition.SetDuration(yScrollDuration);
                    line.YOffsetTransition.SetDelay(yScrollDelay);
                    line.YOffsetTransition.Start(targetYScrollOffset);
                }
            }

            if (isWordAnimationEnabled)
            {
                if (isSecondaryLinePlayingChanged)
                {
                    // Glow animation (from line start to current)
                    if (isGlowEnabled && lyricsEffect.LyricsGlowEffectScope == LyricsEffectScope.LineStartToCurrentChar
                         && isSecondaryLinePlaying)
                    {
                        foreach (var renderChar in line.PrimaryRenderChars)
                        {
                            var stepInOutDuration = Math.Min(TimeConstants.AnimationDuration.TotalMilliseconds, maxAnimationDurationMs) / 2.0 / 1000.0;
                            var stepLastingDuration = Math.Max(maxAnimationDurationMs / 1000.0 - stepInOutDuration * 2, 0);
                            renderChar.GlowTransition.Start(
                                new Keyframe<double>(targetCharGlow, stepInOutDuration),
                                new Keyframe<double>(targetCharGlow, stepLastingDuration),
                                new Keyframe<double>(0, stepInOutDuration)
                            );
                        }
                    }

                    // Float animation (whole line)
                    if (isFloatEnabled)
                    {
                        foreach (var renderChar in line.PrimaryRenderChars)
                        {
                            if (isSecondaryLinePlaying)
                            {
                                if (renderChar.EndMs < currentPositionMs)
                                    renderChar.FloatTransition.JumpTo(0);
                                else
                                    renderChar.FloatTransition.Start(targetCharFloat);
                            }
                            else
                            {
                                renderChar.FloatTransition.Start(0);
                            }
                        }
                    }
                }

                // Float animation (per-char)
                foreach (var renderChar in line.PrimaryRenderChars)
                {
                    renderChar.ProgressPlayed = renderChar.GetPlayProgress(currentPositionMs);

                    bool isCharPlaying = renderChar.GetIsPlaying(currentPositionMs);
                    bool isCharPlayingChanged = renderChar.IsPlayingLastFrame != isCharPlaying;

                    if (isCharPlayingChanged)
                    {
                        if (isFloatEnabled)
                        {
                            renderChar.FloatTransition.SetDurationMs(Math.Min(lyricsEffect.LyricsFloatAnimationDuration, maxAnimationDurationMs));
                            renderChar.FloatTransition.Start(0);
                        }
                        renderChar.IsPlayingLastFrame = isCharPlaying;
                    }
                    else
                    {
                        if (!isCharPlaying && currentPositionMs > renderChar.EndMs && renderChar.FloatTransition.Value != 0)
                        {
                            renderChar.FloatTransition.SetDurationMs(Math.Min(lyricsEffect.LyricsFloatAnimationDuration, maxAnimationDurationMs));
                            renderChar.FloatTransition.Start(0);
                        }
                    }
                }

                foreach (var syllable in line.PrimaryRenderSyllables)
                {
                    bool isSyllablePlaying = syllable.GetIsPlaying(currentPositionMs);
                    bool isSyllablePlayingChanged = syllable.IsPlayingLastFrame != isSyllablePlaying;

                    if (isSyllablePlayingChanged)
                    {
                        // Scale
                        if (isScaleEnabled && isSyllablePlaying)
                        {
                            foreach (var renderChar in syllable.ChildrenRenderLyricsChars)
                            {
                                if (syllable.DurationMs >= lyricsEffect.LyricsScaleEffectLongSyllableDuration)
                                {
                                    var (inDuration, outDuration) = CalculateSegmentDuration(syllable.DurationMs / 1000.0, maxAnimationDurationMs / 1000.0);
                                    renderChar.ScaleTransition.Start(
                                        new Keyframe<double>(targetCharScale, inDuration),
                                        new Keyframe<double>(1.0, outDuration)
                                    );
                                }
                            }
                        }

                        // Glow (long syllable)
                        if (isGlowEnabled && isSyllablePlaying && lyricsEffect.LyricsGlowEffectScope == LyricsEffectScope.LongDurationSyllable
                            && syllable.DurationMs >= lyricsEffect.LyricsGlowEffectLongSyllableDuration)
                        {
                            foreach (var renderChar in syllable.ChildrenRenderLyricsChars)
                            {
                                var (inDuration, outDuration) = CalculateSegmentDuration(syllable.DurationMs / 1000.0, maxAnimationDurationMs / 1000.0);
                                renderChar.GlowTransition.Start(
                                    new Keyframe<double>(targetCharGlow, inDuration),
                                    new Keyframe<double>(0, outDuration)
                                );
                            }
                        }

                        syllable.IsPlayingLastFrame = isSyllablePlaying;
                    }
                }

                foreach (var renderChar in line.PrimaryRenderChars)
                {
                    renderChar.Update(elapsedTime);
                }
            }

            line.Update(elapsedTime);
        }
    }

    private static double CalculateTargetOpacity(double baseOpacity, double baseOpacityWhenZeroDistanceFactor, double distanceFactor, bool isMouseScrolling, LyricsEffectSettings lyricsEffect)
    {
        if (distanceFactor == 0)
            return baseOpacityWhenZeroDistanceFactor;

        if (isMouseScrolling)
            return baseOpacity;

        if (lyricsEffect.IsLyricsFadeOutEffectEnabled)
            return (1 - distanceFactor) * baseOpacity;

        return baseOpacity;
    }

    private static (double InDuration, double OutDuration) CalculateSegmentDuration(double desiredDuration, double maxDuration)
    {
        var inDuration = Math.Min(desiredDuration, maxDuration);
        var outDuration = Math.Min(maxDuration - inDuration, TimeConstants.AnimationDuration.TotalSeconds);
        return (inDuration, outDuration);
    }
}
