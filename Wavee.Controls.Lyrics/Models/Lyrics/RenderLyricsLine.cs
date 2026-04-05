using Wavee.Controls.Lyrics.Enums;
using Wavee.Controls.Lyrics.Extensions;
using Wavee.Controls.Lyrics.Helper;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.UI;

namespace Wavee.Controls.Lyrics.Models.Lyrics
{
    public class RenderLyricsLine : BaseRenderLyrics
    {
        public List<RenderLyricsChar> PrimaryRenderChars { get; private set; } = [];
        public List<RenderLyricsSyllable> PrimaryRenderSyllables { get; private set; }

        public double AnimationDuration { get; set; } = 0.3;

        public LineTransitions Transitions { get; }

        // Convenience accessors for the most frequently used transitions
        public ValueTransition<double> AngleTransition => Transitions.Angle;
        public ValueTransition<double> BlurAmountTransition => Transitions.Blur;
        public ValueTransition<double> PhoneticOpacityTransition => Transitions.PhoneticOpacity;
        public ValueTransition<double> PlayedPrimaryOpacityTransition => Transitions.PlayedOpacity;
        public ValueTransition<double> UnplayedPrimaryOpacityTransition => Transitions.UnplayedOpacity;
        public ValueTransition<double> TranslatedOpacityTransition => Transitions.TranslatedOpacity;
        public ValueTransition<double> ScaleTransition => Transitions.Scale;
        public ValueTransition<double> YOffsetTransition => Transitions.YOffset;
        public ValueTransition<Color> PlayedFillColorTransition => Transitions.PlayedFill;
        public ValueTransition<Color> UnplayedFillColorTransition => Transitions.UnplayedFill;
        public ValueTransition<Color> PlayedStrokeColorTransition => Transitions.PlayedStroke;
        public ValueTransition<Color> UnplayedStrokeColorTransition => Transitions.UnplayedStroke;

        public CanvasTextLayout? PrimaryTextLayout { get; private set; }
        public CanvasTextLayout? SecondaryTextLayout { get; private set; }
        public CanvasTextLayout? TertiaryTextLayout { get; private set; }

        /// <summary>
        /// 原文坐标（相对于坐标原点）
        /// </summary>
        public Vector2 PrimaryPosition { get; set; }
        /// <summary>
        /// 译文坐标（相对于坐标原点）
        /// </summary>
        public Vector2 SecondaryPosition { get; set; }
        /// <summary>
        /// 注音坐标（相对于坐标原点）
        /// </summary>
        public Vector2 TertiaryPosition { get; set; }

        /// <summary>
        /// 顶部坐标（相对于坐标原点）
        /// </summary>
        public Vector2 TopLeftPosition { get; set; }
        /// <summary>
        /// 中心坐标（相对于坐标原点）
        /// </summary>
        public Vector2 CenterPosition { get; set; }
        /// <summary>
        /// 底部坐标（相对于坐标原点）
        /// </summary>
        public Vector2 BottomRightPosition { get; set; }

        public CanvasGeometry? PrimaryCanvasGeometry { get; private set; }
        public CanvasGeometry? SecondaryCanvasGeometry { get; private set; }
        public CanvasGeometry? TertiaryCanvasGeometry { get; private set; }

        public string PrimaryText { get; set; } = "";
        public string SecondaryText { get; set; } = "";
        public string TertiaryText { get; set; } = "";

        public CanvasCommandList? CachedStroke { get; private set; }
        public CanvasCommandList? CachedFill { get; private set; }

        public TintEffect? UnplayedFillTint { get; private set; }
        public TintEffect? UnplayedStrokeTint { get; private set; }
        public CompositeEffect? UnplayedComposite { get; private set; }

        private CropEffect? _primaryCropEffect;
        private GaussianBlurEffect? _primaryBlurEffect;
        private OpacityEffect? _primaryOpacityEffect;

        private CropEffect? _secondaryCropEffect;
        private GaussianBlurEffect? _secondaryBlurEffect;
        private OpacityEffect? _secondaryOpacityEffect;

        private CropEffect? _tertiaryCropEffect;
        private GaussianBlurEffect? _tertiaryBlurEffect;
        private OpacityEffect? _tertiaryOpacityEffect;

        public CanvasTextLayoutRegion[]? PrimaryTextRegions { get; private set; }

