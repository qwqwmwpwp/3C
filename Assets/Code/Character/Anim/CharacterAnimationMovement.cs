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

    internal sealed class ExternalAnimationMotion
    {
        internal CharacterControllerFrameResult Latest { get; private set; }
        internal int Version { get; private set; }
        internal int StateVersion { get; private set; }
        internal bool Paused { get; private set; }
        private long sequence;
        private double elapsed;
        private Dictionary<CharacterBehaviorId, double> grants;

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
                motion.Contacts, motion.Facing, Version, motion.Trajectory);
            elapsed += motion.DeltaTime;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        internal void SetPaused(bool paused)
        {
            if (Paused == paused) return;
            Paused = paused;
            Version++;
            Latest = default;
            grants = null;
        }

        internal void Reset()
        {
            StateVersion++;
            Version++;
            elapsed = 0d;
            sequence = 0;
            grants = null;
            Latest = default;
        }
    }
}
