using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>从移动意图和地面预测确定本次可以比较的片段；播放中的片段也使用相同门槛。</summary>
    internal readonly struct GroundPoseQuery
    {
        internal readonly GroundMotionUse Use;
        internal readonly GroundTrackGait Target, From, Alternate;
        internal readonly float EntrySpeed, Angle, ArcYaw;

        internal GroundPoseQuery(GroundMotionUse use, GroundTrackGait target, float speed,
            float angle = 0f, GroundTrackGait from = GroundTrackGait.Any,
            GroundTrackGait alternate = GroundTrackGait.Any, float arcYaw = 0f)
        {
            Use = use;
            Target = target;
            From = from;
            Alternate = alternate;
            EntrySpeed = speed;
            Angle = angle;
            ArcYaw = arcYaw;
        }

        internal static GroundTrackGait GaitOf(string key) => key switch
        {
            CharacterAnimationIntentConverter.WalkKey => GroundTrackGait.Walk,
            CharacterAnimationIntentConverter.MoveKey => GroundTrackGait.Run,
            CharacterAnimationIntentConverter.SprintKey => GroundTrackGait.Sprint,
            _ => GroundTrackGait.Any
        };

        internal static GroundPoseQuery Moving(bool starting, CharacterMoveGait input, string fallback,
            CharacterMotionTrack source, CharacterAnimationMotion motion, float walkLimit, float sprintEntry)
        {
            var path = motion.Trajectory.Match;
            var target = GaitOf(fallback);
            float speed = motion.ActualSpeed;
            float future = path.Near.magnitude / CharacterMatchTrajectory.NearSeconds;
            bool fasterGait = future > walkLimit + 0.001f && motion.Trajectory.FutureVelocity.magnitude > walkLimit;
            var alternate = GroundTrackGait.Any;
            if (input == CharacterMoveGait.Walk) target = GroundTrackGait.Walk;
            else if (fasterGait)
            {
                target = input == CharacterMoveGait.Sprint && motion.Trajectory.FutureVelocity.magnitude >= sprintEntry
                    ? GroundTrackGait.Sprint : GroundTrackGait.Run;
                if (speed <= walkLimit) alternate = GroundTrackGait.Walk;
                else if (target == GroundTrackGait.Sprint && speed < sprintEntry) alternate = GroundTrackGait.Run;
            }
            else if (speed <= walkLimit && future <= walkLimit + 0.001f)
                target = GroundTrackGait.Walk;
            return new GroundPoseQuery(starting ? GroundMotionUse.Start : GroundMotionUse.Cycle, target, speed,
                from: source?.Gait ?? GroundTrackGait.Any, alternate: alternate, arcYaw: path.FarYaw);
        }

        internal bool AllowsGait(CharacterMotionTrack track) => track.Gait == GroundTrackGait.Any ||
            track.Gait == Target || track.Gait == Alternate && Alternate != GroundTrackGait.Any;

        internal bool AllowsUse(CharacterMotionTrack track)
        {
            if (Use == GroundMotionUse.Stop || Use == GroundMotionUse.Turn) return track.Use == Use;
            if (track.Use == GroundMotionUse.Transition)
                return track.FromGait == From && From != Target && track.Gait == Target;
            return track.Use == GroundMotionUse.Cycle || Use == GroundMotionUse.Start && track.Use == GroundMotionUse.Start;
        }

        internal bool AllowsHeading(CharacterMotionTrack track)
        {
            if (track.Use == GroundMotionUse.Turn)
                return (track.Side == GroundTrackSide.Either || (Angle < 0f) == (track.Side == GroundTrackSide.Left)) &&
                    (track.TurnDegrees <= 0f || Mathf.Abs(Mathf.Abs(Angle) - track.TurnDegrees) <= 55f);
            if (track.IsLoop && track.Side != GroundTrackSide.Either)
                return Mathf.Abs(ArcYaw) >= 6f && (ArcYaw < 0f) == (track.Side == GroundTrackSide.Left);
            return true;
        }

        internal float EntryPenalty(CharacterMotionTrack track)
        {
            // 从静止直接跳进匀速周期会丢掉蹬地；独立起步仍需通过脚姿、承重和轨迹比较。
            if (Use == GroundMotionUse.Start && track.IsLoop) return 1.5f;
            return 0f;
        }
    }
}

