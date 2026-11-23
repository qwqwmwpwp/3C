using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct CharacterLayerObservation
    {
        public readonly AnimatorStateInfo Current, Next;
        public readonly bool InTransition;
        public readonly float BlendProgress;
        public AnimatorStateInfo Destination => InTransition ? Next : Current;
        public bool Ready => Current.fullPathHash != 0;

        public CharacterLayerObservation(Animator animator, int layer)
        {
            this = default;
            if (layer < 0 || layer >= animator.layerCount) return;
            Current = animator.GetCurrentAnimatorStateInfo(layer);
            InTransition = animator.IsInTransition(layer);
            if (!InTransition) return;
            Next = animator.GetNextAnimatorStateInfo(layer);
            BlendProgress = animator.GetAnimatorTransitionInfo(layer).normalizedTime;
        }
    }

    /// <summary>同一渲染帧、下发任何播放指令前取得的播放观测。</summary>
    internal readonly struct CharacterPlaybackObservation
    {
        public readonly CharacterLayerObservation Full, Lower, Upper;
        public readonly float SourceLength, DestinationClipLength;
        public readonly bool? LeftSupport;
        public readonly bool EarlyStance;

        public CharacterPlaybackObservation(CharacterLayerObservation full, CharacterLayerObservation lower,
            CharacterLayerObservation upper, float sourceLength, float destinationLength, bool? leftSupport, bool earlyStance)
        {
            Full = full;
            Lower = lower;
            Upper = upper;
            SourceLength = sourceLength;
            DestinationClipLength = destinationLength;
            LeftSupport = leftSupport;
            EarlyStance = earlyStance;
        }
    }

    internal sealed class CharacterPlaybackObserver
    {
        private readonly Animator animator;
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly List<AnimatorClipInfo> clips = new List<AnimatorClipInfo>(2);

        public CharacterPlaybackObserver(Animator animator, CharacterAnimationResourceCatalog resources)
        {
            this.animator = animator;
            this.resources = resources;
        }

        public CharacterPlaybackObservation Capture(CharacterLocomotionPhase phase, in CharacterAnimationLayerPlan previousBase)
        {
            if (!animator.isInitialized) return default;
            int fullLayer = resources.GetLayer(CharacterAnimationBodyRegion.FullBody);
            int lowerLayer = resources.GetLayer(CharacterAnimationBodyRegion.LowerBody);
            int upperLayer = resources.GetLayer(CharacterAnimationBodyRegion.UpperBody);
            var full = new CharacterLayerObservation(animator, fullLayer);
            var lower = lowerLayer != fullLayer && lowerLayer != upperLayer ? new CharacterLayerObservation(animator, lowerLayer) : default;
            var upper = upperLayer != fullLayer && lowerLayer != upperLayer ? new CharacterLayerObservation(animator, upperLayer) : default;
            bool releasing = phase == CharacterLocomotionPhase.BrakeLeadIn || phase == CharacterLocomotionPhase.Stopping;
            float sourceLength = 0f, destinationLength = 0f;
            bool enteringIdle = phase == CharacterLocomotionPhase.Idle && previousBase.Demand.Key != CharacterAnimationIntentConverter.IdleKey;
            bool readStop = releasing || previousBase.Binding.IsStop;
            if (full.Ready && (readStop || enteringIdle))
            {
                animator.GetCurrentAnimatorClipInfo(fullLayer, clips);
                float weight = 0f;
                foreach (var clip in clips)
                {
                    if (clip.clip == null) continue;
                    sourceLength += clip.weight * clip.clip.length;
                    weight += clip.weight;
                }
                if (weight > 0f) sourceLength /= weight;
                if (readStop && full.InTransition) animator.GetNextAnimatorClipInfo(fullLayer, clips);
                if (clips.Count > 0 && clips[0].clip != null) destinationLength = clips[0].clip.length;
            }
            bool? left = null;
            bool early = false;
            if (full.Ready && releasing)
            {
                var state = full.Current;
                bool mapped = resources.TryGetPhase(state.fullPathHash, out var gait);
                if (full.InTransition && (!mapped || full.BlendProgress >= 0.5f) &&
                    resources.TryGetPhase(full.Next.fullPathHash, out var nextGait))
                {
                    gait = nextGait;
                    state = full.Next;
                    mapped = true;
                }
                if (mapped)
                {
                    left = gait.IsLeftSupport(state.normalizedTime);
                    early = gait.IsEarlyStance(state.normalizedTime);
                }
                else if (previousBase.Demand.IsLocomotion && animator.isHuman)
                {
                    var a = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    var b = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                    if (a && b) left = Vector3.Dot(a.position - b.position, animator.transform.up) <= 0f;
                }
            }
            return new CharacterPlaybackObservation(full, lower, upper, sourceLength, destinationLength, left, early);
        }
    }
}
