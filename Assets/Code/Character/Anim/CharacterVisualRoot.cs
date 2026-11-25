using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 主线程累计在播地面素材与实际模型运动的短时差，只发布有限的水平表现修正。
    internal sealed class CharacterVisualRoot
    {
        private readonly float distance, yawLimit, followSpeed, turnSpeed, releaseSeconds;
        private readonly VisualRootPolicy[] policies = new VisualRootPolicy[(int)VisualRootPhase.Missing + 1];
        private Vector3 previousPosition, previousForward;
        private bool continuous;
        internal Vector3 Offset { get; private set; }
        internal float Yaw { get; private set; }

        // 保存该角色使用的视觉根限制。
        internal CharacterVisualRoot(VisualRootModule settings)
        {
            distance = settings.Distance;
            yawLimit = settings.Yaw;
            followSpeed = settings.FollowSpeed;
            turnSpeed = settings.TurnSpeed;
            releaseSeconds = settings.Release;
            BindingPath = settings.RootPath;
            for (int i = 0; i < policies.Length; i++) policies[i] = settings.For((VisualRootPhase)i)?.Snapshot();
        }

        internal string BindingPath { get; }
#if UNITY_EDITOR
        internal VisualRootDebug Debug;
#endif

        // 根据本帧实际素材增量、模型运动和阶段策略分别计算有限水平位移与偏航。
        internal void Advance(CharacterClipPlayback clips, in CharacterAnimationMotion motion, Transform model, float dt)
        {
            Vector3 position = model.position;
            Vector3 forward = Vector3.ProjectOnPlane(model.forward, Vector3.up).normalized;
            var step = clips.ReferenceStep(out float coverage, out var use);
            var phase = Phase(clips, motion, use, coverage);
            bool valid = phase <= VisualRootPhase.Turn && forward.sqrMagnitude > 0.9f && dt > 0f && dt <= 0.25f;
            bool moving = motion.ActualSpeed > 0.02f || continuous && Vector3.Angle(previousForward, forward) > 0.1f;
            var policy = policies[(int)phase];
            var positionMode = Resolve(policy?.Position, valid && moving);
            var rotationMode = Resolve(policy?.Rotation, valid && moving);
            float release = Mathf.Exp(-Mathf.Max(0f, dt) / Mathf.Max(0.01f, policy?.ReleaseSeconds ?? releaseSeconds));
            Vector3 actual = continuous ? Vector3.ProjectOnPlane(position - previousPosition, Vector3.up) : Vector3.zero;
            float turn = continuous ? Vector3.SignedAngle(previousForward, forward, Vector3.up) : 0f;
            Vector3 facing = Quaternion.AngleAxis(Yaw, Vector3.up) * previousForward;
            Vector3 reference = facing * step.Position.y + Vector3.Cross(Vector3.up, facing) * step.Position.x;
            Vector3 error = continuous && valid ? reference - actual * coverage : Vector3.zero;
            float angular = continuous && valid ? step.Yaw - turn * coverage : 0f;
            float maxDistance = Mathf.Max(0f, distance) * Mathf.Clamp01(policy?.DistanceShare ?? 1f);
            float maxYaw = Mathf.Max(0f, yawLimit) * Mathf.Clamp01(policy?.YawShare ?? 1f);
            Vector3 next = positionMode switch {
                VisualRootMode.Fixed => Vector3.zero, VisualRootMode.Release => Offset * release,
                VisualRootMode.Accumulate => Offset + error,
                _ => Offset * release + Vector3.ClampMagnitude(error, Mathf.Max(0f, followSpeed) * dt) };
            float yawStep = Mathf.Max(0f, turnSpeed) * Mathf.Max(0f, dt);
            float nextYaw = rotationMode switch {
                VisualRootMode.Fixed => 0f, VisualRootMode.Release => Yaw * release, VisualRootMode.Accumulate => Yaw + angular,
                _ => Yaw * release + Mathf.Clamp(angular, -yawStep, yawStep) };
            Offset = Vector3.ClampMagnitude(next, maxDistance);
            Yaw = Mathf.Clamp(nextYaw, -maxYaw, maxYaw);
#if UNITY_EDITOR
            Debug = new VisualRootDebug { Bound = true, Binding = string.IsNullOrEmpty(BindingPath) ? "Hips" : BindingPath,
                Reason = !valid ? "无可比较的连续运动" : !moving ? "真实运动静止" : !continuous ? "建立连续基准" : "按阶段策略",
                ActualYaw = turn, ReferenceYaw = step.Yaw, Phase = phase, PositionMode = positionMode, RotationMode = rotationMode,
                Actual = actual, Reference = reference, Offset = Offset, Yaw = Yaw, Limited = next.sqrMagnitude > maxDistance * maxDistance || Mathf.Abs(nextYaw) > maxYaw };
#endif
            previousPosition = position;
            previousForward = forward;
            continuous = valid;
        }

        // 不可比较的参考运动只允许固定或释放，防止跨中断累计增量。
        private static VisualRootMode Resolve(VisualRootMode? selected, bool valid) =>
            valid ? selected ?? VisualRootMode.Follow : selected == VisualRootMode.Fixed ? VisualRootMode.Fixed : VisualRootMode.Release;

        // 先应用物理与覆盖条件，地面用途取实际贡献最大的在播声部。
        private static VisualRootPhase Phase(CharacterClipPlayback clips, in CharacterAnimationMotion motion, GroundMotionUse use, float coverage)
        {
            if (!motion.IsValid) return VisualRootPhase.Missing;
            if (motion.IsBlocked || motion.Trajectory.IsBrakeObstructed) return VisualRootPhase.Blocked;
            if ((motion.CollisionFlags & CollisionFlags.Below) == 0) return VisualRootPhase.Airborne;
            if (clips.LayerWeight(CharacterAnimationSource.FullBodyOverlay) >= 0.999f ||
                clips.LayerWeight(CharacterAnimationSource.LowerBody) >= 0.999f) return VisualRootPhase.Covered;
            if (coverage <= 0f) return VisualRootPhase.Missing;
            return use switch { GroundMotionUse.Start => VisualRootPhase.Start, GroundMotionUse.Stop => VisualRootPhase.Stop,
                GroundMotionUse.Turn => VisualRootPhase.Turn, _ => VisualRootPhase.Cycle };
        }

        // 丢弃瞬移、重绑和暂停之前的根运动基准。
        internal void Reset()
        {
            continuous = false;
#if UNITY_EDITOR
            Debug = default;
#endif
            Offset = Vector3.zero;
            Yaw = 0f;
        }
    }

