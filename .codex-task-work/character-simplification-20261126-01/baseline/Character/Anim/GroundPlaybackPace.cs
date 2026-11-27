using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 指定各步态相对素材步频的可用倍率；零范围沿用既有自由调速。
    [Serializable]
    internal sealed class GroundPaceLimits
    {
        public Vector2 Walk, Run, Sprint;
        [Min(0f)] public float ExcessPenalty = 1f;

        // 按候选自身的步态返回有序倍率范围。
        internal Vector2 For(GroundTrackGait gait) => gait switch
        {
            GroundTrackGait.Walk => Walk,
            GroundTrackGait.Run => Run,
            GroundTrackGait.Sprint => Sprint,
            _ => default
        };
    }

    // 保存一个状态的逻辑时钟规则，供匹配搜索与每个实际播放声部共用。
    internal readonly struct GroundPlaybackPace
    {
        internal readonly GroundMotionUse Use;
        private readonly float nominal, stateSpeed, excessWeight;
        private readonly Vector2 limits;

        // 绑定实际播放状态的基础倍率及当前步态的速度范围。
        internal GroundPlaybackPace(GroundMotionUse use, GroundTrackGait gait, float nominal,
            float stateSpeed, GroundPaceLimits tuning)
        {
            Use = use;
            this.nominal = nominal;
            this.stateSpeed = stateSpeed;
            limits = use == GroundMotionUse.Cycle && tuning != null ? tuning.For(gait) : default;
            excessWeight = tuning == null ? 0f : InputMath.NonNegative(tuning.ExcessPenalty, 1f);
        }

        // 计算逻辑片段每秒推进的秒数和超出步态范围的误差，子素材速率由混合配方处理。
        internal float Rate(float? actual, float? start, float braking, out float excess)
        {
            excess = 0f;
            if (Use == GroundMotionUse.Stop) return stateSpeed * braking;
            float? speed = Use == GroundMotionUse.Start ? start ?? actual : actual;
            if (!speed.HasValue || nominal <= 0f) return stateSpeed;
            float requested = Mathf.Max(0f, speed.Value / nominal);
            float applied = requested;
            if (limits.x > 0f && limits.y >= limits.x)
            {
                applied = Mathf.Clamp(requested, limits.x, limits.y);
                float difference = requested - applied;
                excess = difference * difference * excessWeight;
            }
            return stateSpeed * applied;
        }

        // 仅给起步声部预支可信预测的前向速度。
        internal static float StartSpeed(in CharacterAnimationMotion motion, float walkSpeed)
        {
            if (!motion.IsValid || motion.IsBlocked || !motion.Trajectory.IsValid) return motion.ActualSpeed;
            float forward = Vector3.Dot(motion.Trajectory.FutureVelocity, motion.ExpectedVelocity.normalized);
            return Mathf.Max(motion.ActualSpeed, Mathf.Clamp(forward, 0f, Mathf.Min(walkSpeed, motion.ExpectedSpeed)));
        }

        // 将剩余制动时间和位移进度换算为收步片段的追赶倍率。
        internal static float BrakeRate(CharacterGroundTrajectory trajectory, float marker, float time,
            float entry, float entryPhase)
        {
            if (trajectory.IsBrakeLeadIn && marker > 0f) return 0f;
            float distance = trajectory.MainBrakeTravel + trajectory.RemainingStopDistance;
            if (!trajectory.IsBraking || distance <= 0f || trajectory.RemainingStopSeconds <= 0f || time >= marker) return 1f;
            float phase = 1f - Mathf.Sqrt(Mathf.Clamp01(trajectory.RemainingStopDistance / distance));
            float progress = entry > 0f ? Mathf.InverseLerp(entryPhase, 1f, phase) : phase;
            float desired = Mathf.Lerp(entry, marker, progress);
            return Mathf.Clamp((marker - desired) / trajectory.RemainingStopSeconds + (desired - time) / 0.06f, 0.25f, 2.5f);
        }
    }
}
