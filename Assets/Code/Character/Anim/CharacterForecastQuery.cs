using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在角色模拟时钟上独立查询步频速度和动作匹配轨迹。</summary>
    internal static class CharacterForecastQuery
    {
        // 从指定的未来起点构造姿势匹配所需的两个位置样本。
        internal static bool TryMatch(CharacterMovementForecast forecast, double now, float ahead, Vector2 windows, out CharacterMatchTrajectory match)
        {
            match = default;
            return TryControlled(forecast, now, ahead, out var origin) && TryMatchFrom(forecast, now, ahead, origin, windows, out match);
        }

        // 只接受当前角色仍掌握运动控制权的可信样本。
        private static bool TryControlled(CharacterMovementForecast forecast, double now, float ahead, out CharacterForecastSample sample)
        {
            sample = default;
            return forecast != null && forecast.TrySample(now, ahead, out sample) && !sample.ExternallyControlled;
        }

        // 两个位置都可信时，以已验证的同一原点计算角色局部轨迹。
        private static bool TryMatchFrom(CharacterMovementForecast forecast, double now, float ahead,
            in CharacterForecastSample origin, Vector2 windows, out CharacterMatchTrajectory match)
        {
            match = default;
            if (!TryControlled(forecast, now, ahead + windows.x, out var near) ||
                !TryControlled(forecast, now, ahead + windows.y, out var far)) return false;
            match = new CharacterMatchTrajectory(origin, near, far, windows);
            return match.Valid;
        }

        // 短窗决定步频速度，近远位置齐全时才启用匹配，关闭预测沿用原步投影。
        internal static CharacterGroundTrajectory Project(in CharacterControllerFrameResult frame, in CharacterGroundTrajectory original,
            CharacterMovementForecast forecast, double time, bool forecastEnabled, Vector2 windows)
        {
            if (!frame.Ground.IsValid) return original; // 外部轨迹自带它真实使用的窗长。
            if (!forecastEnabled)
                return windows == original.Match.Windows ? original : CharacterGroundTrajectory.FromGround(frame.Ground, windows);
            Vector3 velocity = frame.DeltaTime > 0f ? frame.ActualDisplacement / frame.DeltaTime : Vector3.zero;
            CharacterMatchTrajectory match = default;
            if (TryControlled(forecast, time, 0f, out var origin))
            {
                if (TryControlled(forecast, time, CharacterGroundTrajectory.LookAheadSeconds, out var speed))
                    velocity = speed.Velocity;
                TryMatchFrom(forecast, time, 0f, origin, windows, out match);
            }
            return original.WithForecast(velocity, match);
        }
    }
}
