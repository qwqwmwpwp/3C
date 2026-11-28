using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 从固定步读取世界空间的完整三维运动，速度单位为米每秒，加速度单位为米每二次方秒。
    public readonly struct CharacterBasePoseMotion
    {
        public readonly bool IsValid, HasAcceleration;
        public readonly Vector3 ActualVelocity, ExpectedVelocity, ActualAcceleration;
        public readonly Vector3 Facing, ExpectedFacing;
        public readonly CollisionFlags CollisionFlags;
        public float ActualSpeed => ActualVelocity.magnitude;
        public float ExpectedSpeed => ExpectedVelocity.magnitude;

        // 取三维原始观测，保留高度、垂直速度和俯仰方向。
        internal CharacterBasePoseMotion(in CharacterAnimationMotion source)
        {
            IsValid = source.IsValid;
            HasAcceleration = source.HasAcceleration;
            ActualVelocity = source.WorldVelocity;
            ExpectedVelocity = source.WorldExpectedVelocity;
            ActualAcceleration = source.WorldAcceleration;
            Facing = source.WorldFacing;
            ExpectedFacing = source.WorldExpectedFacing;
            CollisionFlags = source.CollisionFlags;
        }
    }

    // 表示本次选姿时已分配给动作的身体部位和对应覆盖权重。
    public readonly struct CharacterBasePoseOccupancy
    {
        public readonly string UpperKey, LowerKey;
        public readonly float UpperWeight, LowerWeight;

        // 从本次仲裁结果冻结上下身持有者与权重。
        public CharacterBasePoseOccupancy(string upperKey, string lowerKey, float upperWeight, float lowerWeight)
        {
            UpperKey = upperKey;
            LowerKey = lowerKey;
            UpperWeight = upperWeight;
            LowerWeight = lowerWeight;
        }
    }

    // 供策略实例只读查询的固定步或渲染观测；所有对象均为冻结的托管数据。
    public readonly struct CharacterBasePoseInput
    {
        internal readonly CharacterAnimationMovement Frame;
        internal readonly CharacterAnimationMotion RawMotion;
        public long FrameNumber => Frame.FrameNumber;
        public int SimulationVersion => Frame.SimulationVersion;
        public readonly double Time;
        public readonly float DeltaTime;
        public float StepSeconds => Frame.DeltaTime;
        public Vector3 ActualDisplacement => Frame.ActualDisplacement;
        public Vector3 ExpectedDisplacement => Frame.ExpectedDisplacement;
        public Vector2 MoveDirection => Frame.MoveDirection;
        public AnimationGait Gait => Frame.Gait;
        public readonly CharacterAnimationContext Context;
        public CharacterBasePoseMotion Motion => new CharacterBasePoseMotion(RawMotion);
        public readonly CharacterTrajectoryObservations Trajectory;
        public IAnimationTrajectorySnapshot Forecast => Trajectory.Snapshot; // 支持动态时刻的不可变、后台只读查询。
        public readonly CharacterPlaybackObservation Playback;
        public readonly CharacterBasePoseOccupancy Occupancy;
        public readonly CharacterBoneObservations Bones;

        // 从同一批次的运动、轨迹、播放和骨骼快照提取后台可用的值。
        internal CharacterBasePoseInput(in CharacterAnimationMovement frame, in CharacterAnimationContext context,
            in CharacterAnimationMotion motion, in CharacterTrajectoryObservations trajectory, double time, float deltaTime,
            in CharacterPlaybackObservation playback, in CharacterBasePoseOccupancy occupancy, CharacterBoneObservations bones)
        {
            Frame = frame;
            Frame.GroundProjection = null; // 原步投影已由宿主完成，只交付固定值和发布的冻结轨迹。
            Context = context;
            RawMotion = motion;
            Trajectory = trajectory;
            Time = time;
            DeltaTime = deltaTime;
            Playback = playback;
            Occupancy = occupancy;
            Bones = bones;
        }
    }
}
