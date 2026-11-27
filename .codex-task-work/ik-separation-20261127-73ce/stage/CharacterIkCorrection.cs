using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 使用当前姿态与各来源候选制定 IK 计划，保留跨求值的目标过渡和地面接触状态。
    internal struct CharacterIkCorrection
    {
        [ReadOnly] public NativeArray<CharacterIkWeights> Weights;
        [ReadOnly] public NativeArray<CharacterIkInput> Inputs;
        [ReadOnly] public NativeArray<CharacterIkCandidate> Scenes;
        public NativeArray<CharacterIkState> States;
        public int LeftHand, RightHand;
        public CharacterFootPlantFrame FootPlant;
        public byte AdjustModelHeight;
        public float AppliedModelDrop, RemainingModelDrop;
        public TransformSceneHandle ProbeBasis;

        // 在惯性写入前采集手部握持影响和默认动画标记。
        public CharacterGripIk.HandReference ReadHand(AnimationStream stream, in CharacterIkSkeleton skeleton, int index)
        {
            if (index < 0) return default;
            var binding = skeleton.Bindings[index];
            if (!binding.IsReady(stream)) return default;
            var best = SelectTarget(stream, binding, Inputs[index], States[index]);
            return CharacterGripIk.ReadHand(stream, binding, Weights[index], best, States[index]);
        }

        // 将本段所有通道规划完毕后才交还姿态流程写骨；冻结时仅使失效链丢弃旧计划。
        public void BuildPlans(AnimationStream stream, CharacterIkSkeleton skeleton, in CharacterFootWarp warp,
            Vector3 rootPivot, Vector3 legPivot, in CharacterFootObservation feet, in CharacterGripIk.HandReference left,
            in CharacterGripIk.HandReference right, bool frozen)
        {
            for (int i = 0; i < skeleton.Bindings.Length; i++)
            {
                var binding = skeleton.Bindings[i];
                if (!binding.IsReady(stream))
                {
                    States[i] = default;
                    skeleton.Plans[i] = default;
                    continue;
                }
                if (frozen) continue;
                var weights = Weights[i];
                var input = Inputs[i];
                var state = States[i];
                var tip = binding.ReadTip(stream);
                var ground = CharacterGroundIk.Observe(stream, ProbeBasis, tip.position, warp, rootPivot, legPivot,
                    weights.IsFoot, ref input, ref state.Ground);
                var hand = i == LeftHand ? left : i == RightHand ? right : default;
                var best = hand.Selected ? hand.Best : SelectTarget(stream, binding, input, state);
                var plan = Resolve(stream, binding, weights, input, tip, ground, best, hand, feet, ref state);
                States[i] = state;
                skeleton.Plans[i] = plan;
            }
        }

        // 仲裁单通道的过渡并应用选中来源的目标规则，生成最终计划和下段承重需求。
        private CharacterIkPlan Resolve(AnimationStream stream, in CharacterIkBinding binding, in CharacterIkWeights weights,
            in CharacterIkInput input, in Pose tip, in CharacterGroundFootHit ground, in CharacterIkCandidate best,
            in CharacterGripIk.HandReference hand, in CharacterFootObservation feet, ref CharacterIkState state)
        {
            float gate = CharacterGripIk.Permission(best, weights.Permission, ref state);
            float step = CharacterIkDecision.BlendStep(weights, stream.deltaTime);
            bool groundedFoot = weights.IsFoot && state.Source == CharacterIkSource.Ground;
            bool changed = CharacterIkDecision.Advance(best, step, gate, groundedFoot,
                groundedFoot && best.Source == CharacterIkSource.Ground, ref state);
            if (changed || best.Source == CharacterIkSource.None) state.Ground.Release();
            float length = 0f;
            Vector3 hip = default;
            if (best.Source != CharacterIkSource.None)
            {
                var target = best.Target;
                if (best.Source == CharacterIkSource.Animation)
                {
                    target = binding.ReadMarker(stream);
                    hand.TransportDefault(tip, ref target);
                }
                else if (best.Source == CharacterIkSource.Ground)
                {
                    CharacterFootReach reach = default;
                    if (weights.IsFoot)
                    {
                        length = binding.Measure(stream, tip.position, out hip);
                        reach = new CharacterFootReach(hip, length, CharacterModelHeight.AllowsDrop(ground) ? RemainingModelDrop : 0f);
                    }
                    target = CharacterGroundIk.Place(best, ground, input.Probes.Up, tip, weights, FootPlant,
                        gate, state.Envelope, stream.deltaTime, reach, ref state.Ground);
                }
                CharacterIkDecision.BlendTarget(target, step, ref state);
            }
            var plan = CharacterIkDecision.Plan(weights, gate, state);
            if (AdjustModelHeight != 0 && weights.IsFoot)
                state.Ground.RequiredModelDrop = CharacterModelHeight.RequiredDrop(hip, length, weights.Limb,
                    state.Ground, plan, ground, feet, gate, AppliedModelDrop);
            return plan;
        }

        // 排除动画流中不可读取的标记，再选择该骨链的目标。
        private CharacterIkCandidate SelectTarget(AnimationStream stream, in CharacterIkBinding binding,
            CharacterIkInput input, in CharacterIkState state)
        {
            if (!binding.CanReadTarget(stream)) input.Animation = default;
            return CharacterIkDecision.Select(input, Scenes, state);
        }
    }
}
