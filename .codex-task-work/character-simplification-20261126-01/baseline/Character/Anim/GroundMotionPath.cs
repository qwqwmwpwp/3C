using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 计算候选脚姿和预测轨迹的匹配代价。
    internal static class GroundMotionCost
    {
        // 汇总指定入点的脚位、脚速、接触、路径、朝向和步频代价。
        public static GroundMatchCost Evaluate(CharacterMotionTrack track, float time, in CharacterFootPose sample, in CharacterFootObservation feet,
            in CharacterMatchTrajectory goal, float speed, float rate, float excess, in GroundMatchCost profile, bool customFeatures,
            GroundFeatureGroups groups, bool allowPast, bool legacyPelvis)
        {
            float expectedPace = goal.Near.magnitude / goal.Windows.x;
            float ratio = Mathf.Lerp(speed, expectedPace, 0.5f) / Mathf.Max(0.01f, track.ReferenceSpeed);
            float pace = track.IsLoop ? Mathf.Log(Mathf.Clamp(ratio, 0.20f, 5f)) : 0f;
            if (customFeatures) return new GroundMatchCost { Contact = feet.ContactCost(sample.Contact) * profile.Contact,
                Pace = (pace * pace + excess) * profile.Pace };
            var near = GroundMotionPath.Relative(track, time, goal.Windows.x * rate);
            var far = GroundMotionPath.Relative(track, time, goal.Windows.y * rate);
            float a = Mathf.DeltaAngle(near.Yaw, goal.NearYaw), b = Mathf.DeltaAngle(far.Yaw, goal.FarYaw);
            float position = (sample.Left - feet.Pose.Left).sqrMagnitude + (sample.Right - feet.Pose.Right).sqrMagnitude;
            if (allowPast && feet.PastSeconds > 0f && profile.HistoryWeight > 0f && (groups & GroundFeatureGroups.Feet) != 0)
            {
                if (!track.TrySamplePose(time - feet.PastSeconds * rate, out var past))
                    return new GroundMatchCost { Position = float.PositiveInfinity, MissingHistory = true };
                float history = (past.Left - feet.Past.Left).sqrMagnitude + (past.Right - feet.Past.Right).sqrMagnitude;
                position = Mathf.Lerp(position, history, profile.HistoryWeight);
            }
            var cost = new GroundMatchCost
            {
                Position = position * profile.Position,
                Pelvis = legacyPelvis && feet.Pose.HasPelvis != 0 ?
                    (sample.Pelvis - feet.Pose.Pelvis).sqrMagnitude * profile.Pelvis : 0f,
                Velocity = ((sample.LeftVelocity * rate - feet.Pose.LeftVelocity).sqrMagnitude +
                    (sample.RightVelocity * rate - feet.Pose.RightVelocity).sqrMagnitude) * profile.Velocity,
                Contact = feet.ContactCost(sample.Contact) * profile.Contact,
                Path = ((near.Position - goal.Near).sqrMagnitude + (far.Position - goal.Far).sqrMagnitude) * profile.Path,
                Facing = (a * a + b * b) * profile.Facing,
                Pace = (pace * pace + excess) * profile.Pace
            };
            if ((groups & GroundFeatureGroups.Feet) == 0) cost.Position = cost.Velocity = 0f;
            if ((groups & GroundFeatureGroups.Body) == 0) cost.Pelvis = 0f;
            if ((groups & GroundFeatureGroups.Trajectory) == 0) cost.Path = cost.Facing = 0f;
            return cost;
        }
    }

    // 在素材逻辑时间轴上计算局部参考轨迹。
    internal static class GroundMotionPath
    {
        // 计算素材时间段的相对根路径，非循环仅累计到实际末帧。
        public static GroundPathKey Relative(CharacterMotionTrack track, float time, float horizon)
        {
            if (!track.IsLoop) horizon = Mathf.Clamp(horizon, 0f, Mathf.Max(0f, track.Seconds - time));
            if (track.PathSource == GroundPathSource.Straight)
                return new GroundPathKey { Position = Vector2.up * track.Distance(time, horizon) };
            var keys = track.PathSource == GroundPathSource.RootMotion ? track.RootPath : track.ReferencePath;
            var start = Sample(keys, time);
            float endTime = time + horizon;
            var end = Sample(keys, endTime);
            if (track.IsLoop && endTime >= track.Seconds)
            {
                var cycle = Sample(keys, track.Seconds);
                int cycles = Mathf.FloorToInt(endTime / track.Seconds);
                end = Sample(keys, Mathf.Repeat(endTime, track.Seconds));
                // 按二进制幂组合局部根轨迹，极高倍率时仍然有界。
                while (cycles > 0)
                {
                    if ((cycles & 1) != 0) end = Compose(cycle, end);
                    cycle = Compose(cycle, cycle);
                    cycles >>= 1;
                }
            }
            return new GroundPathKey { Position = Rotate(end.Position - start.Position, -start.Yaw), Yaw = end.Yaw - start.Yaw };
        }

        private static GroundPathKey Compose(GroundPathKey parent, GroundPathKey local) =>
            new GroundPathKey { Position = parent.Position + Rotate(local.Position, parent.Yaw), Yaw = parent.Yaw + local.Yaw };

        private static Vector2 Rotate(Vector2 v, float yaw)
        {
            float angle = yaw * Mathf.Deg2Rad;
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            return new Vector2(c * v.x + s * v.y, -s * v.x + c * v.y);
        }

        private static GroundPathKey Sample(GroundPathKey[] keys, float time)
        {
            int low = 0, high = keys.Length - 1;
            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (keys[mid].Seconds <= time) low = mid;
                else high = mid;
            }
            var a = keys[low];
            var b = keys[high];
            float weight = Mathf.InverseLerp(a.Seconds, b.Seconds, time);
            return new GroundPathKey { Position = Vector2.Lerp(a.Position, b.Position, weight), Yaw = Mathf.Lerp(a.Yaw, b.Yaw, weight) };
        }
    }
}