#if UNITY_EDITOR
    // 发布本帧根参考运动、实际模型运动、预算限制及释放阶段。
    internal struct VisualRootDebug
    {
        internal bool Bound, Limited;
        internal string Binding, Reason;
        internal VisualRootPhase Phase;
        internal VisualRootMode PositionMode, RotationMode;
        internal Vector3 Actual, Reference, Offset;
        internal float Yaw, ActualYaw, ReferenceYaw;
    }
#endif

    // 在动画流的人形骨盆施加水平表现偏移，不修改模型、角色控制器或物理根。
    internal struct CharacterVisualRootBone
    {
        private TransformStreamHandle hips;
        private byte bound;
        internal Vector3 Offset;
        internal float Yaw;
        internal bool Bound => bound != 0;

        // 只绑定人形模型内部可独立写入的骨盆。
        internal static CharacterVisualRootBone Bind(Animator animator, CharacterVisualRoot visual, Transform owner)
        {
            if (visual == null || !animator.isHuman) return default;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var bone = string.IsNullOrWhiteSpace(visual.BindingPath) ? hips : animator.transform.Find(visual.BindingPath);
            if (!hips || !bone || bone == animator.transform || bone == owner || !bone.IsChildOf(animator.transform) ||
                !hips.IsChildOf(bone) || owner.IsChildOf(bone) || bone.GetComponentInChildren<Rigidbody>(true) ||
                bone.GetComponentInChildren<Collider>(true))
            {
                Debug.LogWarning($"视觉根路径 {visual.BindingPath} 需要指向模型内部包含髋骨且不含物理组件的骨骼。", animator);
                return default;
            }
            return new CharacterVisualRootBone { bound = 1, hips = animator.BindStreamTransform(bone) };
        }

        // 给出实际写入的旋转中心和位移，供后续足部探测还原输入坐标。
        internal Quaternion Apply(AnimationStream stream, out Vector3 pivot, out Vector3 applied)
        {
            pivot = applied = default;
            if (!Bound || !hips.IsValid(stream)) return Quaternion.identity;
            pivot = hips.GetPosition(stream);
            applied = Offset;
            var rotation = Quaternion.AngleAxis(Yaw, Vector3.up);
            hips.SetPosition(stream, pivot + Offset);
            hips.SetRotation(stream, rotation * hips.GetRotation(stream));
            return rotation;
        }
    }
}
