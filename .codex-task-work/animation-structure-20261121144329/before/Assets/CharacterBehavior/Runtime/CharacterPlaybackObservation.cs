using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct CharacterClipProgress
    {
        public readonly int fullPathHash;
        public readonly float normalizedTime;
        public readonly bool loop;

        public CharacterClipProgress(int hash, float progress, bool looping)
        {
            fullPathHash = hash;
            normalizedTime = progress;
            loop = looping;
        }

        public CharacterClipProgress(AnimatorStateInfo state) : this(state.fullPathHash, state.normalizedTime, state.loop) { }
    }

    internal readonly struct CharacterLayerObservation
    {
        public readonly CharacterClipProgress Current, Next;
        public readonly float SourceLength, DestinationLength;
        public readonly bool InTransition;
        public readonly float BlendProgress;
        public CharacterClipProgress Destination => InTransition ? Next : Current;
        public bool Ready => Current.fullPathHash != 0;

        public CharacterLayerObservation(CharacterClipProgress current, CharacterClipProgress next, bool fading, float blend,
            float sourceLength, float destinationLength)
        {
            Current = current;
            Next = next;
            InTransition = fading;
            BlendProgress = blend;
            SourceLength = sourceLength;
            DestinationLength = destinationLength;
        }

        public CharacterLayerObservation(Animator animator, int layer)
        {
            this = default;
            if (layer < 0 || layer >= animator.layerCount) return;
            var source = animator.GetCurrentAnimatorStateInfo(layer);
            Current = new CharacterClipProgress(source);
            SourceLength = source.length;
            InTransition = animator.IsInTransition(layer);
            if (!InTransition) return;
            Next = new CharacterClipProgress(animator.GetNextAnimatorStateInfo(layer));
            BlendProgress = animator.GetAnimatorTransitionInfo(layer).normalizedTime;
        }
    }

    /// <summary>同一渲染帧、下发任何播放指令前取得的播放观测。</summary>
    internal readonly struct CharacterPlaybackObservation
    {
        public readonly CharacterLayerObservation Full, Lower, Upper, Overlay;
        public readonly float SourceLength, DestinationClipLength;
        public readonly bool? LeftSupport;
        public readonly bool EarlyStance;

        public CharacterPlaybackObservation(CharacterLayerObservation full, CharacterLayerObservation lower,
            CharacterLayerObservation upper, CharacterLayerObservation overlay, float sourceLength, float destinationLength, bool? leftSupport, bool earlyStance)
        {
            Full = full;
            Lower = lower;
            Upper = upper;
            Overlay = overlay;
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
        private readonly CharacterClipPlayback playback;
        private readonly List<AnimatorClipInfo> clips = new List<AnimatorClipInfo>(2);

        public CharacterPlaybackObserver(Animator animator, CharacterAnimationResourceCatalog resources, CharacterClipPlayback playback)
        {
            this.animator = animator;
            this.resources = resources;
            this.playback = playback;
        }

        private CharacterLayerObservation Read(int layer) => playback.Available
            ? playback.Observe(layer) : new CharacterLayerObservation(animator, layer);

        public CharacterPlaybackObservation Capture(CharacterLocomotionPhase phase, in CharacterAnimationLayerPlan previousBase)
        {
            if (!playback.Available && !animator.isInitialized) return default;
            int fullLayer = resources.GetLayer(CharacterAnimationBodyRegion.FullBody);
            int lowerLayer = resources.GetLayer(CharacterAnimationBodyRegion.LowerBody);
            int upperLayer = resources.GetLayer(CharacterAnimationBodyRegion.UpperBody);
            var full = Read(fullLayer);
            var lower = lowerLayer != fullLayer && lowerLayer != upperLayer ? Read(lowerLayer) : default;
            var upper = upperLayer != fullLayer && lowerLayer != upperLayer ? Read(upperLayer) : default;
            bool releasing = phase == CharacterLocomotionPhase.BrakeLeadIn || phase == CharacterLocomotionPhase.Stopping;
            float sourceLength = full.SourceLength, destinationLength = full.DestinationLength;
            bool enteringIdle = phase == CharacterLocomotionPhase.Idle && previousBase.Demand.Key != CharacterAnimationIntentConverter.IdleKey;
            bool readStop = releasing || previousBase.Binding.IsStop;
            if (!playback.Available && full.Ready && (readStop || enteringIdle))
            {
                animator.GetCurrentAnimatorClipInfo(fullLayer, clips);
                float weight = 0f, weightedLength = 0f;
                foreach (var clip in clips)
                {
                    if (clip.clip == null) continue;
                    weightedLength += clip.weight * clip.clip.length;
                    weight += clip.weight;
                }
                if (weight > 0f) sourceLength = weightedLength / weight;
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
            var overlay = Read(resources.GetLayer(CharacterAnimationBodyRegion.FullBodyOverlay));
            return new CharacterPlaybackObservation(full, lower, upper, overlay, sourceLength, destinationLength, left, early);
        }
    }
}
