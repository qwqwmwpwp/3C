using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>外部控制器已求解的一个运动步。位移是世界空间米，方向是世界 XZ 平面的移动意图。</summary>
    public struct CharacterAnimationMovement
    {
        public float DeltaTime;
        public Vector3 ActualDisplacement, ExpectedDisplacement, Facing;
        public Vector2 MoveDirection;
        public bool MoveApproved;
        public CharacterMoveGait Gait;
        public CollisionFlags Contacts;
        public CharacterGroundTrajectory Trajectory;
    }

    // 保存最近成功提交的外部固定步及其独立表现轨迹，供动画采样和配置重建恢复。
    internal sealed class ExternalAnimationMotion
    {
        internal CharacterControllerFrameResult Latest { get; private set; }
        internal CharacterGroundTrajectory LatestTrajectory { get; private set; }
        internal int Version { get; private set; }
        internal int StateVersion { get; private set; }
        internal bool Paused { get; private set; }
        private long sequence;
        private double elapsed, pauseStart, pausedSeconds;
        internal double Time => (Paused ? pauseStart : UnityEngine.Time.timeAsDouble) - pausedSeconds;
        private Dictionary<CharacterBehaviorId, double> grants;

        // 验证外部实测和移动许可，成功后同步更新最近固定步和该步的表现轨迹。
        internal bool Commit(in CharacterAnimationMovement motion)
        {
            if (Paused || motion.DeltaTime <= 0.000001f || !Finite(motion.DeltaTime) ||
                !Finite(motion.ActualDisplacement) || !Finite(motion.ExpectedDisplacement) || !Finite(motion.Facing) ||
                !Finite(motion.MoveDirection.x) || !Finite(motion.MoveDirection.y)) return false;
            bool previous = Latest.Behavior.MoveApproved;
            var id = motion.MoveApproved ? CharacterBehaviorId.Move : CharacterBehaviorId.None;
            var step = new CharacterBehaviorStep
            {
                Active = id, Executed = id, DisplacementProvider = id, OrdinarySpeedLimit = float.PositiveInfinity,
                GrantTimes = grants, StartSeconds = elapsed
            };
            if (motion.MoveApproved && !previous) step.RecordGrant(id);
            grants = step.GrantTimes;
            var expected = new Vector2(motion.ExpectedDisplacement.x, motion.ExpectedDisplacement.z);
            var behavior = new CharacterBehaviorFrameResult(++sequence, expected, motion.MoveApproved, previous, default, 0f, 0f, step);
            var input = new CharacterInputSnapshot(motion.MoveDirection, false, false, CharacterBehaviorId.None, motion.Gait);
            Latest = new CharacterControllerFrameResult(behavior, input, motion.DeltaTime, motion.ActualDisplacement,
                motion.Contacts, motion.Facing, Version);
            LatestTrajectory = motion.Trajectory;
            elapsed += motion.DeltaTime;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        // 暂停或恢复时切换模拟代次，清除上一代次的固定步、轨迹和移动许可。
        internal void SetPaused(bool paused)
        {
            if (Paused == paused) return;
            double now = UnityEngine.Time.timeAsDouble;
            if (paused) pauseStart = now;
            else pausedSeconds += now - pauseStart;
            Paused = paused;
            Version++;
            Latest = default;
            LatestTrajectory = default;
            grants = null;
        }

        // 重置外部输入的时间与序号，并丢弃上一状态的固定步和轨迹。
        internal void Reset()
        {
            StateVersion++;
            Version++;
            elapsed = 0d;
            sequence = 0;
            grants = null;
            Latest = default;
            LatestTrajectory = default;
        }
    }
}
