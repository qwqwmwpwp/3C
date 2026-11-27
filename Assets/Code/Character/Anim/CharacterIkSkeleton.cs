using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 保存用于动画流求解的骨链和可选目标标记。
    internal struct CharacterIkBinding
    {
        private byte valid, hasAnimationTarget; // 固定宽度标记，托管端和动画线程共用布局。
        public bool Valid { get => valid != 0; set => valid = (byte)(value ? 1 : 0); }
        public bool HasAnimationTarget { get => hasAnimationTarget != 0; set => hasAnimationTarget = (byte)(value ? 1 : 0); }
        public TransformStreamHandle Root, Mid, Tip, AnimationTarget;

        // 核对当前动画流中的骨链句柄是否仍可用。
        public bool IsReady(AnimationStream stream) => Valid && Root.IsValid(stream) && Mid.IsValid(stream) &&
            Tip.IsValid(stream);

        // 检查当前流中的动画标记是否有效。
        public bool CanReadTarget(AnimationStream stream) => HasAnimationTarget && AnimationTarget.IsValid(stream);

        // 读取当前骨链末端的世界姿态，供各修正来源共同使用。
        public Pose ReadTip(AnimationStream stream) => new Pose(Tip.GetPosition(stream), Tip.GetRotation(stream));

        // 读取本次动画流中标记的世界姿态。
        public Pose ReadMarker(AnimationStream stream) => new Pose(AnimationTarget.GetPosition(stream), AnimationTarget.GetRotation(stream));

        // 用修正后的当前姿态测量链长，供目标可达性判定使用。
        public float Measure(AnimationStream stream, Vector3 tip, out Vector3 root)
        {
            root = Root.GetPosition(stream);
            Vector3 mid = Mid.GetPosition(stream);
            return Vector3.Distance(root, mid) + Vector3.Distance(mid, tip);
        }
    }

    // 描述最终 IK 对单条骨链的世界目标及位置、旋转权重。
    internal struct CharacterIkPlan
    {
        public Pose Target;
        public float PositionWeight, RotationWeight;
    }

    // 绑定模型骨链，持有各链的求解计划，并集中把计划写回当前动画流。
    internal struct CharacterIkSkeleton
    {
        [ReadOnly] public NativeArray<CharacterIkBinding> Bindings;
        public NativeArray<CharacterIkPlan> Plans;

        // 为模型的全部通道分配固定索引，未装配的通道保留空绑定。
        public void Allocate(int count)
        {
            Bindings = new NativeArray<CharacterIkBinding>(count, Allocator.Persistent);
            Plans = new NativeArray<CharacterIkPlan>(count, Allocator.Persistent);
        }

        // 在 Animator 下绑定层级合法的三关节链，目标标记不能位于任何受驱动骨链中。
        public bool Bind(int index, Animator animator, in CharacterIkBones bones, CharacterBoundIkChannel[] channels)
        {
            if (!bones.Root || !bones.Mid || !bones.Tip || bones.Root == animator.transform ||
                bones.Root == bones.Mid || bones.Mid == bones.Tip || !bones.Root.IsChildOf(animator.transform) ||
                !bones.Mid.IsChildOf(bones.Root) || !bones.Tip.IsChildOf(bones.Mid)) return false;
            bool marker = bones.AnimationTarget && bones.AnimationTarget.IsChildOf(animator.transform);
            if (marker)
                foreach (var channel in channels)
                    if (channel.Bones.Root && bones.AnimationTarget.IsChildOf(channel.Bones.Root)) { marker = false; break; }
            Bindings[index] = new CharacterIkBinding { Valid = true, HasAnimationTarget = marker,
                Root = animator.BindStreamTransform(bones.Root), Mid = animator.BindStreamTransform(bones.Mid),
                Tip = animator.BindStreamTransform(bones.Tip),
                AnimationTarget = marker ? animator.BindStreamTransform(bones.AnimationTarget) : default };
            return true;
        }

        // 全部目标计划就绪后，按原绑定顺序求解并写回骨骼。
        public void Apply(AnimationStream stream)
        {
            for (int i = 0; i < Bindings.Length; i++)
            {
                var plan = Plans[i];
                if (plan.PositionWeight > 0f || plan.RotationWeight > 0f)
                    Solve(stream, Bindings[i], plan);
            }
        }

        // 播放图断开后释放骨链句柄与计划缓冲。
        public void Dispose()
        {
            if (Plans.IsCreated) Plans.Dispose();
            if (Bindings.IsCreated) Bindings.Dispose();
        }

        // 使用基础动画的弯曲平面，按余弦定理解中间关节，目标距离夹在骨链可达范围内。
        private static void Solve(AnimationStream stream, CharacterIkBinding chain, CharacterIkPlan plan)
        {
            Quaternion tipRotation = chain.Tip.GetRotation(stream);
            if (plan.PositionWeight > 0f)
            {
                Vector3 a = chain.Root.GetPosition(stream), b = chain.Mid.GetPosition(stream), c = chain.Tip.GetPosition(stream);
                Vector3 aim = Vector3.Lerp(c, plan.Target.position, plan.PositionWeight) - a;
                float upper = Vector3.Distance(a, b), lower = Vector3.Distance(b, c);
                if (upper > 0.0001f && lower > 0.0001f)
                {
                    Vector3 axis = aim.sqrMagnitude > 1e-10f ? aim.normalized : (b - a).normalized;
                    Vector3 animatedAxis = (c - a).normalized;
                    Vector3 bend = animatedAxis.sqrMagnitude > 0f
                        ? Quaternion.FromToRotation(animatedAxis, axis) * Vector3.ProjectOnPlane(b - a, animatedAxis)
                        : Vector3.ProjectOnPlane(b - a, axis);
                    if (bend.sqrMagnitude < 1e-10f)
                    {
                        Vector3 hint = chain.Root.GetRotation(stream) * Vector3.forward;
                        bend = Vector3.ProjectOnPlane(hint, axis);
                        if (bend.sqrMagnitude < 1e-10f)
                            bend = Vector3.Cross(axis, chain.Root.GetRotation(stream) * Vector3.up);
                    }
                    float distance = Mathf.Clamp(aim.magnitude, Mathf.Max(0.00001f, Mathf.Abs(upper - lower)), upper + lower);
                    float along = (upper * upper - lower * lower + distance * distance) / (2f * distance);
                    Vector3 joint = a + axis * along + bend.normalized * Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
                    chain.Root.SetRotation(stream, Quaternion.FromToRotation(b - a, joint - a) * chain.Root.GetRotation(stream));
                    b = chain.Mid.GetPosition(stream);
                    chain.Mid.SetRotation(stream, Quaternion.FromToRotation(chain.Tip.GetPosition(stream) - b,
                        a + axis * distance - b) * chain.Mid.GetRotation(stream));
                }
            }
            chain.Tip.SetRotation(stream, Quaternion.Slerp(tipRotation, plan.Target.rotation, plan.RotationWeight));
        }
    }
}
