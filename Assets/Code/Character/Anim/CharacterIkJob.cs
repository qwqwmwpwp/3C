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
        public byte ClipDriven;
        public CharacterIkLimb Limb;
        public bool IsFoot => Limb == CharacterIkLimb.LeftFoot || Limb == CharacterIkLimb.RightFoot;
        public float ClipPermission;
        public float PositionWeight, RotationWeight, BlendSeconds;

        public bool IsReady(AnimationStream stream) => Valid && Root.IsValid(stream) && Mid.IsValid(stream) &&
            Tip.IsValid(stream) && (ClipDriven != 0 || Curve.IsValid(stream));
        public bool CanReadTarget(AnimationStream stream) => HasAnimationTarget && AnimationTarget.IsValid(stream);
        public float Permission(AnimationStream stream)
        {
            float value = ClipDriven != 0 ? ClipPermission : Curve.GetFloat(stream);
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
        public float Gate, Envelope, Transition, RequiredModelDrop;
        private byte locked;
        public bool Locked { get => locked != 0; set => locked = (byte)(value ? 1 : 0); }
        public byte WasVisibleGrip;
        public CharacterFootProbeMotion ProbeMotion;
        public CharacterFootPlant FootPlant;
        public Pose Target, From, LocalAnchor;
        public CharacterIkPlan Plan;
    }

    /// <summary>只根据候选、许可和显式历史制定计划。这里保存锁点和切换进度，不写骨骼。</summary>
    internal static class CharacterIkDecision
    {
        private const float LockGate = 0.7f;
        private const float ReleaseGate = 0.3f;

        public static void Resolve(in CharacterIkBinding binding, in CharacterIkInput input, in CharacterGroundFootHit ground, float gate,
            in CharacterIkCandidate best, int mask, float deltaTime, in CharacterFootPlantFrame footPlant, Vector3 ankle, ref CharacterIkState state)
        {
            if (state.WasVisibleGrip != 0 && best.UsesVisibleGrip == 0)
            {
                state.Envelope = 0f;
                state.Source = CharacterIkSource.None; // 旧握点完全退出后，下个目标从自己的位置进入。
            }
            state.WasVisibleGrip = best.UsesVisibleGrip;
            if (best.UsesVisibleGrip != 0) gate = best.VisiblePermission;
            state.Gate = gate;
            state.CandidateMask = mask;
            state.SceneCount = input.SceneCount + (input.Scene.Source == CharacterIkSource.Scene ? 1 : 0);
            float step = BlendStep(binding, deltaTime);
            if (best.Source == CharacterIkSource.None)
            {
                state.Locked = false;
                state.FootPlant = default;
                state.Envelope = binding.IsFoot && state.Source == CharacterIkSource.Ground
                    ? 0f : Mathf.MoveTowards(state.Envelope, 0f, step);
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
                    bool changedGround = binding.IsFoot && state.Source == CharacterIkSource.Ground && best.Source == CharacterIkSource.Ground;
                    state.Transition = state.Envelope > 0f && !changedGround ? 0f : 1f;
                    state.Locked = false;
                    state.FootPlant = default;
                }
                state.Source = best.Source;
                state.Instance = best.Instance;
                state.ObjectId = best.ObjectId;
                state.Slot = best.Slot;
                state.Envelope = Mathf.MoveTowards(state.Envelope, 1f, step);
                Pose target = best.Target;
                if (binding.IsFoot && best.Source == CharacterIkSource.Ground)
                {
                    float positionWeight = binding.PositionWeight * gate * state.Envelope;
                    CharacterFootPlant.Place(ref state, footPlant, binding.Limb, ground, ankle, gate, positionWeight, deltaTime, ref target);
                }
                else if (best.Source == CharacterIkSource.Ground)
                {
                    // 非足部通道沿用完整的地面位置和旋转锁定。
                    if (!state.Locked && gate >= LockGate)
                    {
                        state.LocalAnchor = new Pose(ground.ToLocal.MultiplyPoint3x4(target.position),
                            Quaternion.Inverse(ground.Rotation) * target.rotation);
                        state.Locked = true;
                    }
                    if (gate <= ReleaseGate) state.Locked = false;
                    if (state.Locked)
                    {
                        float grip = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ReleaseGate, LockGate, gate));
                        target = new Pose(Vector3.Lerp(target.position,
                            ground.ToWorld.MultiplyPoint3x4(state.LocalAnchor.position), grip),
                            Quaternion.Slerp(target.rotation, ground.Rotation * state.LocalAnchor.rotation, grip));
                    }
                }
                state.Transition = Mathf.MoveTowards(state.Transition, 1f, step);
                state.Target = state.Transition >= 1f ? target : new Pose(
                    Vector3.Lerp(state.From.position, target.position, state.Transition),
                    Quaternion.Slerp(state.From.rotation, target.rotation, state.Transition));
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
        public CharacterFootPlantFrame FootPlant;
        public byte AdjustModelHeight;
        public float AppliedModelDrop;
        public TransformSceneHandle ProbeBasis;
#if UNITY_EDITOR
        public CharacterAnimationEvaluationCapture DebugCapture;
