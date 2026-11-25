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
        // 提供该动作已获准的相对对齐窗口，默认动作沿用原有运动。
        internal virtual CharacterAlignmentWindow AlignmentWindow => default;
        /// <summary>在 (after, limit] 内返回下一切点；无切点时返回 limit 或正无穷。</summary>
        public abstract double NextBoundary(double after, double limit);

        /// <summary>积分半开区间 [start, end) 的动作运动，空区间返回零运动及空起步序号。</summary>
        public abstract CharacterMotionSample Sample(double start, double end);
    }

    // 让各类获准动作以相同的实例编号、相对时窗和修正预算接入对齐。
    internal readonly struct CharacterAlignmentWindow
    {
        internal readonly long Instance;
        internal readonly CharacterActionAlignmentSettings Settings;
        internal readonly double Start, End;

        // 保存捕获时刻为零点的窗口，界限已经过该动作的移动许可约束。
        internal CharacterAlignmentWindow(long instance, in CharacterActionAlignmentSettings settings, double start, double end)
        {
            Instance = instance;
            Settings = settings;
            Start = start;
            End = end;
        }
    }

    /// <summary>将区间运动与该动作可选的本地起步实例序号一并返回。</summary>
    public readonly struct CharacterMotionSample
    {
        public readonly CharacterBehaviorMotion Motion;
        public readonly long? StartInstance;
        internal readonly CharacterAlignmentPlan Alignment;

        /// <summary>保存区间运动及跨切片保持不变的起步实例序号。</summary>
        public CharacterMotionSample(CharacterBehaviorMotion motion, long? startInstance = null)
            : this(motion, startInstance, null) { }

        // 将该区间实际使用的对齐方案随运动传给物理结算。
        internal CharacterMotionSample(CharacterBehaviorMotion motion, long? startInstance, CharacterAlignmentPlan alignment)
        {
            Motion = motion;
            StartInstance = startInstance;
            Alignment = alignment;
        }
    }
}