        public RenderLyricsRegion[]? RenderLyricsRegions { get; private set; }

        /// <summary>
        /// 轨道索引 (0 = 主轨道, 1 = 第一副轨道, etc.)
        /// 用于布局计算时的堆叠逻辑
        /// </summary>
        public int LaneIndex { get; set; } = 0;

        public double? PrimaryLineHeight => PrimaryRenderChars.FirstOrDefault()?.LayoutRect.Height;

        public bool IsPrimaryHasRealSyllableInfo { get; set; }

        public RenderLyricsLine(LyricsLine lyricsLine) : base(lyricsLine)
        {
            Transitions = new LineTransitions(AnimationDuration);

            StartMs = lyricsLine.StartMs;
            EndMs = lyricsLine.EndMs;
            TertiaryText = lyricsLine.TertiaryText;
            PrimaryText = lyricsLine.PrimaryText;
            SecondaryText = lyricsLine.SecondaryText;
            PrimaryRenderSyllables = lyricsLine.PrimarySyllables.Select(x => new RenderLyricsSyllable(x)).ToList();
            IsPrimaryHasRealSyllableInfo = lyricsLine.IsPrimaryHasRealSyllableInfo;
        }

        public void DisposeTextLayout()
        {
            TertiaryTextLayout?.Dispose();
            TertiaryTextLayout = null;

            PrimaryTextLayout?.Dispose();
            PrimaryTextLayout = null;

            SecondaryTextLayout?.Dispose();
            SecondaryTextLayout = null;
        }

        public void RecreateTextLayout(
            ICanvasAnimatedControl control,
            bool createPhonetic, bool createTranslated,
            int phoneticTextFontSize, int originalTextFontSize, int translatedTextFontSize,
            LyricsFontWeight fontWeight,
            string fontFamilyCJK, string fontFamilyWestern,
            double maxWidth, double maxHeight, TextAlignmentType type)
        {
            DisposeTextLayout();

            // 音译
            if (createPhonetic && !string.IsNullOrWhiteSpace(TertiaryText))
            {
                TertiaryTextLayout = new CanvasTextLayout(control, TertiaryText, new CanvasTextFormat
                {
                    HorizontalAlignment = CanvasHorizontalAlignment.Left,
                    VerticalAlignment = CanvasVerticalAlignment.Top,
                    FontSize = phoneticTextFontSize,
                    FontWeight = fontWeight.ToFontWeight(),
                }, (float)maxWidth, (float)maxHeight)
                {
                    HorizontalAlignment = type.ToCanvasHorizontalAlignment(),
                    Options = CanvasDrawTextOptions.NoPixelSnap,
                };
                TertiaryTextLayout.SetFontFamily(TertiaryText, fontFamilyCJK, fontFamilyWestern);
            }

            // 原文
            PrimaryTextLayout = new CanvasTextLayout(control, PrimaryText, new CanvasTextFormat
            {
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Top,
                FontSize = originalTextFontSize,
                FontWeight = fontWeight.ToFontWeight(),
            }, (float)maxWidth, (float)maxHeight)
            {
                HorizontalAlignment = type.ToCanvasHorizontalAlignment(),
                Options = CanvasDrawTextOptions.NoPixelSnap,
            };
            PrimaryTextLayout.SetFontFamily(PrimaryText, fontFamilyCJK, fontFamilyWestern);
            PrimaryTextRegions = PrimaryTextLayout.GetCharacterRegions(0, PrimaryText.Length);

            // 翻译
            if (createTranslated && !string.IsNullOrWhiteSpace(SecondaryText))
            {
                SecondaryTextLayout = new CanvasTextLayout(control, SecondaryText, new CanvasTextFormat
                {
                    HorizontalAlignment = CanvasHorizontalAlignment.Left,
                    VerticalAlignment = CanvasVerticalAlignment.Top,
                    FontSize = translatedTextFontSize,
                    FontWeight = fontWeight.ToFontWeight(),
                }, (float)maxWidth, (float)maxHeight)
                {
                    HorizontalAlignment = type.ToCanvasHorizontalAlignment(),
                    Options = CanvasDrawTextOptions.NoPixelSnap,
                };
                SecondaryTextLayout.SetFontFamily(SecondaryText, fontFamilyCJK, fontFamilyWestern);
            }
        }

