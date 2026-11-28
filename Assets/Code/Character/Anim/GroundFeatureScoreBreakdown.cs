#if UNITY_EDITOR
namespace ThreeC.CharacterBehavior
{
    // 累计同一次特征评分各组的贡献和实际纳入评分的通道位。
    internal struct GroundFeatureScoreBreakdown
    {
        internal float Feet, Body, Hands, Trajectory, Shared;
        internal uint Channels;

        // 通道只有一个有效组时归入该组，同时命中多个有效组时只计入共用项。
        internal void Add(int index, GroundFeatureGroups groups, float contribution)
        {
            Channels |= 1u << index;
            switch (groups)
            {
                case GroundFeatureGroups.Feet: Feet += contribution; break;
                case GroundFeatureGroups.Body: Body += contribution; break;
                case GroundFeatureGroups.Hands: Hands += contribution; break;
                case GroundFeatureGroups.Trajectory: Trajectory += contribution; break;
                default: Shared += contribution; break;
            }
        }
    }

}
#endif
