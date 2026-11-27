using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>下身、上身依次只写自己的遮罩。默认姿势和共享动作各只有一条采样路径。</summary>
    internal sealed class CharacterBodyPlaybackGraph
    {
        // 维护部位覆盖或共享全身动作的当前权重和片段混合器。
        private sealed class Blend
        {
            private readonly PlayableGraph graph;
            private readonly AvatarMask mask;
            private readonly Vector3 inertiaMask;
            private readonly CharacterLayerBlendWeight coverage;
            private CharacterClipMixer parked;
            private readonly bool maskedStage;
            private bool fadingOut;
            private float previousWeight;
            internal float ExitImpulse { get; private set; }
            private CharacterTransitionProfile exitProfile;
            public readonly Playable Node;
            public CharacterClipMixer Source { get; private set; }
            public float Weight { get; private set; }

            // 创建带 AvatarMask 的部位输出，或连接共享全身动作的交叉混合输出。
            public Blend(PlayableGraph graph, Playable input, AvatarMask mask, bool smoothWeight = false)
            {
                this.graph = graph;
                if (smoothWeight) coverage = new CharacterLayerBlendWeight();
                this.mask = mask;
                maskedStage = mask != null;
                if (maskedStage)
                {
                    var mixer = AnimationLayerMixerPlayable.Create(graph, 2, false);
                    mixer.SetLayerMaskFromAvatarMask(1, mask);
                    Node = mixer;
                }
                else Node = AnimationMixerPlayable.Create(graph, 1);
                inertiaMask = new Vector3(Writes(AvatarMaskBodyPart.Body) ? 1f : 0f,
                    Writes(AvatarMaskBodyPart.LeftArm) ? 1f : 0f, Writes(AvatarMaskBodyPart.RightArm) ? 1f : 0f);
                Connect(graph, input, Node, 0);
                Node.SetInputWeight(0, 1f);
            }

            // 图重建时从上次实际输出的层覆盖继续后续权重过渡。
            internal void RestoreWeight(float weight) => Weight = previousWeight = weight;

            private bool Writes(AvatarMaskBodyPart part) => !mask || mask.GetHumanoidBodyPartActive(part);

            // 记录推进前的层覆盖，为当前求值计算覆盖变化与卸层边沿。
            public void AdvanceWeight(in CharacterAnimationLayerPlan plan, float dt, bool holdOutgoing = false,
                bool fullyCovered = false, float secondsUntilCovered = 0f)
            {
                previousWeight = Weight;
                if (!plan.HasState && fullyCovered)
                {
                    Weight = 0f;
                    coverage?.Reset();
                    return;
                }
                if (!plan.HasState && holdOutgoing)
                {
                    if (coverage != null) Weight = coverage.Hold(Weight, dt, secondsUntilCovered);
                    return;
                }

                Weight = coverage != null ? coverage.Advance(Weight, plan, dt) : plan.WeightAfter(Weight, dt);
            }

            // 在卸下播放源前采集可见短退出，播放开始回执仍只由实际进入产生。
            public bool Apply(CharacterClipPlayback clips, in CharacterAnimationLayerPlan plan,
                in CharacterAnimationPlaybackPlan frame, float dt)
            {
                ExitImpulse = 0f;
                if (plan.HasState) fadingOut = false;
                else if (Source != null && previousWeight > Weight && !fadingOut)
                {
                    exitProfile = Source.ExitProfile;
                    ExitImpulse = (previousWeight - Weight) * (exitProfile?.Strength(plan.LayerBlendSeconds) ?? 0f);
                    fadingOut = true; // 平滑淡出只处理第一段实际变化，尾帧卸源不重复交接。
                }
                if (!clips.ReuseMixers && parked != null)
                {
                    parked.Release(false);
                    parked = null;
                }
                if (Source == null && !plan.HasState) return false;
                bool created = Source == null;
                if (created)
                {
                    Source = parked ?? new CharacterClipMixer(graph);
                    parked = null;
                    if (Node.GetInputCount() == 1) Node.SetInputCount(2);
                    Connect(graph, Source.Mixer, Node, 1);
                }
                Node.SetInputWeight(1, Weight);
                if (!maskedStage) Node.SetInputWeight(0, 1f - Weight);
                if (!plan.HasState && Weight == 0f)
                {
                    graph.Disconnect(Node, 1);
                    Source.Release(clips.ReuseMixers);
                    if (clips.ReuseMixers) parked = Source;
                    Source = null;
                    if (!maskedStage) Node.SetInputCount(1);
                    return false;
                }
                return clips.Advance(Source, plan, frame, dt, created);
            }

            // 卸下旧连续性的所有声部，保留混合节点及身体遮罩。
            internal void ClearMotionSources()
            {
                Source?.Release(true);
                Weight = previousWeight = ExitImpulse = 0f;
                fadingOut = false;
                coverage?.Reset();
                if (Node.GetInputCount() > 1) Node.SetInputWeight(1, 0f);
                Node.SetInputWeight(0, 1f);
            }

            // 按与姿态一致的身体遮罩和权重混合片段许可。
            public CharacterCurveSample MixPermission(CharacterCurveSample previous, AvatarMaskBodyPart part,
                string curve, float dt, string action, long? instance)
            {
                if (Source == null || Weight <= 0f || !Writes(part)) return previous;
                var sample = Source.Permission(curve, dt, action, instance);
                return new CharacterCurveSample(Mathf.Lerp(previous.Value, sample.Value, Weight),
                    Mathf.Lerp(previous.Coverage, sample.Coverage, Weight));
            }

            // 保留本层自己的进出掩码，其他层只按其真实覆盖减权，不在此阶段合并不同骨骼的调校。
            internal TransitionPart CollectTransition(ref TransitionSources sources, AvatarMaskBodyPart part, bool entered)
            {
                if (!Writes(part)) return default;
                if (Source != null) sources.Cover(Weight);
                if (entered && Source != null)
                {
                    var current = Source.EntryImpulse(part);
                    current.Weight *= Weight;
                    return current;
                }
                return ExitImpulse > 0f ? exitProfile.Part(part, ExitImpulse) : default;
            }

            // 只在当前层实际覆盖的骨骼分区中混合惯性抑制。
            public Vector3 MixInertiaSuppression(Vector3 previous)
            {
                if (Source == null || Weight <= 0f) return previous;
                Vector3 suppression = Source.InertiaSuppression();
                return previous + Vector3.Scale(inertiaMask, suppression - previous) * Weight;
            }
        }

        private readonly CharacterClipMixer baseline;
        private readonly Blend lower, upper, handoff;
        public Playable Root => handoff.Node;
        internal CharacterTransitionSignal Transition { get; private set; }

        // 按基础姿势、下身、上身和共享动作的顺序连接混合器。
        public CharacterBodyPlaybackGraph(PlayableGraph graph, CharacterClipLayer lowerLayer, CharacterClipLayer upperLayer)
        {
            baseline = new CharacterClipMixer(graph);
            lower = new Blend(graph, baseline.Mixer, lowerLayer.Mask);
            upper = new Blend(graph, lower.Node, upperLayer.Mask, smoothWeight: true);
            handoff = new Blend(graph, upper.Node, null, smoothWeight: true); // 无共享动作时仅透传已合成的两部位姿势。
        }

        // 恢复三处可见覆盖，帧间重绑仍由原蒙版和全身混合输出。
        internal void RestoreWeights(float[] weights)
        {
            lower.RestoreWeight(weights[(int)CharacterAnimationSource.LowerBody]);
            upper.RestoreWeight(weights[(int)CharacterAnimationSource.UpperBody]);
            handoff.RestoreWeight(weights[(int)CharacterAnimationSource.FullBodyOverlay]);
        }

        private static void Connect(PlayableGraph graph, Playable input, Playable output, int port)
        {
            if (!graph.Connect(input, 0, output, port)) throw new InvalidOperationException("无法连接角色身体姿势源。");
        }

        // 推进所有声部并分别发布实际进入回执与可见姿态切换。
        public CharacterAnimationPlaybackEmission Apply(CharacterClipPlayback clips, in CharacterAnimationPlaybackPlan frame, float dt)
        {
            var takeover = frame.Overlay;
            var timing = takeover.Demand.Timing;
            float previousCoverage = handoff.Weight;
            handoff.AdvanceWeight(takeover, dt);
            float nextCoverage = handoff.Weight;
            bool entering = takeover.HasState && (timing.HasWeight && !timing.Entered ||
                takeover.LayerBlendSeconds > 0f && nextCoverage > previousCoverage);
            bool covered = takeover.HasState && nextCoverage >= 1f;
            float untilCovered = timing.RemainingEntry;
            lower.AdvanceWeight(frame.LowerBody, dt, entering, covered);
            upper.AdvanceWeight(frame.UpperBody, dt, entering, covered, untilCovered);
            bool groundPlayed = clips.Advance(baseline, frame.FullBody, frame, dt);
            bool legsPlayed = lower.Apply(clips, frame.LowerBody, frame, dt);
            bool armsPlayed = upper.Apply(clips, frame.UpperBody, frame, dt);
            bool sharedPlayed = handoff.Apply(clips, takeover, frame, dt);
            var emission = new CharacterAnimationPlaybackEmission(groundPlayed, legsPlayed, armsPlayed, sharedPlayed);
            Transition = groundPlayed || legsPlayed || armsPlayed || sharedPlayed ||
                lower.ExitImpulse > 0f || upper.ExitImpulse > 0f || handoff.ExitImpulse > 0f
                ? ReadTransition(emission) : default;
            return emission;
        }

        // 舍弃旧动作和参考轨迹的片段时钟，保持现有播放图连接。
        internal void ClearMotionSources()
        {
            baseline.Release(true);
            lower.ClearMotionSources();
            upper.ClearMotionSources();
            handoff.ClearMotionSources();
            Transition = default;
        }

        // 查找已提交计划对应的混合源。
        public CharacterClipMixer Source(CharacterAnimationSource source) => source switch
        {
            CharacterAnimationSource.Baseline => baseline,
            CharacterAnimationSource.LowerBody => lower.Source,
            CharacterAnimationSource.UpperBody => upper.Source,
            _ => handoff.Source
        };

        // 读取当前已应用到图上的混合源权重。
        public float Weight(CharacterAnimationSource source) => source switch
        {
            CharacterAnimationSource.Baseline => 1f,
            CharacterAnimationSource.LowerBody => lower.Weight,
            CharacterAnimationSource.UpperBody => upper.Weight,
            _ => handoff.Weight
        };

        public CharacterCurveSample Permission(AvatarMaskBodyPart part, string curve, float dt, string action, long? instance)
        {
            var value = baseline.Permission(curve, dt, action, instance);
            value = lower.MixPermission(value, part, curve, dt, action, instance);
            value = upper.MixPermission(value, part, curve, dt, action, instance);
            return handoff.MixPermission(value, part, curve, dt, action, instance);
        }

        // 固定本次求值的进出边沿，不借用动作开始回执表示释放。
        private CharacterTransitionSignal ReadTransition(in CharacterAnimationPlaybackEmission emission) =>
            new CharacterTransitionSignal {
                Body = TransitionInfluence(AvatarMaskBodyPart.Body, emission), Head = TransitionInfluence(AvatarMaskBodyPart.Head, emission),
                LeftArm = TransitionInfluence(AvatarMaskBodyPart.LeftArm, emission), RightArm = TransitionInfluence(AvatarMaskBodyPart.RightArm, emission),
                LeftLeg = TransitionInfluence(AvatarMaskBodyPart.LeftLeg, emission), RightLeg = TransitionInfluence(AvatarMaskBodyPart.RightLeg, emission)
            };

        // 按与动画图相同的部位遮罩将实际播放边沿转成一次性惯性输入。
        private TransitionSources TransitionInfluence(AvatarMaskBodyPart part, in CharacterAnimationPlaybackEmission emission)
        {
            var sources = new TransitionSources { Baseline = emission.FullBody ? baseline.EntryImpulse(part) : default };
            var legs = lower.CollectTransition(ref sources, part, emission.LowerBody);
            sources.Lower = legs;
            var arms = upper.CollectTransition(ref sources, part, emission.UpperBody);
            sources.Upper = arms;
            var shared = handoff.CollectTransition(ref sources, part, emission.Overlay);
            sources.Overlay = shared;
            return sources;
        }

        public Vector3 InertiaAllowance()
        {
            Vector3 suppression = baseline.InertiaSuppression();
            suppression = lower.MixInertiaSuppression(suppression);
            suppression = upper.MixInertiaSuppression(suppression);
            suppression = handoff.MixInertiaSuppression(suppression);
            return new Vector3(1f - Mathf.Clamp01(suppression.x), 1f - Mathf.Clamp01(suppression.y),
                1f - Mathf.Clamp01(suppression.z));
        }
    }
}
