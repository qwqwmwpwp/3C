// 改编自 https://github.com/0xcjy/UmaViewer
// 来源提交 12c77723169259071ce86e2eab9c871de4a0919d，参考 LiveTimelineEasing.cs。
using System;

namespace ThreeC.AnimationCamera
{
    /// <summary>镜头插值缓动预设，数值编号与已有时间轴资产兼容。</summary>
    public enum CameraEaseType
    {
        Linear = 0,
        ExpoEaseOut = 1,
        ExpoEaseIn = 2,
        ExpoEaseInOut = 3,
        ExpoEaseOutIn = 4,
        CircEaseOut = 5,
        CircEaseIn = 6,
        CircEaseInOut = 7,
        CircEaseOutIn = 8,
        QuadEaseOut = 9,
        QuadEaseIn = 10,
        QuadEaseInOut = 11,
        QuadEaseOutIn = 12,
        SineEaseOut = 13,
        SineEaseIn = 14,
        SineEaseInOut = 15,
        SineEaseOutIn = 16,
        CubicEaseOut = 17,
        CubicEaseIn = 18,
        CubicEaseInOut = 19,
        CubicEaseOutIn = 20,
        QuartEaseOut = 21,
        QuartEaseIn = 22,
        QuartEaseInOut = 23,
        QuartEaseOutIn = 24,
        QuintEaseOut = 25,
        QuintEaseIn = 26,
        QuintEaseInOut = 27,
        QuintEaseOutIn = 28,
        ElasticEaseOut = 29,
        ElasticEaseIn = 30,
        ElasticEaseInOut = 31,
        ElasticEaseOutIn = 32,
        BounceEaseOut = 33,
        BounceEaseIn = 34,
        BounceEaseInOut = 35,
        BounceEaseOutIn = 36,
        BackEaseOut = 37,
        BackEaseIn = 38,
        BackEaseInOut = 39,
        BackEaseOutIn = 40
    }

    /// <summary>对归一化时间求缓动值，保持端点精确，并保留弹性、回弹与回退曲线的超调。</summary>
    public static class CameraEasing
    {
        private const double TwoPi = 6.28318531;
        private const double BackAmount = 1.70158;
        private const double ExpoResidual = 1.0 / 1024.0;

        /// <summary>检查枚举值是否属于已支持的缓动预设。</summary>
        public static bool IsValid(CameraEaseType type)
        {
            return (uint)type <= (uint)CameraEaseType.BackEaseOutIn;
        }

        /// <summary>将有限进度限制在 0～1，端点精确，区间内的缓动结果允许超调。</summary>
        /// <returns>预设有效且输入、结果均为有限数时返回 true。</returns>
        public static bool TryEvaluate(CameraEaseType type, float time, out float value)
        {
            value = 0f;
            if (!IsValid(type) || float.IsNaN(time) || float.IsInfinity(time))
                return false;

            if (time <= 0f)
                return true;
            if (time >= 1f)
            {
                value = 1f;
                return true;
            }
            if (type == CameraEaseType.Linear)
            {
                value = time;
                return true;
            }

            int index = (int)type - 1;
            int family = index / 4;
            int variant = index % 4;
            double sample;
            switch (variant)
            {
                case 0:
                    sample = EaseOut(family, time);
                    break;
                case 1:
                    sample = EaseIn(family, time);
                    break;
                case 2:
                    sample = EaseInOut(family, time);
                    break;
                default:
                    sample = EaseOutIn(family, time);
                    break;
            }

            if (double.IsNaN(sample) || double.IsInfinity(sample))
                return false;

            value = (float)sample;
            if (!float.IsNaN(value) && !float.IsInfinity(value))
                return true;

            value = 0f;
            return false;
        }

