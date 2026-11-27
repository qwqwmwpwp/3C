namespace ThreeC.CharacterBehavior
{
    /// <summary>为活动动作提供可独立于行为实例执行的精确运动采样。</summary>
    public interface ICharacterIntervalMotion
    {
        /// <summary>冻结当前动作的数值和只读曲线，以便按任意有效区间采样。</summary>
        CharacterFrozenMotion FreezeMotion(in CharacterMotionCapture capture);
    }

    /// <summary>持有动作独占或只读共享的采样数据，所有区间均以捕获时刻为零点。</summary>
    public abstract class CharacterFrozenMotion
    {
        /// <summary>在 (after, limit] 内返回下一切点；无切点时返回 limit 或正无穷。</summary>
        public abstract double NextBoundary(double after, double limit);

        /// <summary>积分半开区间 [start, end) 的动作运动，空区间返回零运动及空起步序号。</summary>
        public abstract CharacterMotionSample Sample(double start, double end);
    }

    /// <summary>将区间运动与该动作可选的本地起步实例序号一并返回。</summary>
    public readonly struct CharacterMotionSample
    {
        public readonly CharacterBehaviorMotion Motion;
        public readonly long? StartInstance;

        /// <summary>保存区间运动及跨切片保持不变的起步实例序号。</summary>
        public CharacterMotionSample(CharacterBehaviorMotion motion, long? startInstance = null)
        {
            Motion = motion;
            StartInstance = startInstance;
        }
    }
}
