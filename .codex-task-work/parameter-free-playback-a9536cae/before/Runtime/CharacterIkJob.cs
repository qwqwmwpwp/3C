using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    internal struct CharacterIkBinding
    {
        private byte valid, hasAnimationTarget; // 固定宽度标记，托管端和动画线程共用布局。
        public bool Valid { get => valid != 0; set => valid = (byte)(value ? 1 : 0); }
        public bool HasAnimationTarget { get => hasAnimationTarget != 0; set => hasAnimationTarget = (byte)(value ? 1 : 0); }
        public TransformStreamHandle Root, Mid, Tip, AnimationTarget;
        public PropertyStreamHandle Curve;
        public float PositionWeight, RotationWeight, BlendSeconds;

        public bool IsReady(AnimationStream stream) => Valid && Root.IsValid(stream) && Mid.IsValid(stream) &&
            Tip.IsValid(stream) && Curve.IsValid(stream);
        public bool CanReadTarget(AnimationStream stream) => HasAnimationTarget && AnimationTarget.IsValid(stream);
        public float Permission(AnimationStream stream)
        {
            float value = Curve.GetFloat(stream);
            return value > 0f ? Mathf.Min(value, 1f) : 0f;
        }
    }

    internal struct CharacterIkPlan
    {
        public Pose Target;
        public float PositionWeight, RotationWeight;
    }

    internal struct CharacterIkState
    {
        public CharacterIkSource Source;
        public int CandidateMask, ObjectId, Slot, SceneCount;
        public long Instance;
        public float Gate, Envelope, Transition;
        private byte locked;
        public bool Locked { get => locked != 0; set => locked = (byte)(value ? 1 : 0); }
        public Pose Target, From, LocalAnchor;
        public CharacterIkPlan Plan;
    }

    /// <summary>只根据候选、许可和显式历史制定计划。这里保存锁点和切换进度，不写骨骼。</summary>
    internal static class CharacterIkDecision
    {
        private const float LockGate = 0.7f;
        private const float ReleaseGate = 0.3f;

        public static void Resolve(in CharacterIkBinding binding, in CharacterIkInput input, float gate,
            NativeArray<CharacterIkCandidate> scenes, float deltaTime, ref CharacterIkState state)
        {
            state.Gate = gate;
            var best = Select(input, scenes, state, out int mask);
            state.CandidateMask = mask;
            state.SceneCount = input.SceneCount + (input.Scene.Source == CharacterIkSource.Scene ? 1 : 0);
            float step = BlendStep(binding, deltaTime);
            if (best.Source == CharacterIkSource.None)
            {
                state.Locked = false;
                state.Envelope = Mathf.MoveTowards(state.Envelope, 0f, step);
                if (gate <= 0f || state.Envelope <= 0f)
                {
                    state.Source = CharacterIkSource.None;
                    state.Envelope = 0f;
                }
            }
            else
            {
                bool changed = !IsCurrent(best, state);
                if (changed)
                {
                    state.From = state.Target;
                    state.Transition = state.Envelope > 0f ? 0f : 1f;
                    state.Locked = false;
                }
                state.Source = best.Source;
                state.Instance = best.Instance;
                state.ObjectId = best.ObjectId;
                state.Slot = best.Slot;
                Pose target = best.Target;
                if (best.Source == CharacterIkSource.Ground)
                {
                    // 锁定与释放使用不同阈值，局部锁点随平台移动、旋转和缩放。
                    if (!state.Locked && gate >= LockGate)
                    {
                        state.LocalAnchor = new Pose(input.WorldToGround.MultiplyPoint3x4(target.position),
                            Quaternion.Inverse(input.GroundRotation) * target.rotation);
                        state.Locked = true;
                    }
                    if (gate <= ReleaseGate) state.Locked = false;
                    if (state.Locked)
                    {
                        // 卸重时从平台锁点平滑过渡到动画脚下的地面点。
                        float grip = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ReleaseGate, LockGate, gate));
                        target = new Pose(Vector3.Lerp(target.position,
                            input.GroundToWorld.MultiplyPoint3x4(state.LocalAnchor.position), grip),
                            Quaternion.Slerp(target.rotation, input.GroundRotation * state.LocalAnchor.rotation, grip));
                    }
                }
                state.Transition = Mathf.MoveTowards(state.Transition, 1f, step);
                state.Target = state.Transition >= 1f ? target : new Pose(
                    Vector3.Lerp(state.From.position, target.position, state.Transition),
                    Quaternion.Slerp(state.From.rotation, target.rotation, state.Transition));
                state.Envelope = Mathf.MoveTowards(state.Envelope, 1f, step);
            }
            float weight = gate * state.Envelope;
            state.Plan = new CharacterIkPlan { Target = state.Target, PositionWeight = binding.PositionWeight * weight,
                RotationWeight = binding.RotationWeight * weight };
        }

        public static float BlendStep(in CharacterIkBinding binding, float deltaTime) =>
            binding.BlendSeconds > 0f ? Mathf.Max(0f, deltaTime) / binding.BlendSeconds : 1f;

        public static CharacterIkCandidate Select(in CharacterIkInput input, NativeArray<CharacterIkCandidate> scenes,
            in CharacterIkState state, out int mask)
        {
            mask = 0;
            var best = default(CharacterIkCandidate);
            Consider(input.Animation, state, ref best, ref mask);
            Consider(input.Scene, state, ref best, ref mask);
            for (int i = 0; i < input.SceneCount; i++) Consider(scenes[input.SceneStart + i], state, ref best, ref mask);
            Consider(input.Ground, state, ref best, ref mask);
            return best;
        }

        private static bool IsCurrent(in CharacterIkCandidate next, in CharacterIkState state) =>
            next.Source == state.Source && next.Instance == state.Instance && next.ObjectId == state.ObjectId && next.Slot == state.Slot;

        private static void Consider(CharacterIkCandidate next, in CharacterIkState state, ref CharacterIkCandidate best,
            ref int mask)
        {
            if (next.Source == CharacterIkSource.None) return;
            mask |= 1 << (int)next.Source;
            if (best.Source == CharacterIkSource.None || next.Priority > best.Priority ||
                (next.Priority == best.Priority && IsCurrent(next, state)))
                best = next;
        }
    }

    /// <summary>先采集所有骨链的基础姿态并完成判断，再按绑定顺序写入同一动画流。</summary>
    internal struct CharacterIkJob : IAnimationJob
    {
        [ReadOnly] public NativeArray<CharacterIkBinding> Bindings;
        [ReadOnly] public NativeArray<CharacterIkInput> Inputs;
        [ReadOnly] public NativeArray<CharacterIkCandidate> Scenes;
        public NativeArray<CharacterIkState> States;
        [ReadOnly] public NativeArray<CharacterPoseBoneBinding> PoseBindings;
        public NativeArray<CharacterPoseBoneHistory> PoseHistory;
        public CharacterPoseInertiaFrame Inertia;
        public int LeftHand, RightHand;
        public CharacterFootSampler Feet;
        private byte frozen;
        public bool Frozen { get => frozen != 0; set => frozen = (byte)(value ? 1 : 0); }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            if (!Frozen) Feet.Capture(stream, Bindings);
            var left = default(HandReference);
            var right = default(HandReference);
            if (PoseBindings.Length > 0)
            {
                if (!Frozen && Inertia.Strength > 0f)
                {
                    left = ReadHand(stream, LeftHand);
                    right = ReadHand(stream, RightHand);
                }
                CharacterPoseInertia.Apply(stream, PoseBindings, PoseHistory, Inertia, left.Grip, right.Grip, Frozen);
            }
            for (int i = 0; i < Bindings.Length; i++)
            {
                var binding = Bindings[i];
                if (!binding.IsReady(stream))
                {
                    States[i] = default;
                    continue;
                }
                if (Frozen) continue;
                var input = Inputs[i];
                if (input.Animation.Source != CharacterIkSource.None)
                {
                    if (binding.CanReadTarget(stream))
                    {
                        input.Animation.Target = new Pose(binding.AnimationTarget.GetPosition(stream),
                            binding.AnimationTarget.GetRotation(stream));
                        if (i == LeftHand) left.TransportDefault(stream, binding.Tip, ref input.Animation.Target);
                        if (i == RightHand) right.TransportDefault(stream, binding.Tip, ref input.Animation.Target);
                    }
                    else input.Animation = default;
                }
                if (input.Ground.Source != CharacterIkSource.None)
                {
                    // 探针负责确定地面；接触目标沿角色竖轴投影本次动画脚踝，保留步幅和站姿宽度。
                    Vector3 foot = binding.Tip.GetPosition(stream);
                    float rise = Vector3.Dot(input.Ground.Target.position - foot, input.GroundNormal) /
                        Vector3.Dot(input.ProbeUp, input.GroundNormal);
                    input.Ground.Target.position = foot + input.ProbeUp * rise;
                    input.Ground.Target.rotation *= binding.Tip.GetRotation(stream);
                }
                var state = States[i];
                CharacterIkDecision.Resolve(binding, input, binding.Permission(stream), Scenes, stream.deltaTime, ref state);
                States[i] = state;
            }
            for (int i = 0; i < Bindings.Length; i++)
            {
                var plan = States[i].Plan;
                if (plan.PositionWeight > 0f || plan.RotationWeight > 0f)
                    Solve(stream, Bindings[i], plan);
            }
        }

        private struct HandReference
        {
            public float Grip;
            public bool HasDefault;
            public Pose Tip, Marker;

            public void TransportDefault(AnimationStream stream, TransformStreamHandle tip, ref Pose target)
            {
                if (!HasDefault) return;
                Vector3 position = tip.GetPosition(stream);
                Quaternion rotation = tip.GetRotation(stream);
                if (position.Equals(Tip.position) && rotation.Equals(Tip.rotation)) return;
                // 保留手端到根节点标记的同帧偏差，避免满权重 IK 抹去自由手的惯性。
                Quaternion change = rotation * Quaternion.Inverse(Tip.rotation);
                target = new Pose(position + change * (Marker.position - Tip.position), change * Marker.rotation);
            }
        }

        private HandReference ReadHand(AnimationStream stream, int index)
        {
            if (index < 0) return default;
            var binding = Bindings[index];
            if (!binding.IsReady(stream)) return default;
            var input = Inputs[index];
            if (!binding.CanReadTarget(stream)) input.Animation = default;
            var state = States[index];
            var best = CharacterIkDecision.Select(input, Scenes, state, out _);
            if (best.Source == CharacterIkSource.Animation && best.ActionBound == 0)
                return new HandReference { HasDefault = true,
                    Tip = new Pose(binding.Tip.GetPosition(stream), binding.Tip.GetRotation(stream)),
                    Marker = new Pose(binding.AnimationTarget.GetPosition(stream), binding.AnimationTarget.GetRotation(stream)) };
            if (best.Source != CharacterIkSource.Scene && best.Source != CharacterIkSource.Animation) return default;
            float envelope = Mathf.MoveTowards(state.Envelope, 1f, CharacterIkDecision.BlendStep(binding, stream.deltaTime));
            return new HandReference { Grip = binding.Permission(stream) * envelope *
                Mathf.Max(binding.PositionWeight, binding.RotationWeight) *
                (best.Source == CharacterIkSource.Animation ? best.GripWeight : 1f) };
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