        public void DisposeTextGeometry()
        {
            TertiaryCanvasGeometry?.Dispose();
            TertiaryCanvasGeometry = null;

            PrimaryCanvasGeometry?.Dispose();
            PrimaryCanvasGeometry = null;

            SecondaryCanvasGeometry?.Dispose();
            SecondaryCanvasGeometry = null;
        }

        public void RecreateTextGeometry()
        {
            DisposeTextGeometry();

            if (TertiaryTextLayout != null)
            {
                TertiaryCanvasGeometry = CanvasGeometry.CreateText(TertiaryTextLayout);
            }

            if (PrimaryTextLayout != null)
            {
                PrimaryCanvasGeometry = CanvasGeometry.CreateText(PrimaryTextLayout);
            }

            if (SecondaryTextLayout != null)
            {
                SecondaryCanvasGeometry = CanvasGeometry.CreateText(SecondaryTextLayout);
            }
        }

        public void RecreateRenderChars(int strokeWidth)
        {
            PrimaryRenderChars.Clear();
            if (PrimaryTextLayout == null) return;

            foreach (var syllable in PrimaryRenderSyllables)
            {
                syllable.ChildrenRenderLyricsChars.Clear();
            }

            var textLength = PrimaryText.Length;

            for (int startCharIndex = 0; startCharIndex < textLength; startCharIndex++)
            {
                var region = PrimaryTextLayout.GetCharacterRegions(startCharIndex, 1).FirstOrDefault();
                var bounds = region.LayoutBounds.Extend(
                    startCharIndex == 0 ? strokeWidth : strokeWidth / 4f,
                    strokeWidth / 2f,
                    startCharIndex == textLength - 1 ? strokeWidth : strokeWidth / 4f,
                    strokeWidth / 2f);

                var syllable = PrimaryRenderSyllables.FirstOrDefault(x => x.StartIndex <= startCharIndex && startCharIndex <= x.EndIndex);
                if (syllable == null) continue;

                var avgCharDuration = syllable.DurationMs / syllable.Length;
                var charStartMs = syllable.StartMs + (startCharIndex - syllable.StartIndex) * avgCharDuration;
                var charEndMs = charStartMs + avgCharDuration;

                var renderLyricsChar = new RenderLyricsChar(new BaseLyrics
                {
                    StartIndex = startCharIndex,
                    Text = PrimaryText[startCharIndex].ToString(),
                    StartMs = charStartMs,
                    EndMs = charEndMs,
                }, bounds);

                syllable.ChildrenRenderLyricsChars.Add(renderLyricsChar);

                PrimaryRenderChars.Add(renderLyricsChar);
            }
        }

        public void EnsureCaches(ICanvasResourceCreator resourceCreator, double strokeWidth)
        {
            if (CachedStroke != null && CachedFill != null) return;

            // 缓存纯白色的填充（作为 Fill Mask）
            CachedFill = new CanvasCommandList(resourceCreator);
            using (var ds = CachedFill.CreateDrawingSession())
            {
                if (TertiaryTextLayout != null) ds.DrawTextLayout(TertiaryTextLayout, TertiaryPosition, Colors.White);
                if (PrimaryTextLayout != null) ds.DrawTextLayout(PrimaryTextLayout, PrimaryPosition, Colors.White);
                if (SecondaryTextLayout != null) ds.DrawTextLayout(SecondaryTextLayout, SecondaryPosition, Colors.White);
            }

            CachedStroke = new CanvasCommandList(resourceCreator);

            // 缓存纯白色的描边（作为 Stroke Mask）
            if (strokeWidth > 0)
            {
                using var roundStrokeStyle = new CanvasStrokeStyle
                {
                    LineJoin = CanvasLineJoin.Round,
                    StartCap = CanvasCapStyle.Round,
                    EndCap = CanvasCapStyle.Round,
                };
                using var ds = CachedStroke.CreateDrawingSession();
                if (TertiaryCanvasGeometry != null) ds.DrawGeometry(TertiaryCanvasGeometry, TertiaryPosition, Colors.White, (float)strokeWidth, roundStrokeStyle);
                if (PrimaryCanvasGeometry != null) ds.DrawGeometry(PrimaryCanvasGeometry, PrimaryPosition, Colors.White, (float)strokeWidth, roundStrokeStyle);
                if (SecondaryCanvasGeometry != null) ds.DrawGeometry(SecondaryCanvasGeometry, SecondaryPosition, Colors.White, (float)strokeWidth, roundStrokeStyle);
            }

            UnplayedFillTint = new TintEffect { Source = CachedFill, Color = Colors.White };
            UnplayedStrokeTint = new TintEffect { Source = CachedStroke, Color = Colors.White };
            UnplayedComposite = new CompositeEffect { Sources = { UnplayedStrokeTint, UnplayedFillTint }, Mode = CanvasComposite.SourceOver };

            if (PrimaryTextRegions != null && (RenderLyricsRegions == null || RenderLyricsRegions.Length != PrimaryTextRegions.Length))
            {
                DisposeRenderLyricsRegions();
                RenderLyricsRegions = new RenderLyricsRegion[PrimaryTextRegions.Length];
                for (int i = 0; i < PrimaryTextRegions.Length; i++)
                {
                    RenderLyricsRegions[i] = new RenderLyricsRegion(CachedFill, CachedStroke);
                }
            }
        }

