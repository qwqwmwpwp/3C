using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>为步频、停步与姿势匹配提供同一固定步的表现轨迹，距离均取世界水平面。</summary>
    public readonly struct CharacterGroundTrajectory
    {
        public const float LookAheadSeconds = 0.06f;
        public bool IsValid { get; }
        public Vector3 FutureVelocity { get; }
        public CharacterMatchTrajectory Match { get; }
        public bool IsBraking { get; }
        /// <summary>本固定步提交后，是否仍保有轻制动时间。</summary>
        public bool IsBrakeLeadIn { get; }
        /// <summary>本固定步中发生侧面接触，并且求解后沿预计行进方向损失至少一半速度。</summary>
        public bool IsBrakeObstructed { get; }
        public float InitialStopDistance { get; }
        public float InitialStopSeconds { get; }
        public float ActualStopTravel { get; }
        /// <summary>主制动累计的实际平面位移；跨阶段的一步按主制动占用时间计入。</summary>
        public float MainBrakeTravel { get; }
        public float RemainingStopDistance { get; }
        public float RemainingStopSeconds { get; }

        // 保留实测制动信息，仅用发布预测替换速度和匹配样本。
        internal CharacterGroundTrajectory WithForecast(Vector3 velocity, CharacterMatchTrajectory match)
            => new CharacterGroundTrajectory(velocity, IsBraking, IsBrakeLeadIn, IsBrakeObstructed, InitialStopDistance,
                InitialStopSeconds, ActualStopTravel, MainBrakeTravel, RemainingStopDistance, RemainingStopSeconds, match);

        // 按动画所需的时间窗投影本步运动，保留同一次求解记录的制动信息。
        internal static CharacterGroundTrajectory FromGround(in CharacterGroundObservation ground)
            => FromGround(ground, CharacterMatchTrajectory.DefaultWindows);

        // 按当前匹配模块的预测窗投影同一次运动结算。
        internal static CharacterGroundTrajectory FromGround(in CharacterGroundObservation ground, Vector2 windows)
        {
            if (!ground.IsValid) return default;
            var sample = ground.Prediction.Sample(LookAheadSeconds, windows.x, windows.y);
            var match = new CharacterMatchTrajectory(sample.NearTravel, sample.FarTravel, ground.Prediction.Facing, sample.NearYaw, sample.FarYaw, windows);
            return new CharacterGroundTrajectory(sample.Velocity, ground.IsBraking, ground.IsBrakeLeadIn, ground.IsBrakeObstructed,
                ground.InitialStopDistance, ground.InitialStopSeconds, ground.ActualStopTravel, ground.MainBrakeTravel,
                ground.RemainingStopDistance, ground.RemainingStopSeconds, match);
        }

        /// <summary>供运动来源提交自身预测。距离为世界水平米，时长为秒；普通移动可只填写未来速度和匹配轨迹。</summary>
        public CharacterGroundTrajectory(Vector3 futureVelocity, bool braking = false, bool leadIn = false, bool obstructed = false,
            float initialDistance = 0f, float initialSeconds = 0f, float travel = 0f, float mainTravel = 0f,
            float remainingDistance = 0f, float remainingSeconds = 0f, CharacterMatchTrajectory match = default)
        {
            IsValid = true;
            Match = match;
            FutureVelocity = Vector3.ProjectOnPlane(futureVelocity, Vector3.up);
            IsBraking = braking;
            IsBrakeLeadIn = leadIn;
            IsBrakeObstructed = obstructed;
            InitialStopDistance = initialDistance;
            InitialStopSeconds = initialSeconds;
            ActualStopTravel = travel;
            MainBrakeTravel = mainTravel;
            RemainingStopDistance = remainingDistance;
            RemainingStopSeconds = remainingSeconds;
        }
    }
}