        private static double EaseOut(int family, double t)
        {
            // 保证组合曲线的两段在连接边界处取值精确。
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            switch (family)
            {
                case 0:
                    return (1.0 - Math.Pow(2.0, -10.0 * t)) / (1.0 - ExpoResidual); // 校正参考公式的残差，使终点值为 1。
                case 1:
                    t -= 1.0;
                    return Math.Sqrt(Math.Max(0.0, 1.0 - t * t));
                case 2:
                    return -t * (t - 2.0);
                case 3:
                    return Math.Sin(t * Math.PI * 0.5);
                case 4:
                    t -= 1.0;
                    return t * t * t + 1.0;
                case 5:
                    t -= 1.0;
                    return 1.0 - t * t * t * t;
                case 6:
                    t -= 1.0;
                    return t * t * t * t * t + 1.0;
                case 7:
                    return ElasticOut(t, 0.3);
                case 8:
                    if (t < 4.0 / 11.0)
                        return 7.5625 * t * t;
                    if (t < 8.0 / 11.0)
                    {
                        t -= 6.0 / 11.0;
                        return 7.5625 * t * t + 0.75;
                    }
                    if (t < 10.0 / 11.0)
                    {
                        t -= 9.0 / 11.0;
                        return 7.5625 * t * t + 0.9375;
                    }
                    t -= 21.0 / 22.0;
                    return 7.5625 * t * t + 63.0 / 64.0;
                case 9:
                    t -= 1.0;
                    return t * t * ((BackAmount + 1.0) * t + BackAmount) + 1.0;
                default:
                    return double.NaN;
            }
        }

        private static double EaseIn(int family, double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            switch (family)
            {
                case 0:
                    return (Math.Pow(2.0, (t - 1.0) * 10.0) - ExpoResidual)
                        / (1.0 - ExpoResidual);
                case 1:
                    return 1.0 - Math.Sqrt(Math.Max(0.0, 1.0 - t * t));
                case 2:
                    return t * t;
                case 3:
                    return 1.0 - Math.Cos(t * Math.PI * 0.5);
                case 4:
                    return t * t * t;
                case 5:
                    return t * t * t * t;
                case 6:
                    return t * t * t * t * t;
                case 7:
                    return ElasticIn(t, 0.3);
                case 8:
                    return 1.0 - EaseOut(family, 1.0 - t);
                case 9:
                    return t * t * ((BackAmount + 1.0) * t - BackAmount);
                default:
                    return double.NaN;
            }
        }

        private static double EaseInOut(int family, double t)
        {
            if (family == 3)
            {
                return (1.0 - Math.Cos(Math.PI * t)) * 0.5; // 正弦先缓入后缓出，修正参考实现误用的 OutIn 公式。
            }
            if (family == 7)
            {
                return t < 0.5
                    ? ElasticIn(t * 2.0, 0.45) * 0.5
                    : ElasticOut(t * 2.0 - 1.0, 0.45) * 0.5 + 0.5;
            }
            if (family == 9)
            {
                double amount = BackAmount * 1.525;
                double doubled = t * 2.0;
                if (doubled < 1.0)
                    return 0.5 * doubled * doubled * ((amount + 1.0) * doubled - amount);
                doubled -= 2.0;
                return 0.5 * (doubled * doubled * ((amount + 1.0) * doubled + amount) + 2.0);
            }

            return t < 0.5
                ? EaseIn(family, t * 2.0) * 0.5
                : EaseOut(family, t * 2.0 - 1.0) * 0.5 + 0.5;
        }

        private static double EaseOutIn(int family, double t)
        {
            return t < 0.5
                ? EaseOut(family, t * 2.0) * 0.5
                : EaseIn(family, t * 2.0 - 1.0) * 0.5 + 0.5;
        }

        private static double ElasticIn(double t, double period)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            double phase = period * 0.25;
            double shifted = t - 1.0;
            double sample = Math.Pow(2.0, shifted * 10.0)
                * Math.Sin((shifted - phase) * -TwoPi / period);
            double start = ExpoResidual * Math.Sin((-1.0 - phase) * -TwoPi / period);
            double end = Math.Sin(TwoPi * 0.25);
            return sample - ((1.0 - t) * start + t * (end - 1.0)); // 线性校正两端的残差，保留振荡形状。
        }

        private static double ElasticOut(double t, double period)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            double phase = period * 0.25;
            double sample = Math.Pow(2.0, -10.0 * t)
                * Math.Sin((t - phase) * TwoPi / period) + 1.0;
            double start = Math.Sin(-TwoPi * 0.25) + 1.0;
            double end = ExpoResidual * Math.Sin((1.0 - phase) * TwoPi / period) + 1.0;
            return sample - ((1.0 - t) * start + t * (end - 1.0));
        }
    }
}
