// 2025/6/23 by Zhe Fang

using Wavee.Controls.Lyrics.Enums;
using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace Wavee.Controls.Lyrics.Helper
{
    public class EasingHelper
    {
        #region Interpolators

        // Interpolators are deterministic given (T, EasingType, EaseMode) — cache and share
        // a single Func instance across all call sites. Without this every ValueTransition
        // ctor allocated a fresh closure (lyrics relayout = ~500 closures per track change).
        private static readonly ConcurrentDictionary<(Type, EasingType, EaseMode), Delegate> _interpolatorCache = new();

        public static Func<T, T, double, T> GetInterpolatorByEasingType<T>(EasingType? type, EaseMode easingMode = EaseMode.Out)
            where T : INumber<T>, IFloatingPointIeee754<T>
        {
            var resolvedType = type ?? EasingType.Quad;
            var key = (typeof(T), resolvedType, easingMode);
            if (_interpolatorCache.TryGetValue(key, out var cached))
                return (Func<T, T, double, T>)cached;

            return BuildAndCacheInterpolator<T>(resolvedType, easingMode, key);
        }

        private static Func<T, T, double, T> BuildAndCacheInterpolator<T>(
            EasingType type, EaseMode easingMode, (Type, EasingType, EaseMode) key)
            where T : INumber<T>, IFloatingPointIeee754<T>
        {
            // Resolve the easeIn delegate ONCE here instead of inside the per-invocation
            // lambda body — saves a switch per Update tick on every ValueTransition.
            Func<T, T> easeInFunc = type switch
            {
                EasingType.Sine => EaseInSine,
                EasingType.Quad => EaseInQuad,
                EasingType.Cubic => EaseInCubic,
                EasingType.Quart => EaseInQuart,
                EasingType.Quint => EaseInQuint,
                EasingType.Expo => EaseInExpo,
                EasingType.Circle => EaseInCircle,
                EasingType.Back => EaseInBack,
                EasingType.Elastic => EaseInElastic,
                EasingType.Bounce => EaseInBounce,
                EasingType.SmoothStep => SmoothStep,
                EasingType.Linear => Linear,
                _ => EaseInQuad,
            };
            Func<T, T, double, T> built = (start, end, progress) =>
            {
                double t = Ease(progress, easingMode, easeInFunc);
                return start + ((end - start) * T.CreateChecked(t));
            };
            return (Func<T, T, double, T>)_interpolatorCache.GetOrAdd(key, built);
        }

        #endregion

        public static double Ease<T>(double t, EaseMode mode, Func<T, T> easeIn)
            where T : IFloatingPointIeee754<T>
        {
            t = Math.Clamp(t, 0.0, 1.0);

            T tt = T.CreateChecked(t);
            T half = T.CreateChecked(0.5);
            T two = T.CreateChecked(2);
            T tResult = mode switch
            {
                EaseMode.In => easeIn(tt),
                EaseMode.Out => T.One - easeIn(T.One - tt),
                EaseMode.InOut => tt < half
                    ? easeIn(tt * two) / two
                    : T.One - (easeIn((T.One - tt) * two) / two),
                _ => easeIn(tt),
            };

            return double.CreateChecked(tResult);
        }

        public static T EaseInSine<T>(T t) where T : IFloatingPointIeee754<T>
        {
            return T.One - T.Cos((t * T.Pi) / T.CreateChecked(2));
        }

        public static T EaseInQuad<T>(T t) where T : INumber<T>
        {
            return t * t;
        }

        public static T EaseInCubic<T>(T t) where T : INumber<T>
        {
            return t * t * t;
        }

        public static T EaseInQuart<T>(T t) where T : INumber<T>
        {
            return t * t * t * t;
        }

        public static T EaseInQuint<T>(T t) where T : INumber<T>
        {
            return t * t * t * t * t;
        }

        public static T EaseInExpo<T>(T t) where T : IFloatingPointIeee754<T>
        {
            if (t == T.Zero)
            {
                return T.Zero;
            }

            return T.Pow(T.CreateChecked(2), (T.CreateChecked(10) * t) - T.CreateChecked(10));
        }

        public static T EaseInCircle<T>(T t) where T : IFloatingPointIeee754<T>
        {
            return T.One - T.Sqrt(T.One - (t * t));
        }

        public static T EaseInBack<T>(T t) where T : IFloatingPointIeee754<T>
        {
            T c1 = T.CreateChecked(1.70158);
            T c3 = c1 + T.One;

            return (c3 * t * t * t) - (c1 * t * t);
        }

        public static T EaseInElastic<T>(T t) where T : IFloatingPointIeee754<T>
        {
            if (t == T.Zero || t == T.One)
            {
                return t;
            }

            const double springiness = 6;
            const double oscillations = 1;

            double td = double.CreateChecked(t);

            double expo = (Math.Exp(springiness * td) - 1.0) / (Math.Exp(springiness) - 1.0);
            double result = 0.7 * expo * Math.Sin((Math.PI * 2.0 * oscillations + (Math.PI * 0.5)) * td);

            return T.CreateChecked(result);
        }

        private static T EaseOutBounce<T>(T t) where T : IFloatingPointIeee754<T>
        {
            if (t < T.CreateChecked(4.0 / 11.0))
            {
                return (T.CreateChecked(121) * t * t) / T.CreateChecked(16);
            }
            else if (t < T.CreateChecked(8.0 / 11.0))
            {
                return ((T.CreateChecked(363.0 / 40.0) * t * t) - (T.CreateChecked(99.0 / 10.0) * t)) + T.CreateChecked(17.0 / 5.0);
            }
            else if (t < T.CreateChecked(9.0 / 10.0))
            {
                return ((T.CreateChecked(4356.0 / 361.0) * t * t) - (T.CreateChecked(35442.0 / 1805.0) * t)) + T.CreateChecked(16061.0 / 1805.0);
            }
            else
            {
                return ((T.CreateChecked(54.0 / 5.0) * t * t) - (T.CreateChecked(513.0 / 25.0) * t)) + T.CreateChecked(268.0 / 25.0);
            }
        }

        public static T EaseInBounce<T>(T t) where T : IFloatingPointIeee754<T>
        {
            return T.One - EaseOutBounce(T.One - t);
        }

        public static T SmoothStep<T>(T t) where T : IFloatingPointIeee754<T>
        {
            return t * t * (T.CreateChecked(3) - (T.CreateChecked(2) * t));
        }

        public static T CubicBezier<T>(T t, T p0, T p1, T p2, T p3) where T : IFloatingPointIeee754<T>
        {
            T u = T.One - t;

            return (u * u * u * p0)
                + (T.CreateChecked(3) * u * u * t * p1)
                + (T.CreateChecked(3) * u * t * t * p2)
                + (t * t * t * p3);
        }

        public static T Linear<T>(T t) where T : INumber<T> => t;
    }
}