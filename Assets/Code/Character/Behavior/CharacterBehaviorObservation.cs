namespace ThreeC.CharacterBehavior
{
    /// <summary>主线程一次行为裁决的结果。同一渲染帧内的先后转换按行为序号交付。</summary>
    public readonly struct CharacterBehaviorObservation
    {
        public CharacterBehaviorFrameResult Behavior { get; }
        public long FrameNumber => Behavior.FrameNumber;
        public float DeltaTime { get; }
        internal double Time { get; }
        public int SimulationVersion { get; }

        // 保存本段行为时间与连续性代次，供表现层去重和重绑。
        internal CharacterBehaviorObservation(CharacterBehaviorFrameResult behavior, float deltaTime, int simulationVersion, double? time = null)
        {
            Behavior = behavior;
            DeltaTime = deltaTime;
            Time = time ?? behavior.StepStartTime + deltaTime;
            SimulationVersion = simulationVersion;
        }
    }
}
