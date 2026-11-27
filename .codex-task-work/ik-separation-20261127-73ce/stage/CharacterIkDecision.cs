using Unity.Collections;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存每个通道的配置权重与本次片段的实际许可。
    internal struct CharacterIkWeights
    {
        public CharacterIkLimb Limb;
        public float PositionWeight, RotationWeight, BlendSeconds, ClipPermission;
        public bool IsFoot => Limb == CharacterIkLimb.LeftFoot || Limb == CharacterIkLimb.RightFoot;
        public float Permission => ClipPermission > 0f ? Mathf.Min(ClipPermission, 1f) : 0f;
    }

    // 保存各通道的目标身份、过渡进度以及地面与握持的历史。
    internal struct CharacterIkState
    {
        public CharacterIkSource Source;
        public int ObjectId, Slot;
        public long Instance;
        public float Envelope, Transition;
        public byte WasVisibleGrip;
        public Pose Target, From;
        public CharacterGroundIkState Ground;
    }

    // 选出唯一目标，维护同目标的连续过渡，最后发布骨架可直接使用的权重和位姿。
    internal static class CharacterIkDecision
    {
        // 进入新目标或淡出旧目标，返回本次候选是否改变。
        public static bool Advance(in CharacterIkCandidate best, float step, float gate, bool releaseNow,
            bool directSwitch, ref CharacterIkState state)
        {
            if (best.Source == CharacterIkSource.None)
            {
                state.Envelope = releaseNow ? 0f : Mathf.MoveTowards(state.Envelope, 0f, step);
                if (gate <= 0f || state.Envelope <= 0f)
                {
                    state.Source = CharacterIkSource.None;
                    state.Envelope = 0f;
                }
                return false;
            }
            bool changed = !IsCurrent(best, state);
            if (changed)
            {
                state.From = state.Target;
                state.Transition = state.Envelope > 0f && !directSwitch ? 0f : 1f;
            }
            state.Source = best.Source;
            state.Instance = best.Instance;
            state.ObjectId = best.ObjectId;
            state.Slot = best.Slot;
            state.Envelope = Mathf.MoveTowards(state.Envelope, 1f, step);
            return changed;
        }

        // 将已修正的目标从切换起点过渡到当前位置。
        public static void BlendTarget(Pose target, float step, ref CharacterIkState state)
        {
            state.Transition = Mathf.MoveTowards(state.Transition, 1f, step);
            state.Target = state.Transition >= 1f ? target : new Pose(
                Vector3.Lerp(state.From.position, target.position, state.Transition),
                Quaternion.Slerp(state.From.rotation, target.rotation, state.Transition));
        }

        // 把目标和两种实际权重写成不依赖目标来源的计划。
        public static CharacterIkPlan Plan(in CharacterIkWeights weights, float gate, in CharacterIkState state)
        {
            float weight = gate * state.Envelope;
            return new CharacterIkPlan { Target = state.Target, PositionWeight = weights.PositionWeight * weight,
                RotationWeight = weights.RotationWeight * weight };
        }

        // 将本段持续时间换算成目标过渡进度。
        public static float BlendStep(in CharacterIkWeights binding, float deltaTime) =>
            binding.BlendSeconds > 0f ? Mathf.Max(0f, deltaTime) / binding.BlendSeconds : 1f;

        // 按优先级选出当前目标，同级优先保留上一帧目标。
        public static CharacterIkCandidate Select(in CharacterIkInput input, NativeArray<CharacterIkCandidate> scenes,
            in CharacterIkState state)
        {
            var best = default(CharacterIkCandidate);
            Consider(input.Animation, state, ref best);
            Consider(input.Scene, state, ref best);
            for (int i = 0; i < input.SceneCount; i++) Consider(scenes[input.SceneStart + i], state, ref best);
            Consider(input.Ground, state, ref best);
            return best;
        }

        // 同一来源、实例、对象和提供者才是持续中的同一目标。
        private static bool IsCurrent(in CharacterIkCandidate next, in CharacterIkState state) =>
            next.Source == state.Source && next.Instance == state.Instance && next.ObjectId == state.ObjectId && next.Slot == state.Slot;

        // 若新候选优先级更高或在同级中延续当前目标，就更新选中项。
        private static void Consider(CharacterIkCandidate next, in CharacterIkState state, ref CharacterIkCandidate best)
        {
            if (next.Source == CharacterIkSource.None) return;
            if (best.Source == CharacterIkSource.None || next.Priority > best.Priority ||
                (next.Priority == best.Priority && IsCurrent(next, state)))
                best = next;
        }
    }
}
