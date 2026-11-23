using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 数组中的唯一元素由动画作业发布，下一次 Update 在提交播放命令之前读取。
    internal struct CharacterFootObservation
    {
        public CharacterFootPose Pose;
        public byte Count;
    }

    internal struct CharacterFootSampler
    {
        public NativeArray<CharacterFootObservation> History;
        public TransformStreamHandle Hips;
        public TransformSceneHandle Basis;
        public int Left, Right;

        public void Capture(AnimationStream stream, NativeArray<CharacterIkBinding> bindings)
        {
            if (History.Length == 0) return;
            var left = bindings[Left];
            var right = bindings[Right];
            if (!left.IsReady(stream) || !right.IsReady(stream) || !Hips.IsValid(stream) || !Basis.IsValid(stream))
            {
                History[0] = default;
                return;
            }
            float dt = stream.deltaTime;
            if (dt <= 0f) return;
            var before = History[0];
            Quaternion inverse = Quaternion.Inverse(Basis.GetRotation(stream));
            Vector3 hips = Hips.GetPosition(stream);
            var sample = new CharacterFootPose
            {
                Left = inverse * (left.Tip.GetPosition(stream) - hips),
                Right = inverse * (right.Tip.GetPosition(stream) - hips),
                Contact = new Vector2(left.Permission(stream), right.Permission(stream))
            };
            if (before.Count > 0)
            {
                sample.LeftVelocity = (sample.Left - before.Pose.Left) / dt;
                sample.RightVelocity = (sample.Right - before.Pose.Right) / dt;
            }
            History[0] = new CharacterFootObservation { Count = (byte)Mathf.Min(2, before.Count + 1), Pose = sample };
        }
    }
}
