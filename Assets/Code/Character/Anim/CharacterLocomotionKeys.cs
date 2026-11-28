namespace ThreeC.CharacterBehavior
{
    /// <summary>基础移动意图的表现阶段；全身或局部外部动作仍由仲裁处理。</summary>
    public enum CharacterLocomotionPhase
    {
        Idle,
        Starting,
        Steady,
        Turning,
        MotionOverride,
        Stopping,
        BrakeLeadIn
    }

    // 标记地面片段在播放和过渡中的用途。
    internal enum CharacterLocomotionVariant { None, Start, TurnLeft, TurnRight, Stop, StopLeft, StopRight }

    // 统一地面基础步态的保留语义键，供内容和播放绑定共用。
    internal static class CharacterLocomotionKeys
    {
        public const string IdleKey = "$gait.idle";
        public const string MoveKey = "$gait.run";
        public const string WalkKey = "$gait.walk";
        public const string SprintKey = "$gait.sprint";
    }
}