#endif
        private byte frozen;
        public bool Frozen { get => frozen != 0; set => frozen = (byte)(value ? 1 : 0); }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
#if UNITY_EDITOR
            bool capture = !Frozen && DebugCapture.Active;
            var frame = capture ? DebugCapture.Start(stream, Inertia, PoseBindings.Length > 0, Bindings) : default;
#endif
            if (!Frozen) Feet.Capture(stream);
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
#if UNITY_EDITOR
            if (capture) DebugCapture.ObserveAdditionalPoses(stream, ref frame, CharacterAnimationEvaluationCapture.PoseStage.Inertial);
#endif
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
                var state = States[i];
                var ground = default(CharacterGroundFootHit);
                Vector3 ankle = default;
#if UNITY_EDITOR
                int probeRay = -1;
#endif
                if (binding.IsFoot)
                {
                    ankle = binding.Tip.GetPosition(stream);
                    state.ProbeMotion.Observe(stream, ProbeBasis, ankle);
                    if (input.Ground.Source == CharacterIkSource.Ground)
#if UNITY_EDITOR
                        probeRay = input.Probes.Resolve(ankle, ref input.Ground, out ground);
#else
                        input.Probes.Resolve(ankle, ref input.Ground, out ground);
#endif
                }
                else if (input.Ground.Source == CharacterIkSource.Ground)
                {
                    ankle = binding.Tip.GetPosition(stream);
                    ground = input.Probes.Ray(0);
                }
                var hand = i == LeftHand ? left : i == RightHand ? right : default;
                int mask = hand.Mask;
                var best = hand.Selected ? hand.Best : SelectTarget(stream, binding, input, state, out mask);
                if (best.Source == CharacterIkSource.Animation)
                {
                    best.Target = new Pose(binding.AnimationTarget.GetPosition(stream), binding.AnimationTarget.GetRotation(stream));
                    hand.TransportDefault(stream, binding.Tip, ref best.Target);
                }
                else if (best.Source == CharacterIkSource.Ground)
                {
                    Vector3 up = input.Probes.Up;
                    float rise = Vector3.Dot(best.Target.position - ankle, ground.Normal) / Vector3.Dot(up, ground.Normal);
                    best.Target.position = ankle + up * rise;
                    best.Target.rotation *= binding.Tip.GetRotation(stream);
                }
                CharacterIkDecision.Resolve(binding, input, ground, binding.Permission(stream), best, mask, stream.deltaTime, FootPlant, ankle, ref state);
                if (AdjustModelHeight != 0 && binding.IsFoot)
                {
                    var feet = Feet.History.Length > 0 ? Feet.History[0] : default;
                    state.RequiredModelDrop = CharacterModelHeight.RequiredDrop(stream, binding, state, ground, feet, AppliedModelDrop);
                }
                States[i] = state;
#if UNITY_EDITOR
                if (capture) CharacterAnimationEvaluationCapture.ObservePlan(stream, ref frame, binding, input, state, best, ground, probeRay);
#endif
            }
            for (int i = 0; i < Bindings.Length; i++)
            {
                var plan = States[i].Plan;
                if (plan.PositionWeight > 0f || plan.RotationWeight > 0f)
                    Solve(stream, Bindings[i], plan);
            }
#if UNITY_EDITOR
            if (capture) DebugCapture.Complete(stream, ref frame, Bindings, Inputs, PoseBindings, PoseHistory, left.Grip, right.Grip);
#endif
        }

        private struct HandReference
        {
            public float Grip;
            public bool HasDefault, Selected;
            public CharacterIkCandidate Best;
            public int Mask;
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
            var state = States[index];
            var best = SelectTarget(stream, binding, Inputs[index], state, out int mask);
            var hand = new HandReference { Selected = true, Best = best, Mask = mask };
            if (best.Source == CharacterIkSource.Animation && best.ActionBound == 0)
            {
                hand.HasDefault = true;
                hand.Tip = new Pose(binding.Tip.GetPosition(stream), binding.Tip.GetRotation(stream));
                hand.Marker = new Pose(binding.AnimationTarget.GetPosition(stream), binding.AnimationTarget.GetRotation(stream));
            }
            else if (best.Source == CharacterIkSource.Animation || best.Source == CharacterIkSource.Scene)
            {
                float from = state.WasVisibleGrip != 0 && best.UsesVisibleGrip == 0 ? 0f : state.Envelope;
                float envelope = Mathf.MoveTowards(from, 1f, CharacterIkDecision.BlendStep(binding, stream.deltaTime));
                float permission = best.UsesVisibleGrip != 0 ? best.VisiblePermission : binding.Permission(stream) *
                    (best.Source == CharacterIkSource.Animation ? best.GripWeight : 1f);
                hand.Grip = permission * envelope * Mathf.Max(binding.PositionWeight, binding.RotationWeight);
            }
            return hand;
        }

        private CharacterIkCandidate SelectTarget(AnimationStream stream, in CharacterIkBinding binding,
            CharacterIkInput input, in CharacterIkState state, out int mask)
        {
            if (!binding.CanReadTarget(stream)) input.Animation = default;
            return CharacterIkDecision.Select(input, Scenes, state, out mask);
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
