using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>下身、上身依次只写自己的遮罩。默认姿势和共享动作各只有一条采样路径。</summary>
    internal sealed class CharacterBodyPlaybackGraph
    {
        private sealed class Blend
        {
            private readonly PlayableGraph graph;
            private readonly AvatarMask mask;
            private CharacterClipMixer parked;
            private readonly bool maskedStage, additive;
            public readonly Playable Node;
            public CharacterClipMixer Source { get; private set; }
            public float Weight { get; private set; }

            public Blend(PlayableGraph graph, Playable input, CharacterClipLayer bodyLayer)
            {
                this.graph = graph;
                maskedStage = bodyLayer != null;
                if (maskedStage)
                {
                    mask = bodyLayer.Mask;
                    additive = bodyLayer.Additive;
                    var mixer = AnimationLayerMixerPlayable.Create(graph, 2, false);
                    if (mask) mixer.SetLayerMaskFromAvatarMask(1, mask);
                    mixer.SetLayerAdditive(1, additive);
                    Node = mixer;
                }
                else Node = AnimationMixerPlayable.Create(graph, 1);
                Connect(graph, input, Node, 0);
                Node.SetInputWeight(0, 1f);
            }

            public bool Apply(CharacterClipPlayback clips, in CharacterAnimationLayerPlan plan,
                in CharacterAnimationPlaybackPlan frame, float dt, bool holdOutgoing = false, bool fullyCovered = false)
            {
                if (!clips.ReuseMixers && parked != null)
                {
                    parked.Release(false);
                    parked = null;
                }
                if (Source == null && !plan.HasState) return false;
                bool created = Source == null;
                if (created)
                {
                    Source = parked ?? new CharacterClipMixer(graph, clips.Counters);
                    parked = null;
                    if (Node.GetInputCount() == 1) Node.SetInputCount(2);
                    Connect(graph, Source.Mixer, Node, 1);
                }
                if (fullyCovered && !plan.HasState) Weight = 0f;
                else if (!holdOutgoing || plan.HasState) Weight = plan.WeightAfter(Weight, dt);
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

            public CharacterCurveSample MixPermission(CharacterCurveSample previous, AvatarMaskBodyPart part,
                string curve, float dt, string action, long instance)
            {
                if (Source == null || Weight <= 0f || mask && !mask.GetHumanoidBodyPartActive(part)) return previous;
                var sample = Source.Permission(curve, dt, action, instance);
                return additive ? new CharacterCurveSample(previous.Value + sample.Value * Weight,
                    action == null ? previous.Coverage : previous.Coverage + sample.Coverage * Weight) :
                    new CharacterCurveSample(Mathf.Lerp(previous.Value, sample.Value, Weight), Mathf.Lerp(previous.Coverage, sample.Coverage, Weight));
            }
        }

        private readonly CharacterClipMixer baseline;
        private readonly Blend lower, upper, handoff;
        public Playable Root => handoff.Node;

        public CharacterBodyPlaybackGraph(PlayableGraph graph, CharacterClipLayer lowerLayer, CharacterClipLayer upperLayer, CharacterPlaybackCounters counters)
        {
            baseline = new CharacterClipMixer(graph, counters);
            lower = new Blend(graph, baseline.Mixer, lowerLayer);
            upper = new Blend(graph, lower.Node, upperLayer);
            handoff = new Blend(graph, upper.Node, null); // 无共享动作时仅透传已合成的两部位姿势。
        }

        private static void Connect(PlayableGraph graph, Playable input, Playable output, int port)
        {
            if (!graph.Connect(input, 0, output, port)) throw new InvalidOperationException("无法连接角色身体姿势源。");
        }

        public CharacterAnimationPlaybackEmission Apply(CharacterClipPlayback clips, in CharacterAnimationPlaybackPlan frame, float dt)
        {
            var takeover = frame.Overlay;
            var timing = takeover.Demand.Timing;
            float nextCoverage = takeover.WeightAfter(handoff.Weight, dt);
            bool entering = takeover.HasState && (timing.Handoff.Valid && timing.SamplePhase < timing.Handoff.FullFrom ||
                takeover.LayerBlendSeconds > 0f && nextCoverage > handoff.Weight);
            bool covered = takeover.HasState && nextCoverage >= 1f;
            bool groundPlayed = clips.Advance(baseline, frame.FullBody, frame, dt);
            bool legsPlayed = lower.Apply(clips, frame.LowerBody, frame, dt, entering, covered);
            bool armsPlayed = upper.Apply(clips, frame.UpperBody, frame, dt, entering, covered);
            bool sharedPlayed = handoff.Apply(clips, takeover, frame, dt);
            return new CharacterAnimationPlaybackEmission(groundPlayed, legsPlayed, armsPlayed, sharedPlayed);
        }

        public CharacterClipMixer Source(CharacterAnimationBodyRegion source) => source switch
        {
            CharacterAnimationBodyRegion.FullBody => baseline,
            CharacterAnimationBodyRegion.LowerBody => lower.Source,
            CharacterAnimationBodyRegion.UpperBody => upper.Source,
            _ => handoff.Source
        };

        public float Weight(CharacterAnimationBodyRegion source) => source switch
        {
            CharacterAnimationBodyRegion.FullBody => 1f,
            CharacterAnimationBodyRegion.LowerBody => lower.Weight,
            CharacterAnimationBodyRegion.UpperBody => upper.Weight,
            _ => handoff.Weight
        };

        public CharacterCurveSample Permission(AvatarMaskBodyPart part, string curve, float dt, string action, long instance)
        {
            var value = baseline.Permission(curve, dt, action, instance);
            value = lower.MixPermission(value, part, curve, dt, action, instance);
            value = upper.MixPermission(value, part, curve, dt, action, instance);
            return handoff.MixPermission(value, part, curve, dt, action, instance);
        }
    }
}
