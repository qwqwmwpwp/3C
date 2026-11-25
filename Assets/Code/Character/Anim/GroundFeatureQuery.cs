using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存后台搜索可读的通道快照，配置、有效位和采样值在一次规划期间固定。
    internal readonly struct GroundFeatureQuery
    {
        private readonly GroundFeatureLayout layout;
        private readonly Vector3[] observed;
        private readonly uint mask;
        internal bool Active => layout != null; // 没有可信通道时也不能临时切回另一套评分布局。

#if UNITY_EDITOR
        // 展示本次角色历史及表选维度的交集，避免把烘焙时可用位误读为本帧观测。
        internal uint VisibleMask(GroundFeatureGroups groups)
        {
            if (layout == null) return 0;
            uint selected = mask;
            for (int i = 0; i < layout.Channels.Length; i++)
                if ((layout.Channels[i].Group & groups) == 0 || layout.Channels[i].Weight <= 0f) selected &= ~(1u << i);
            return selected;
        }
#endif

        // 接管主线程已经复制的原始姿态与查询有效位。
        internal GroundFeatureQuery(GroundFeatureLayout layout, Vector3[] observed, uint mask)
        {
            this.layout = layout;
            this.observed = observed;
            this.mask = mask;
        }

        // 在同一组通道、时刻和逻辑倍率下评分入点与续播；不满足已启用历史的入点不可比较。
        internal float Score(CharacterMotionTrack track, float time, float rate, in CharacterMatchTrajectory goal,
            GroundFeatureGroups groups, bool allowPast, out bool missingHistory)
        {
            missingHistory = false;
            float cost = 0f;
            for (int i = 0; i < layout.Channels.Length; i++)
            {
                var c = layout.Channels[i];
                if ((mask & (1u << i)) == 0 || (c.Group & groups) == 0 || c.Weight <= 0f || !allowPast && c.Time < 0f) continue;
                float error;
                if (c.Path)
                {
                    bool near = Mathf.Approximately(c.Time, goal.Windows.x);
                    if (!near && !Mathf.Approximately(c.Time, goal.Windows.y)) continue; // 外部来源只提供其真实采样窗。
                    var path = GroundMotionPath.Relative(track, time, c.Time * rate);
                    if (c.Kind == GroundFeatureKind.TrajectoryPosition)
                        error = (path.Position - (near ? goal.Near : goal.Far)).sqrMagnitude;
                    else
                    {
                        float angle = Mathf.DeltaAngle(path.Yaw, near ? goal.NearYaw : goal.FarYaw);
                        error = angle * angle;
                    }
                }
                else
                {
                    float seconds = time + c.Time * rate;
                    if (!track.IsLoop && seconds < 0f)
                    {
                        missingHistory = true;
                        return float.PositiveInfinity;
                    }
                    float phase = track.IsLoop ? Mathf.Repeat(seconds / track.Seconds, 1f) : Mathf.Clamp01(seconds / track.Seconds);
                    float frame = phase * (track.Poses.Length - 1);
                    int a = Mathf.Min(Mathf.FloorToInt(frame), track.Poses.Length - 1), b = Mathf.Min(a + 1, track.Poses.Length - 1);
                    Vector3 value = Vector3.Lerp(track.FeatureValues[a * layout.Channels.Length + i],
                        track.FeatureValues[b * layout.Channels.Length + i], frame - a);
                    if (c.Kind == GroundFeatureKind.Velocity) value *= rate;
                    if (c.Kind == GroundFeatureKind.Heading)
                    {
                        float angle = Vector3.Angle(value, observed[i]);
                        error = angle * angle;
                    }
                    else error = (value - observed[i]).sqrMagnitude;
                }
                cost += error * c.Weight / (c.Scale * c.Scale);
            }
            return cost;
        }
    }
}
