using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 六项已归一化误差的用途倍率，各分量可以独立关闭。
    [Serializable]
    internal sealed class GroundMatchWeights
    {
        // 保留本次装配的用途系数，避免后台评分读取正在编辑的共享资产。
        internal GroundMatchWeights Snapshot() => (GroundMatchWeights)MemberwiseClone();

        [Min(0f)] public float Position = 1f, Velocity = 1f, Contact = 1f, Path = 1f, Facing = 1f, Pace = 1f;
    }

    // 保存特征单位和五种查询用途的权重；默认分母与已有评分相同。
    [Serializable]
    internal sealed class GroundMatchTuning
    {
        [Range(0f, 0.3f)] public float HistorySeconds = 0.10f;
        [Range(0f, 1f)] public float HistoryWeight = 0.30f;
        [Min(0f)] public float PelvisWeight = 1.5f; // 根坐标系中的骨盆位置误差（平方米）。
        [Range(0.06f, 0.6f)] public float NearWindow = 0.15f;
        [Range(0.06f, 1f)] public float FarWindow = 0.30f;
        internal Vector2 Windows => new Vector2(Mathf.Clamp(NearWindow, 0.06f, 0.6f),
            Mathf.Max(Mathf.Clamp(NearWindow, 0.06f, 0.6f), Mathf.Clamp(FarWindow, 0.06f, 1f)));
        [Min(0.001f)] public float PositionScale = 0.4472136f, VelocityScale = 6.3245553f;
        [Min(0.001f)] public float ContactScale = 1f, PathScale = 0.5f, FacingScale = 45f, PaceScale = 0.7071068f;
        public GroundMatchWeights Start = new GroundMatchWeights(), Cycle = new GroundMatchWeights(),
            Turn = new GroundMatchWeights(), Stop = new GroundMatchWeights(), Transition = new GroundMatchWeights();

        // 固定模块的尺度、时间窗和各用途权重，直到角色下一次装配。
        internal GroundMatchTuning Snapshot()
        {
            var copy = (GroundMatchTuning)MemberwiseClone();
            copy.Start = Start?.Snapshot();
            copy.Cycle = Cycle?.Snapshot();
            copy.Turn = Turn?.Snapshot();
            copy.Stop = Stop?.Snapshot();
            copy.Transition = Transition?.Snapshot();
            return copy;
        }

        // 固定整次查询的特征系数，同一组系数用于入点与续播。
        internal GroundMatchCost Profile(GroundMotionUse use)
        {
            var weights = use switch
            {
                GroundMotionUse.Start => Start, GroundMotionUse.Turn => Turn,
                GroundMotionUse.Stop => Stop, GroundMotionUse.Transition => Transition, _ => Cycle
            };
            return new GroundMatchCost
            {
                Pelvis = Mathf.Max(0f, PelvisWeight), HistoryWeight = Mathf.Clamp01(HistoryWeight),
                Position = Factor(weights?.Position, PositionScale, 0.4472136f),
                Velocity = Factor(weights?.Velocity, VelocityScale, 6.3245553f),
                Contact = Factor(weights?.Contact, ContactScale, 1f),
                Path = Factor(weights?.Path, PathScale, 0.5f),
                Facing = Factor(weights?.Facing, FacingScale, 45f),
                Pace = Factor(weights?.Pace, PaceScale, 0.7071068f)
            };
        }

        // 将特征尺度转为平方误差乘数，并恢复无效序列化尺度。
        private static float Factor(float? weight, float scale, float fallback)
        {
            if (!(scale >= 0.001f) || float.IsInfinity(scale)) scale = fallback;
            return InputMath.NonNegative(weight ?? 1f, 1f) / (scale * scale);
        }
    }

    // 保存同一候选的姿势、轨迹、入场偏好及编辑器的特征分组诊断。
    internal struct GroundMatchCost
    {
        public float Position, Velocity, Contact, Path, Facing, Pace, Entry, Pelvis, Features;
        internal float HistoryWeight;
        internal bool MissingHistory;
#if UNITY_EDITOR
        internal GroundFeatureScoreBreakdown FeatureBreakdown;
#endif
        public float Total => Position + Velocity + Contact + Path + Facing + Pace + Entry + Pelvis + Features;
    }
}