        public OpacityEffect GetPrimaryOverlayEffect(ICanvasImage source)
        {
            return EnsureOverlayEffect(ref _primaryCropEffect, ref _primaryBlurEffect, ref _primaryOpacityEffect, source);
        }

        public OpacityEffect GetSecondaryOverlayEffect(ICanvasImage source)
        {
            return EnsureOverlayEffect(ref _secondaryCropEffect, ref _secondaryBlurEffect, ref _secondaryOpacityEffect, source);
        }

        public OpacityEffect GetTertiaryOverlayEffect(ICanvasImage source)
        {
            return EnsureOverlayEffect(ref _tertiaryCropEffect, ref _tertiaryBlurEffect, ref _tertiaryOpacityEffect, source);
        }

        private static OpacityEffect EnsureOverlayEffect(
            ref CropEffect? cropEffect,
            ref GaussianBlurEffect? blurEffect,
            ref OpacityEffect? opacityEffect,
            ICanvasImage source)
        {
            cropEffect ??= new CropEffect { BorderMode = EffectBorderMode.Hard };
            blurEffect ??= new GaussianBlurEffect { Source = cropEffect, BorderMode = EffectBorderMode.Soft };
            opacityEffect ??= new OpacityEffect { Source = blurEffect };

            if (!ReferenceEquals(cropEffect.Source, source))
            {
                cropEffect.Source = source;
            }

            return opacityEffect;
        }

        private void DisposePrimaryRenderCharsEffects()
        {
            foreach (var cache in PrimaryRenderChars)
            {
                cache?.DisposeEffetcts();
            }
        }

        private void DisposeRenderLyricsRegions()
        {
            if (RenderLyricsRegions != null)
            {
                foreach (var region in RenderLyricsRegions)
                {
                    region?.Dispose();
                }
                RenderLyricsRegions = null;
            }
        }

        private void DisposeOverlayEffects()
        {
            _primaryOpacityEffect?.Dispose();
            _primaryBlurEffect?.Dispose();
            _primaryCropEffect?.Dispose();

            _secondaryOpacityEffect?.Dispose();
            _secondaryBlurEffect?.Dispose();
            _secondaryCropEffect?.Dispose();

            _tertiaryOpacityEffect?.Dispose();
            _tertiaryBlurEffect?.Dispose();
            _tertiaryCropEffect?.Dispose();

            _primaryOpacityEffect = null;
            _primaryBlurEffect = null;
            _primaryCropEffect = null;

            _secondaryOpacityEffect = null;
            _secondaryBlurEffect = null;
            _secondaryCropEffect = null;

            _tertiaryOpacityEffect = null;
            _tertiaryBlurEffect = null;
            _tertiaryCropEffect = null;
        }

        public void DisposeCaches()
        {
            UnplayedComposite?.Dispose();
            UnplayedStrokeTint?.Dispose();
            UnplayedFillTint?.Dispose();
            CachedStroke?.Dispose();
            CachedFill?.Dispose();

            UnplayedComposite = null;
            UnplayedStrokeTint = null;
            UnplayedFillTint = null;
            CachedStroke = null;
            CachedFill = null;

            DisposeRenderLyricsRegions();
            DisposePrimaryRenderCharsEffects();
            DisposeOverlayEffects();
        }

        public void Update(TimeSpan elapsedTime)
        {
            Transitions.Update(elapsedTime);
        }

    }
}
