namespace ThreeC.CharacterBehavior
{
    /// <summary>主线程一次行为裁决的结果。同一渲染帧内的先后转换按行为序号交付。</summary>
    internal readonly struct CharacterBehaviorObservation
    {
        internal CharacterBehaviorFrameResult Behavior { get; }
        internal long FrameNumber => Behavior.FrameNumber;
        internal float DeltaTime { get; }
        internal int SimulationVersion { get; }

        // 保存本段行为时间与连续性代次，供表现层去重和重绑。
        internal CharacterBehaviorObservation(CharacterBehaviorFrameResult behavior, float deltaTime, int simulationVersion)
        {
            Behavior = behavior;
            DeltaTime = deltaTime;
            SimulationVersion = simulationVersion;
        }
    }
}
