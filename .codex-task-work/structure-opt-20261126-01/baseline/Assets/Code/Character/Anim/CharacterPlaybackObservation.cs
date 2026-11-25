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

    }

    // 保存一个片段混合源的当前姿态、淡入目标和对应长度。
    internal readonly struct CharacterLayerObservation
    {
        public readonly CharacterClipProgress Current, Next;
        public readonly float SourceLength, DestinationLength;
        public readonly bool InTransition;
        public readonly float BlendProgress;
        public CharacterClipProgress Destination => InTransition ? Next : Current;
        public bool Ready => Current.fullPathHash != 0;

        // 记录混合器当前播放和正在进入的片段。
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
    }

    /// <summary>同一渲染帧、下发任何播放指令前取得的播放观测。</summary>
    internal readonly struct CharacterPlaybackObservation
    {
        public readonly CharacterLayerObservation Full, Lower, Upper, Overlay;
        public readonly bool? LeftSupport;
        public readonly bool EarlyStance;

        // 仅在收步相关阶段把同一帧的承重脚观测交给地面选姿。
        internal CharacterPlaybackObservation ForPhase(CharacterLocomotionPhase phase)
        {
            bool release = phase == CharacterLocomotionPhase.BrakeLeadIn || phase == CharacterLocomotionPhase.Stopping;
            return new CharacterPlaybackObservation(Full, Lower, Upper, Overlay, release ? LeftSupport : null, release && EarlyStance);
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

    // 从已提交的片段和脚姿计算基础步态的承重脚及各覆盖源的播放进度。
    internal sealed class CharacterPlaybackObserver
    {
        private readonly Animator animator;
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly CharacterClipPlayback clips;

        // 绑定用于采样分层状态和脚姿的资源及片段图。
        public CharacterPlaybackObserver(Animator animator, CharacterAnimationResourceCatalog resources, CharacterClipPlayback clips)
        {
            this.animator = animator;
            this.resources = resources;
            this.clips = clips;
        }

        // 在新计划下发之前采样所有播放源和可用的脚姿。
        public CharacterPlaybackObservation Capture(in CharacterAnimationLayerPlan previousBase,
            in CharacterFootObservation feet)
        {
            var full = clips.Observe(CharacterAnimationSource.Baseline);
            var lower = clips.Observe(CharacterAnimationSource.LowerBody);
            var upper = clips.Observe(CharacterAnimationSource.UpperBody);
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
                bool ownsFeet = clips.LayerWeight(CharacterAnimationSource.LowerBody) > 0f ||
                    clips.LayerWeight(CharacterAnimationSource.FullBodyOverlay) > 0f;
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
            var overlay = clips.Observe(CharacterAnimationSource.FullBodyOverlay);
            return new CharacterPlaybackObservation(full: full, lower: lower, upper: upper, overlay: overlay,
                leftSupport: left, earlyStance: early);
        }
    }
}
