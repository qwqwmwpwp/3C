using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>把按世界时钟采样的预测转换为当前动作数据库使用的角色局部查询。</summary>
    internal static class CharacterForecastQuery
    {
        internal static bool TryMatch(CharacterMovementForecast forecast, double now, float ahead, out CharacterMatchTrajectory match)
        {
            match = default;
            if (forecast == null || !forecast.TrySample(now, ahead, out var origin) ||
                !forecast.TrySample(now, ahead + CharacterMatchTrajectory.NearSeconds, out var near) ||
                !forecast.TrySample(now, ahead + CharacterMatchTrajectory.FarSeconds, out var far)) return false;
            match = new CharacterMatchTrajectory(origin, near, far);
            return match.Valid;
        }

        internal static CharacterControllerFrameResult ForAnimation(CharacterControllerFrameResult frame,
            CharacterMovementForecast forecast, double time)
        {
            if (!frame.Trajectory.IsValid || !TryMatch(forecast, time, 0f, out var match) ||
                !forecast.TrySample(time, CharacterGroundTrajectory.LookAheadSeconds, out var speed)) return frame;
            return frame.WithTrajectory(frame.Trajectory.WithForecast(speed.Velocity, match));
        }
    }
}
