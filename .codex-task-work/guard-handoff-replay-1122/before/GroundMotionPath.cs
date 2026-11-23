using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct GroundMotionCost
    {
        public readonly float Feet, Velocity, Contact, Path, Facing;
        public float Total => Feet + Velocity + Contact + Path + Facing;

        public GroundMotionCost(CharacterMotionTrack track, int index, in CharacterFootObservation feet,
            in CharacterMatchTrajectory goal, float speed)
        {
            var sample = track.Poses[index];
            float rate = track.IsLoop && track.ScalesWithSpeed ? speed / Mathf.Max(0.01f, track.ReferenceSpeed) : 1f;
            Feet = ((sample.Left - feet.Pose.Left).sqrMagnitude + (sample.Right - feet.Pose.Right).sqrMagnitude) / 0.20f;
            Velocity = ((sample.LeftVelocity * rate - feet.Pose.LeftVelocity).sqrMagnitude +
                (sample.RightVelocity * rate - feet.Pose.RightVelocity).sqrMagnitude) / 40f;
            Contact = feet.ContactCost(sample.Contact);
            float t = track.TimeAt(index);
            var near = GroundMotionPath.Relative(track, t, CharacterMatchTrajectory.NearSeconds * rate);
            var far = GroundMotionPath.Relative(track, t, CharacterMatchTrajectory.FarSeconds * rate);
            Path = ((near.Position - goal.Near).sqrMagnitude + (far.Position - goal.Far).sqrMagnitude) / 0.25f;
            float a = Mathf.DeltaAngle(near.Yaw, goal.NearYaw) / 45f;
            float b = Mathf.DeltaAngle(far.Yaw, goal.FarYaw) / 45f;
            Facing = a * a + b * b;
        }

#if UNITY_EDITOR
        public override string ToString() => $"合计 {Total:F2}（脚位 {Feet:F2} 脚速 {Velocity:F2} 接触 {Contact:F2} 路径 {Path:F2} 朝向 {Facing:F2}）";
#endif
    }

    internal static class GroundMotionPath
    {
        public static GroundPathKey Relative(CharacterMotionTrack track, float time, float horizon)
        {
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
