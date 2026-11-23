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

    internal enum CharacterPlaybackConfirmation { Untracked, Awaiting, Confirmed, Lost }

    internal readonly struct CharacterLayerObservation
    {
        public readonly CharacterClipProgress Current, Next;
        public readonly float SourceLength, DestinationLength;
        private readonly float? rawLength, currentClipLength;
        public readonly bool InTransition;
        public readonly CharacterPlaybackConfirmation Confirmation;
        public readonly float BlendProgress;
        public CharacterClipProgress Destination => InTransition ? Next : Current;
        public bool Ready => Current.fullPathHash != 0;

        public CharacterLayerObservation(CharacterClipProgress current, CharacterClipProgress next, bool fading, float blend,
            float sourceLength, float destinationLength, CharacterPlaybackConfirmation confirmation = default,
            float? rawLength = null, float? currentClipLength = null)
        {
            Current = current;
            Next = next;
            InTransition = fading;
            BlendProgress = blend;
            SourceLength = sourceLength;
            DestinationLength = destinationLength;
            Confirmation = confirmation;
            this.rawLength = rawLength;
            this.currentClipLength = currentClipLength;
        }

        internal CharacterLayerObservation ForPhase(bool readLengths, bool readNext)
        {
            if (!rawLength.HasValue || readNext) return this;
            return new CharacterLayerObservation(Current, Next, InTransition, BlendProgress,
                readLengths ? SourceLength : rawLength.Value, readLengths ? currentClipLength.GetValueOrDefault() : 0f, Confirmation);
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
        public readonly bool? LeftSupport;
        public readonly bool EarlyStance;

        internal CharacterPlaybackObservation ForPhase(CharacterLocomotionPhase phase, in CharacterAnimationLayerPlan previous)
        {
            bool release = phase == CharacterLocomotionPhase.BrakeLeadIn || phase == CharacterLocomotionPhase.Stopping;
            bool stop = release || previous.Binding.IsStop;
            bool enteringIdle = phase == CharacterLocomotionPhase.Idle && previous.Demand.Key != CharacterAnimationIntentConverter.IdleKey;
            return new CharacterPlaybackObservation(Full.ForPhase(stop || enteringIdle, stop), Lower, Upper, Overlay,
                release ? LeftSupport : null, release && EarlyStance);
        }

        public CharacterPlaybackObservation(CharacterLayerObservation full, CharacterLayerObservation lower,
            CharacterLayerObservation upper, CharacterLayerObservation overlay, bool? leftSupport, bool earlyStance)
        {
            Full = full;
            Lower = lower;
            Upper = upper;
            Overlay = overlay;
            LeftSupport = leftSupport;
            EarlyStance = earlyStance;
        }
    }

    internal sealed class CharacterPlaybackObserver
    {
        private readonly Animator animator;
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly CharacterAnimationPlayer playback;

        public CharacterPlaybackObserver(Animator animator, CharacterAnimationResourceCatalog resources, CharacterAnimationPlayer playback)
        {
            this.animator = animator;
            this.resources = resources;
            this.playback = playback;
        }

        public CharacterPlaybackObservation Capture(in CharacterAnimationLayerPlan previousBase,
            in CharacterFootObservation feet)
        {
            var backend = playback.Active;
            var full = backend.Observe(CharacterAnimationBodyRegion.FullBody, true, true);
            var lower = backend.Observe(CharacterAnimationBodyRegion.LowerBody);
            var upper = backend.Observe(CharacterAnimationBodyRegion.UpperBody);
            bool? left = null;
            bool early = false;
            if (full.Ready)
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
                bool ownsFeet = backend.LayerWeight(CharacterAnimationBodyRegion.LowerBody) > 0f ||
                    backend.LayerWeight(CharacterAnimationBodyRegion.FullBodyOverlay) > 0f;
                if (mapped && !ownsFeet)
                {
                    left = gait.IsLeftSupport(state.normalizedTime);
                    early = gait.IsEarlyStance(state.normalizedTime);
                }
                else if (previousBase.Demand.IsLocomotion && feet.Count > 0)
                {
                    var support = feet.Support;
                    left = support == GroundTrackSide.Either ? feet.Pose.Left.y <= feet.Pose.Right.y : support == GroundTrackSide.Left;
                }
                else if (previousBase.Demand.IsLocomotion && animator.isHuman)
                {
                    var a = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    var b = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                    if (a && b) left = Vector3.Dot(a.position - b.position, animator.transform.up) <= 0f;
                }
            }
            var overlay = backend.Observe(CharacterAnimationBodyRegion.FullBodyOverlay);
            return new CharacterPlaybackObservation(full: full, lower: lower, upper: upper, overlay: overlay,
                leftSupport: left, earlyStance: early);
        }
    }
}
