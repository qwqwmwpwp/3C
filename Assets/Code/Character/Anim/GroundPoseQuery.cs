using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>从移动意图和地面预测确定本次可以比较的片段；播放中的片段也使用相同门槛。</summary>
    internal readonly struct GroundPoseQuery
    {
        internal readonly GroundMotionUse Use;
        internal readonly GroundRuleSelection Selection;
        internal readonly GroundTrackGait Target, From, Alternate;
        internal readonly float EntrySpeed, Angle;

        // 固定本次行为用途、允许的步态和入场状态。
        internal GroundPoseQuery(GroundMotionUse use, GroundTrackGait target, float speed,
            float angle = 0f, GroundTrackGait from = GroundTrackGait.Any,
            GroundTrackGait alternate = GroundTrackGait.Any)
        {
            Selection = default;
            Use = use;
            Target = target;
            From = from;
            Alternate = alternate;
            EntrySpeed = speed;
            Angle = angle;
        }

        // 保留基础合法条件并附上条件表得出的范围与特征配置。
        internal GroundPoseQuery(in GroundPoseQuery source, GroundRuleSelection selection)
        { this = source; Selection = selection; }

        internal static GroundTrackGait GaitOf(string key) => key switch
        {
            CharacterLocomotionKeys.WalkKey => GroundTrackGait.Walk,
            CharacterLocomotionKeys.MoveKey => GroundTrackGait.Run,
            CharacterLocomotionKeys.SprintKey => GroundTrackGait.Sprint,
            _ => GroundTrackGait.Any
        };

        // 根据当前与目标步态确定统一查询用途，并保留实际输入的步态门槛。
        internal static GroundPoseQuery Moving(bool starting, AnimationGait input, string fallback,
            CharacterMotionTrack source, CharacterAnimationMotion motion, float walkLimit, float sprintEntry)
        {
            var path = motion.Trajectory.Match;
            var target = GaitOf(fallback);
            float speed = motion.ActualSpeed;
            float future = path.Near.magnitude / path.Windows.x;
            bool fasterGait = future > walkLimit + 0.001f && motion.Trajectory.FutureVelocity.magnitude > walkLimit;
            var alternate = GroundTrackGait.Any;
            if (input == AnimationGait.Walk) target = GroundTrackGait.Walk;
            else if (fasterGait)
            {
                target = input == AnimationGait.Sprint && motion.Trajectory.FutureVelocity.magnitude >= sprintEntry
                    ? GroundTrackGait.Sprint : GroundTrackGait.Run;
                if (speed <= walkLimit) alternate = GroundTrackGait.Walk;
                else if (target == GroundTrackGait.Sprint && speed < sprintEntry) alternate = GroundTrackGait.Run;
            }
            else if (speed <= walkLimit && future <= walkLimit + 0.001f)
                target = GroundTrackGait.Walk;
            var use = starting ? GroundMotionUse.Start : source != null && source.Gait != GroundTrackGait.Any &&
                source.Gait != target ? GroundMotionUse.Transition : GroundMotionUse.Cycle;
            return new GroundPoseQuery(use, target, speed, path.FarYaw,
                from: source?.Gait ?? GroundTrackGait.Any, alternate: alternate);
        }

        internal bool AllowsGait(CharacterMotionTrack track) => track.Gait == GroundTrackGait.Any ||
            track.Gait == Target || track.Gait == Alternate && Alternate != GroundTrackGait.Any;

        // 保留收步、转向和步态交接的行为权限。
        internal bool AllowsUse(CharacterMotionTrack track)
        {
            if (Use == GroundMotionUse.Stop || Use == GroundMotionUse.Turn) return track.Use == Use;
            if (track.Use == GroundMotionUse.Transition)
                return track.FromGait == From && From != Target && track.Gait == Target;
            return track.Use == GroundMotionUse.Cycle || Use == GroundMotionUse.Start && track.Use == GroundMotionUse.Start;
        }

        // 合法转向按有符号目标角付出软代价，循环弧线由实际近远路径评分。
        internal float EntryPenalty(CharacterMotionTrack track)
        {
            if (track.Use == GroundMotionUse.Turn && track.Side != GroundTrackSide.Either)
            {
                float target = track.Side == GroundTrackSide.Left ? -track.TurnDegrees : track.TurnDegrees;
                float delta = Mathf.DeltaAngle(Angle, target) / 60f;
                return delta * delta;
            }
            // 从静止直接跳进匀速周期会丢掉蹬地；独立起步仍需通过脚姿、承重和轨迹比较。
            if (Use == GroundMotionUse.Start && track.IsLoop) return 1.5f;
            return 0f;
        }
    }
}

